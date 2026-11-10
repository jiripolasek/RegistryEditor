// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Settings;

[Flags]
public enum ToolbarButtons
{
    None = 0,
    Favorites = 1,
    Notes = 2,
    Watches = 4,
    Journal = 8,
    SavedSearches = 16,
    Workspaces = 32,
    Sidebar = 64,
    BottomPanel = 128,
    All = Favorites | Notes | Watches | Journal | SavedSearches | Workspaces | Sidebar | BottomPanel
}
