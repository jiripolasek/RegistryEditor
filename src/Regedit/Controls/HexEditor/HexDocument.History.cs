// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Controls;

public sealed partial class HexDocument
{
    private const int HistoryByteLimit = 4 * 1024 * 1024;
    private const int HistoryEntryLimit = 128;

    private readonly History _undo = new();
    private readonly History _redo = new();

    internal Change? NextHistoryChange(bool redo) => (redo ? this._redo : this._undo).Last?.Change;

    private HistoryEntry? CaptureInverse(Change change)
    {
        var end = change.Offset + change.Removed;
        var first = this.FindPiece(change.Offset);
        var count = change.Removed == 0 ? 0 : this.FindPiece(end - 1) - first + 1;
        long cost = 64 + 64L * count;
        for (var i = 0; i < count && cost <= HistoryByteLimit; i++)
        {
            var piece = this._pieces[first + i];
            if (piece.Edits is not null)
            {
                cost += Math.Min(end, piece.Start + piece.Length) - Math.Max(change.Offset, piece.Start);
            }
        }

        if (cost > HistoryByteLimit)
        {
            return null;
        }

        var restore = new Piece[count];
        for (var i = 0; i < count; i++)
        {
            var piece = this._pieces[first + i];
            var start = Math.Max(change.Offset, piece.Start);
            var length = Math.Min(end, piece.Start + piece.Length) - start;
            var offset = piece.Offset + (start - piece.Start);
            // Source ranges are immutable. Only edited bytes need an independent copy.
            restore[i] = piece.Edits is { } data
                ? new Piece(start, 0, length, data.AsSpan((int)offset, (int)length).ToArray())
                : new Piece(start, offset, length, null);
        }

        return new HistoryEntry(new Change(change.Offset, change.Inserted, change.Removed), restore, (int)cost);
    }

    private void RecordEdit(Change change, HistoryEntry? inverse, bool finishByte)
    {
        this._redo.Clear();
        if (!finishByte)
        {
            this._undo.Push(inverse);
        }

        this.LastChange = change;
        this.Revision++;
    }

    internal void ReplayHistory(bool redo)
    {
        var from = redo ? this._redo : this._undo;
        var to = redo ? this._undo : this._redo;
        var entry = from.Last!;
        var inverse = this.CaptureInverse(entry.Change);
        this.Splice(entry.Change, entry.Restore);
        from.Pop();
        to.Push(inverse);
        this.LastChange = entry.Change;
        this.Revision++;
    }

    private sealed record HistoryEntry(Change Change, Piece[] Restore, int Cost);

    private sealed class History
    {
        private readonly List<HistoryEntry> _entries = [];
        private int _cost;

        internal HistoryEntry? Last => this._entries.Count > 0 ? this._entries[^1] : null;

        internal void Clear()
        {
            this._entries.Clear();
            this._cost = 0;
        }

        internal void Pop()
        {
            this._cost -= this._entries[^1].Cost;
            this._entries.RemoveAt(this._entries.Count - 1);
        }

        internal void Push(HistoryEntry? entry)
        {
            if (entry is null)
            {
                // Older inverses depend on the edit that did not fit.
                this.Clear();
                return;
            }

            while (this._entries.Count >= HistoryEntryLimit || this._cost + entry.Cost > HistoryByteLimit)
            {
                this._cost -= this._entries[0].Cost;
                this._entries.RemoveAt(0);
            }

            this._entries.Add(entry);
            this._cost += entry.Cost;
        }
    }
}
