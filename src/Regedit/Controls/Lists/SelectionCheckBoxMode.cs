// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Lists;

public enum SelectionCheckBoxMode
{
    /// <summary>Use the list's native selection presentation.</summary>
    Default,

    /// <summary>Show a check box before each item.</summary>
    Inline,

    /// <summary>Show a check box over the leading cell on hover or selection. Without a leading cell, use Inline.</summary>
    Overlay
}
