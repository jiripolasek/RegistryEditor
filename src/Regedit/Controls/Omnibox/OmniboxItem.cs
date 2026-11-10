// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Omnibox;

public sealed record OmniboxItem(string Label, string Address)
{
    public bool? HasChildren { get; init; }

    public override string ToString()
    {
        return this.Label;
    }
}
