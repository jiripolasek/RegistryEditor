// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Controls;

/// <summary>Validates the entire input, returns its byte count, and copies the prefix that fits in destination. Return -1 for invalid input.</summary>
public delegate int HexPasteReader(string text, Span<byte> destination);

internal sealed class HexEditorBuffer
{
    private long _anchor;
    private bool _fixedLength;
    private long _highDigitRevision;

    public HexDocument? Document { get; private set; }

    public long Length => this.Document?.Length ?? 0;

    public long Caret { get; private set; }

    // The current byte stays highlighted even when there is no range to replace.
    public bool HasSelection { get; private set; }

    public long SelectionStart => Math.Min(this._anchor, this.Caret);

    public long SelectionLength
    {
        get
        {
            var last = Math.Max(this._anchor, this.Caret);
            return (last < this.Length ? last + 1 : this.Length) - this.SelectionStart;
        }
    }

    public bool LowNibble { get; private set; }

    public bool IsReadOnly { get; private set; }

    public bool CanResize => !this.IsReadOnly && !this._fixedLength && this.Document is { IsFixedLength: false };

    public bool CanWrite => !this.IsReadOnly && (this.Length > 0 || this.CanResize);

    public long MaxLength { get; set; }

    private long GrowthAvailable => (this.MaxLength == 0 ? long.MaxValue : Math.Max(this.Length, this.MaxLength)) - this.Length;

    public long LastPosition => this.CanResize ? this.Length : Math.Max(0, this.Length - 1);

    public bool CanWriteByte(long offset) => this.CanWrite
                                             && offset >= 0
                                             && (offset < this.Length
                                                 || (offset == this.Length && this.CanResize && this.GrowthAvailable > 0));

    public void Load(HexDocument? document)
    {
        this.Document = document;
        this.MoveTo(0);
    }

    public void SetEditOptions(bool readOnly, bool fixedLength)
    {
        this.IsReadOnly = readOnly;
        this._fixedLength = fixedLength;
        this._anchor = Math.Min(this._anchor, this.LastPosition);
        this.MoveTo(this.Caret, extend: this.Length > 0);
    }

    public void ApplyExternalChange(HexDocument.Change change)
    {
        var last = this.LastPosition;
        var caret = change.MapPosition(this.Caret);
        var anchor = change.MapPosition(this._anchor);
        var firstDeleted = change.Offset + change.Inserted;
        var upper = Math.Max(this.Caret, this._anchor);
        if (this.HasSelection && upper >= firstDeleted && change.Replaces(upper))
        {
            if (this.SelectionStart >= firstDeleted)
            {
                this.HasSelection = false;
            }
            else if (upper == this.Caret)
            {
                caret = firstDeleted - 1;
            }
            else
            {
                anchor = firstDeleted - 1;
            }
        }

        this.LowNibble &= !change.Replaces(this.Caret);
        this.Caret = Math.Min(caret, last);
        this._anchor = Math.Min(anchor, last);
        this.HasSelection &= this.SelectionLength > 0;
    }

    public void MoveTo(long position, bool extend = false)
    {
        position = Math.Clamp(position, 0, this.LastPosition);
        if (!extend)
        {
            this._anchor = position;
            this.HasSelection = false;
        }
        else if (position != this.Caret)
        {
            this.HasSelection = position != this._anchor;
        }

        this.Caret = position;
        this.LowNibble = false;
    }

    public void MoveBy(long delta, bool extend)
    {
        this.MoveTo(this.Caret + Math.Clamp(delta, -this.Caret, this.LastPosition - this.Caret), extend);
    }

    public void Select(long offset, long length)
    {
        if (offset < 0 || offset >= this.Length || length <= 0)
        {
            return;
        }

        this._anchor = offset + (Math.Min(length, this.Length - offset) - 1);
        this.MoveTo(offset, extend: true);
        this.HasSelection = true;
    }

    public bool Type(char character, bool ascii)
    {
        var offset = this.Caret;
        if (!this.CanWriteByte(offset) || this.Document is null || (ascii ? character is < ' ' or > '~' : !char.IsAsciiHexDigit(character)))
        {
            return false;
        }

        Span<byte> value = stackalloc byte[1];
        var read = this.Document.Read(offset, value);
        if (ascii)
        {
            value[0] = (byte)character;
        }
        else
        {
            var digit = HexDigit(character);
            var previous = read == 0 ? 0 : value[0];
            value[0] = (byte)(this.LowNibble ? (previous & 0xF0) | digit : (digit << 4) | (previous & 0x0F));
        }

        var advance = ascii || this.LowNibble;
        var finishByte = !ascii && this.LowNibble && this._highDigitRevision == this.Document.Revision;
        this.Document.Replace(offset, read, value, finishByte);
        this.MoveTo(advance ? offset + 1 : offset);
        this.LowNibble = !advance;
        this._highDigitRevision = this.Document.Revision;
        return true;
    }

    public bool CanReplayHistory(bool redo) => !this.IsReadOnly
                                               && this.Document?.NextHistoryChange(redo) is { } change
                                               && (change.Inserted == change.Removed
                                                   || (this.CanResize && change.Inserted - change.Removed <= this.GrowthAvailable));

    public bool ReplayHistory(bool redo)
    {
        if (!this.CanReplayHistory(redo))
        {
            return false;
        }

        this.Document!.ReplayHistory(redo);
        var change = this.Document.LastChange;
        this.MoveTo(change.Offset);
        if (change.Inserted > 0)
        {
            this.Select(change.Offset, change.Inserted);
        }

        return true;
    }

    public bool Delete(bool backspace)
    {
        if (!this.CanResize || this.Document is null)
        {
            return false;
        }

        var offset = this.SelectionStart;
        var length = this.SelectionLength;
        if (backspace && !this.HasSelection)
        {
            if (offset == 0)
            {
                return false;
            }

            offset--;
            length = 1;
        }

        if (length == 0)
        {
            return false;
        }

        this.Document.Replace(offset, length, []);
        this.MoveTo(offset);
        return true;
    }

    public bool TryPaste(string text, bool ascii, out string? message, bool overwrite = false, HexPasteReader? readCustom = null)
    {
        message = null;
        if (!this.CanWrite || this.Document is null || text.Length == 0)
        {
            return false;
        }

        var byteCount = ascii ? (text.AsSpan().ContainsAnyExceptInRange(' ', '~') ? 0 : text.Length) : CountHexBytes(text);
        if (ascii || byteCount > 0)
        {
            readCustom = null;
        }
        else if (readCustom is not null)
        {
            byteCount = readCustom(text, []);
        }

        if (byteCount <= 0)
        {
            message = ascii ? "Paste printable ASCII characters." : "Paste complete hexadecimal byte pairs.";
            return false;
        }

        var offset = this.SelectionStart;
        var overwriteExisting = overwrite || !this.CanResize;
        if (overwriteExisting && byteCount > this.Length - offset)
        {
            message = "The pasted bytes would extend past the end of the document.";
            return false;
        }

        var remove = overwriteExisting ? byteCount : this.HasSelection ? this.SelectionLength : 0;
        var count = (int)Math.Min(byteCount, remove + this.GrowthAvailable);
        if (count < byteCount)
        {
            message = $"Pasted {count:N0} bytes. The maximum length is {(this.MaxLength == 0 ? long.MaxValue : this.MaxLength):N0} bytes.";
            if (count == 0)
            {
                return false;
            }
        }

        var insert = new byte[count];
        if (ascii)
        {
            System.Text.Encoding.ASCII.GetBytes(text.AsSpan(0, count), insert);
        }
        else if (readCustom is not null)
        {
            if (readCustom(text, insert) != byteCount)
            {
                message = "The clipboard data could not be decoded.";
                return false;
            }
        }
        else
        {
            DecodeHex(text, insert);
        }

        this.Document.Replace(offset, remove, insert);
        this.MoveTo(offset + count);
        return true;
    }

    private static int CountHexBytes(string text)
    {
        var digits = 0;
        foreach (var character in text)
        {
            if (char.IsAsciiHexDigit(character))
            {
                digits++;
            }
            else if (!char.IsWhiteSpace(character))
            {
                return 0;
            }
        }

        return digits % 2 == 0 ? digits / 2 : 0;
    }

    private static int HexDigit(char character) =>
        char.IsAsciiDigit(character) ? character - '0' : char.ToUpperInvariant(character) - 'A' + 10;

    private static void DecodeHex(string text, Span<byte> bytes)
    {
        var position = 0;

        int NextDigit()
        {
            while (char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            return HexDigit(text[position++]);
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((NextDigit() << 4) | NextDigit());
        }
    }
}
