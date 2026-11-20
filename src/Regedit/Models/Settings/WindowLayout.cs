// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Settings;

public sealed record WindowLayout(
    WindowPlacement? Placement = null,
    bool PaneOpen = false,
    bool PanePinned = false,
    double PaneWidth = 360,
    string SelectedTool = "Favorites",
    bool BottomPaneOpen = false,
    double BottomPaneHeight = 240,
    string? BottomTool = null)
{
    internal WindowLayout Normalize()
    {
        return this with
        {
            Placement = this.Placement is { IsValid: true } ? this.Placement : null,
            PaneWidth = double.IsFinite(this.PaneWidth) ? Math.Clamp(this.PaneWidth, 300, 680) : 360,
            BottomPaneHeight = double.IsFinite(this.BottomPaneHeight) ? Math.Clamp(this.BottomPaneHeight, 180, 600) : 240,
            BottomTool = this.BottomTool is "Journal" or "Watches" ? this.BottomTool : null,
            SelectedTool = this.SelectedTool is "Watches" or "Journal" or "Notes" ? this.SelectedTool : "Favorites"
        };
    }
}
