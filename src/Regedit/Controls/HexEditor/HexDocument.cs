// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Controls;

/// <summary>
/// A byte-editing draft over a readable, seekable stream. Keep the stream open and its contents stable
/// for the lifetime of the document. Only edited spans and one source page are retained in memory.
/// </summary>
public sealed partial class HexDocument
{
    private const int PageSize = 8192;
    private readonly record struct Piece(long Start, long Offset, long Length, byte[]? Edits);

    private readonly Stream _source;
    private readonly List<Piece> _pieces = [];
    private readonly byte[] _page = new byte[PageSize];
    private long _pageOffset = -1;
    private byte[] _editBlock = [];
    private int _used;
    private readonly long _sourceLength;

    public long Length { get; private set; }

    /// <summary>Every editor sharing this document must preserve its original byte count.</summary>
    public bool IsFixedLength { get; }

    internal long Revision { get; private set; }

    internal readonly record struct Change(long Offset, long Removed, long Inserted)
    {
        internal long MapPosition(long position) => position < this.Offset ? position :
            position - this.Offset >= this.Removed ? position - this.Removed + this.Inserted :
            this.Offset + Math.Min(position - this.Offset, this.Inserted);

        internal bool Replaces(long position) => position >= this.Offset && position - this.Offset < this.Removed;
    }

    internal Change LastChange { get; private set; }

    internal event EventHandler<Change>? ViewEdited;

    internal void NotifyEdit(object origin) => this.ViewEdited?.Invoke(origin, this.LastChange);

    public HexDocument(Stream source) : this(source, isFixedLength: false)
    {
    }

    public HexDocument(Stream source, bool isFixedLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The source must support reading and seeking.", nameof(source));
        }

        this._source = source;
        this.IsFixedLength = isFixedLength;
        this.Length = this._sourceLength = source.Length;
        if (this.Length > 0)
        {
            this._pieces.Add(new Piece(0, 0, this.Length, null));
        }
    }

    /// <summary>Reads up to the end of the draft. The source stream's position can change.</summary>
    public int Read(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, this.Length);
        var count = (int)Math.Min(destination.Length, this.Length - offset);
        var written = 0;
        for (var index = this.FindPiece(offset); written < count; index++)
        {
            var piece = this._pieces[index];
            var within = offset - piece.Start;
            var take = (int)Math.Min(piece.Length - within, count - written);
            var target = destination.Slice(written, take);
            if (piece.Edits is { } edits)
            {
                edits.AsSpan((int)(piece.Offset + within), take).CopyTo(target);
            }
            else
            {
                this.ReadSource(piece.Offset + within, target);
            }

            written += take;
            offset += take;
        }

        return count;
    }

    private void ReadSource(long offset, Span<byte> target)
    {
        if (target.Length >= PageSize)
        {
            this._source.Position = offset;
            this._source.ReadExactly(target);
            return;
        }

        while (!target.IsEmpty)
        {
            var page = offset - (offset % PageSize);
            var length = (int)Math.Min(PageSize, this._sourceLength - page);
            if (page != this._pageOffset)
            {
                this._pageOffset = -1;
                this._source.Position = page;
                this._source.ReadExactly(this._page.AsSpan(0, length));
                this._pageOffset = page;
            }

            var within = (int)(offset - page);
            var take = Math.Min(target.Length, length - within);
            this._page.AsSpan(within, take).CopyTo(target);
            target = target[take..];
            offset += take;
        }
    }

    /// <summary>Streams the draft to an independent destination. As with Read, serialize access to the document while copying.</summary>
    public void CopyTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || ReferenceEquals(destination, this._source))
        {
            throw new ArgumentException("Use a writable destination separate from the source.", nameof(destination));
        }

        var buffer = new byte[65536];
        for (long offset = 0; offset < this.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = this.Read(offset, buffer);
            destination.Write(buffer, 0, read);
            offset += read;
        }
    }

    /// <summary>Replaces a range in the draft without writing to or shifting the source stream.</summary>
    internal void Replace(long offset, long remove, ReadOnlySpan<byte> insert, bool finishByte = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, this.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(remove);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remove, this.Length - offset);
        if (this.IsFixedLength && remove != insert.Length)
        {
            throw new InvalidOperationException("This document has a fixed length.");
        }

        if (remove == 0 && insert.IsEmpty)
        {
            return;
        }

        _ = checked(this.Length - remove + insert.Length);
        var change = new Change(offset, remove, insert.Length);
        var inverse = finishByte ? null : this.CaptureInverse(change);
        var first = this.FindPiece(offset);
        if (first < this._pieces.Count && remove == insert.Length)
        {
            var piece = this._pieces[first];
            var within = offset - piece.Start;
            if (piece.Edits is { } edits && remove <= piece.Length - within)
            {
                insert.CopyTo(edits.AsSpan((int)(piece.Offset + within)));
                this.RecordEdit(change, inverse, finishByte);
                return;
            }
        }

        var editsToInsert = new Piece[(int)(((long)insert.Length + PageSize - 1) / PageSize)];
        for (var i = 0; i < editsToInsert.Length; i++)
        {
            var count = Math.Min(PageSize, insert.Length);
            editsToInsert[i] = this.StoreEdit(offset, insert[..count]);
            offset += count;
            insert = insert[count..];
        }

        this.Splice(change, editsToInsert);
        this.RecordEdit(change, inverse, finishByte);
    }

    private void Splice(Change change, Piece[] insert)
    {
        var first = this.Split(change.Offset);
        var end = this.Split(change.Offset + change.Removed);
        this._pieces.RemoveRange(first, end - first);
        this._pieces.InsertRange(first, insert);
        var after = first + insert.Length;
        if (insert.Length > 0 && this.TryExtendPrevious(first, this._pieces[first]))
        {
            this._pieces.RemoveAt(first);
            after--;
        }

        var delta = change.Inserted - change.Removed;
        if (delta != 0)
        {
            for (var i = after; i < this._pieces.Count; i++)
            {
                var piece = this._pieces[i];
                this._pieces[i] = piece with { Start = piece.Start + delta };
            }
        }

        if (after < this._pieces.Count && this.TryExtendPrevious(after, this._pieces[after]))
        {
            this._pieces.RemoveAt(after);
        }

        this.Length += delta;
        if (this.Length == 0)
        {
            this._editBlock = [];
            this._used = 0;
        }
    }

    private Piece StoreEdit(long offset, ReadOnlySpan<byte> insert)
    {
        if (insert.Length >= PageSize)
        {
            return new Piece(offset, 0, insert.Length, insert.ToArray());
        }

        if (insert.Length > this._editBlock.Length - this._used)
        {
            this._editBlock = new byte[PageSize];
            this._used = 0;
        }

        var piece = new Piece(offset, this._used, insert.Length, this._editBlock);
        insert.CopyTo(this._editBlock.AsSpan(this._used));
        this._used += insert.Length;
        return piece;
    }

    private bool TryExtendPrevious(int index, Piece next)
    {
        if (index == 0)
        {
            return false;
        }

        var previous = this._pieces[index - 1];
        if (previous.Edits != next.Edits || previous.Offset + previous.Length != next.Offset)
        {
            return false;
        }

        this._pieces[index - 1] = previous with { Length = previous.Length + next.Length };
        return true;
    }

    private int FindPiece(long offset)
    {
        var low = 0;
        var high = this._pieces.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            var piece = this._pieces[mid];
            if (offset >= piece.Start + piece.Length)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int Split(long offset)
    {
        var index = this.FindPiece(offset);
        if (index == this._pieces.Count)
        {
            return index;
        }

        var piece = this._pieces[index];
        if (offset != piece.Start)
        {
            var left = offset - piece.Start;
            this._pieces[index] = piece with { Length = left };
            this._pieces.Insert(++index, new Piece(offset, piece.Offset + left, piece.Length - left, piece.Edits));
        }

        return index;
    }
}
