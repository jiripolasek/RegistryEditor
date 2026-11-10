// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace JPSoftworks.Regedit.Controls.Shared;

/// <summary>
/// Renders <see cref="ToolCommand"/>s as compact icon buttons that share the panel header button style, so tool
/// commands match the generic panel buttons in size and hover feedback. Overflow commands go to a "More options"
/// menu.
/// </summary>
public sealed partial class PanelCommandStrip : StackPanel
{
    private readonly List<(ToolCommand Command, ButtonBase Button, PropertyChangedEventHandler Handler)> _bound = [];
    private readonly List<ToolCommand> _overflow = [];
    private readonly List<ToolCommand> _dynamicOverflow = [];
    private int _maxPrimaryCommands = int.MaxValue;
    private readonly Button _more;
    private readonly MenuFlyout _moreMenu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

    public PanelCommandStrip()
    {
        this.Orientation = Orientation.Horizontal;
        this.Spacing = 2;
        this._more = new Button { Content = new FontIcon { Glyph = "", FontSize = 16 }, Flyout = this._moreMenu };
        AutomationProperties.SetName(this._more, "More options");
        AutomationProperties.SetAutomationId(this._more, "MoreButton");
        ToolTipService.SetToolTip(this._more, "More options");
        this._moreMenu.Opening += (_, _) => this.FillOverflowMenu();
    }

    /// <summary>Style for command buttons and the overflow button.</summary>
    public Style? ButtonStyle { get; set; }

    /// <summary>Style for toggle commands.</summary>
    public Style? ToggleButtonStyle { get; set; }

    internal void SetMaxPrimaryCommands(int count)
    {
        this._maxPrimaryCommands = Math.Max(0, count);
        this.UpdateOverflow();
    }

    internal void SetCommands(IEnumerable<ToolCommand>? commands)
    {
        foreach (var (command, _, handler) in this._bound)
        {
            command.PropertyChanged -= handler;
        }

        this._bound.Clear();
        this._overflow.Clear();
        this._moreMenu.Hide();
        this.Children.Clear();
        foreach (var command in commands ?? [])
        {
            if (command.Placement == ToolCommandPlacement.Overflow)
            {
                this._overflow.Add(command);
                continue;
            }

            var button = this.CreateButton(command);
            PropertyChangedEventHandler handler = (_, _) =>
            {
                Apply(command, button);
                this.UpdateOverflow();
            };
            command.PropertyChanged += handler;
            this._bound.Add((command, button, handler));
            this.Children.Add(button);
        }

        this._more.Style = this.ButtonStyle;
        this.Children.Add(this._more);
        this.UpdateOverflow();
    }

    private void UpdateOverflow()
    {
        this._dynamicOverflow.Clear();
        var visible = 0;
        foreach (var (command, button, _) in this._bound)
        {
            var overflow = command.IsVisible && visible++ >= this._maxPrimaryCommands;
            button.Visibility = command.IsVisible && !overflow ? Visibility.Visible : Visibility.Collapsed;
            if (overflow)
            {
                this._dynamicOverflow.Add(command);
            }
        }

        this._more.Visibility = this._overflow.Count > 0 || this._dynamicOverflow.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private ButtonBase CreateButton(ToolCommand command)
    {
        IconElement icon = command.IconSource is { } source
            ? new IconSourceElement { IconSource = source }
            : new FontIcon { Glyph = SymbolGlyphs.Get(command.Symbol), FontSize = 16 };
        ButtonBase button;
        if (command.IsToggle)
        {
            var toggle = new ToggleButton { Content = icon, Style = this.ToggleButtonStyle };
            toggle.Click += (_, _) => command.IsChecked = toggle.IsChecked == true;
            button = toggle;
        }
        else
        {
            button = new Button { Content = icon, Style = this.ButtonStyle, Flyout = command.Flyout };
        }

        if (command.AutomationId is { } id)
        {
            AutomationProperties.SetAutomationId(button, id);
        }

        command.Attach?.Invoke(button);
        Apply(command, button);
        return button;
    }

    private static void Apply(ToolCommand command, ButtonBase button)
    {
        if (command.IconSource is null && button.Content is FontIcon icon)
        {
            icon.Glyph = SymbolGlyphs.Get(command.Symbol);
        }

        if (button is ToggleButton toggle)
        {
            toggle.IsChecked = command.IsChecked;
        }

        if (!ReferenceEquals(button.Command, command.Command))
        {
            button.Command = command.Command;
        }

        button.CommandParameter = command.CommandParameter;
        if (command.Command is null)
        {
            button.IsEnabled = command.IsEnabled;
        }

        // Icon-only buttons: the label is the accessible name and the tooltip; extra help goes to HelpText.
        AutomationProperties.SetName(button, command.AutomationName ?? command.Label);
        AutomationProperties.SetHelpText(button, command.ToolTip ?? string.Empty);
        ToolTipService.SetToolTip(
            button,
            command.ToolTip is { } help && help != command.Label
                ? command.Label + Environment.NewLine + help
                : command.Label);
        button.Visibility = command.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillOverflowMenu()
    {
        this._moreMenu.Items.Clear();
        foreach (var command in this._dynamicOverflow.Concat(this._overflow).Where(command => command.IsVisible))
        {
            MenuFlyoutItem item = command.IsToggle
                ? new ToggleMenuFlyoutItem { IsChecked = command.IsChecked }
                : new MenuFlyoutItem();
            item.Text = command.Label;
            item.KeyboardAcceleratorTextOverride = command.ShortcutText ?? string.Empty;
            item.Icon = command.IconSource is { } source
                ? new IconSourceElement { IconSource = source }
                : new FontIcon { Glyph = SymbolGlyphs.Get(command.Symbol) };
            item.Command = command.Command;
            item.CommandParameter = command.CommandParameter;
            if (command.Command is null)
            {
                item.IsEnabled = command.IsEnabled;
            }

            if (command.AutomationId is { } id)
            {
                AutomationProperties.SetAutomationId(item, id);
            }

            if (command.ToolTip is { } help)
            {
                AutomationProperties.SetHelpText(item, help);
            }

            if (item is ToggleMenuFlyoutItem toggle)
            {
                toggle.Click += (_, _) => command.IsChecked = toggle.IsChecked;
            }

            if (command.Flyout is { } flyout)
            {
                item.Click += (_, _) => flyout.ShowAt(this._more);
            }

            command.Attach?.Invoke(item);
            this._moreMenu.Items.Add(item);
        }
    }
}
