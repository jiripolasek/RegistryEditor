// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace JPSoftworks.Regedit.Controls.Shared;

public enum ToolCommandPlacement
{
    Primary,
    Overflow
}

/// <summary>
/// Describes one toolbar command once so several <see cref="ListCommandBar"/> instances (for example the
/// view's own toolbar and the bottom panel toolbar) can show it without moving controls between trees.
/// </summary>
public sealed partial class ToolCommand : ObservableObject
{
    [ObservableProperty]
    public partial Symbol Symbol { get; set; }

    public IconSource? IconSource { get; init; }

    public ToolCommandPlacement Placement { get; }

    public string? ShortcutText { get; init; }

    /// <summary>Stable UI Automation id for generated buttons.</summary>
    public string? AutomationId { get; init; }

    /// <summary>Accessible name when it differs from the visible label.</summary>
    public string? AutomationName { get; init; }

    /// <summary>Creates an <see cref="AppBarToggleButton"/> whose state is kept in <see cref="IsChecked"/>.</summary>
    public bool IsToggle { get; init; }

    /// <summary>Flyout shared by every generated button; only one bar shows it at a time.</summary>
    [ObservableProperty]
    public partial FlyoutBase? Flyout { get; set; }

    /// <summary>Extra per-button wiring such as middle-click handlers.</summary>
    public Action<FrameworkElement>? Attach { get; init; }

    [ObservableProperty]
    public partial string Label { get; set; }

    [ObservableProperty]
    public partial ICommand? Command { get; set; }

    [ObservableProperty]
    public partial object? CommandParameter { get; set; }

    /// <summary>Tooltip, also published as the accessible help text.</summary>
    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>Enabled state for commands without an <see cref="ICommand"/>.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public ToolCommand(string label, Symbol symbol, ToolCommandPlacement placement = ToolCommandPlacement.Primary)
    {
        this.Label = label;
        this.Symbol = symbol;
        this.Placement = placement;
    }
}
