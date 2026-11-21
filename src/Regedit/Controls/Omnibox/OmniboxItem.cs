// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Windows.Input;

namespace JPSoftworks.Regedit.Controls.Omnibox;

public sealed record OmniboxItem(string Label, string Address)
{
    public bool? HasChildren { get; init; }

    public string Detail => this.Description ?? (this.Label == this.Address ? string.Empty : this.Address);

    public string? Description { get; init; }

    public string? IconGlyph { get; init; }

    public string? RightText { get; init; }

    public string? Group { get; init; }

    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    internal OmniboxScope? Scope { get; init; }
    internal OmniboxItem? FirstHiddenResult { get; init; }

    public override string ToString()
    {
        return this.Label;
    }
}
