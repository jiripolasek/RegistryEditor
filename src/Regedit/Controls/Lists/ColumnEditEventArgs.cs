// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Lists;

/// <summary>
///     Raised when the user confirms an inline edit with changed text. The host applies the change (for example a
///     rename) or sets <see cref="Cancel" /> with an optional <see cref="Error" /> to keep the editor open.
/// </summary>
public sealed class ColumnEditCommittingEventArgs(object item, string columnId, string oldText, string newText) : EventArgs
{
    public object Item { get; } = item;

    public string ColumnId { get; } = columnId;

    public string OldText { get; } = oldText;

    public string NewText { get; } = newText;

    public bool Cancel { get; set; }

    /// <summary>Shown beside the editor when <see cref="Cancel" /> is set.</summary>
    public string? Error { get; set; }
}

public sealed class ColumnEditEndedEventArgs(object item, string columnId, bool committed, string text) : EventArgs
{
    public object Item { get; } = item;

    public string ColumnId { get; } = columnId;

    /// <summary>True when changed text was accepted; false for Escape, unchanged text or an abandoned edit.</summary>
    public bool Committed { get; } = committed;

    public string Text { get; } = text;
}
