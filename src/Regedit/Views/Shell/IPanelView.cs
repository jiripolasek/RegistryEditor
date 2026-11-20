// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;

namespace JPSoftworks.Regedit.Views.Shell;

/// <summary>
/// A view that can be shown in the bottom panel. The panel header renders its search box and commands
/// from this description; the view never hands over its own controls.
/// </summary>
internal interface IPanelView
{
    /// <summary>UI Automation id of the panel command bar while this view is shown.</summary>
    string PanelCommandsId { get; }

    IReadOnlyList<ToolCommand> PanelCommands { get; }

    PanelSearch? PanelSearch { get; }

    /// <summary>Switches between the compact panel layout and the full main-tab layout.</summary>
    void SetDocked(bool docked);
}

/// <summary>Describes the panel header search box and keeps it in sync with the view's filter text.</summary>
public sealed class PanelSearch(
    string automationName,
    string? automationId,
    string placeholder,
    string? toolTip,
    Func<string> getText,
    Action<string> setText)
{
    /// <summary>Raised by the view when its filter text changes from another source.</summary>
    public event Action? TextChanged;

    public string AutomationName { get; } = automationName;

    public string? AutomationId { get; } = automationId;

    public string Placeholder { get; } = placeholder;

    public string? ToolTip { get; } = toolTip;

    public string Text
    {
        get => getText();
        set
        {
            if (getText() != value)
            {
                setText(value);
            }
        }
    }

    public void NotifyTextChanged()
    {
        this.TextChanged?.Invoke();
    }
}
