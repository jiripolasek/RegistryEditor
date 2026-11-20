// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Models.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
    private readonly List<(ToolCommand Command, Button Pin)> _toolsMenuPins = [];

    private void InitializeToolbar()
    {
        this.ToolbarShortcuts.SetCommands(this._window.ToolbarCommands.Select(item => item.Command));
        var contextMenu = this._window.CreateToolbarContextMenu();
        this.PathRow.ContextFlyout = this.ToolbarShortcuts.ContextFlyout = this.ToolsButton.ContextFlyout = contextMenu;
        this.UpdateToolbar();
    }

    internal void UpdateToolbar()
    {
        this._window.UpdateToolbarCommands();
        this.UpdateToolsMenuPins();
        this.BackButton.Visibility = this.Settings.ShowBackButton ? Visibility.Visible : Visibility.Collapsed;
        this.ForwardButton.Visibility = this.Settings.ShowForwardButton ? Visibility.Visible : Visibility.Collapsed;
        this.UpButton.Visibility = this.Settings.ShowUpButton ? Visibility.Visible : Visibility.Collapsed;
        this.RefreshButton.Visibility = this.Settings.ShowRefreshButton ? Visibility.Visible : Visibility.Collapsed;
        this.NavigationButtons.Visibility
            = this.Settings.ShowBackButton
              || this.Settings.ShowForwardButton
              || this.Settings.ShowUpButton
              || this.Settings.ShowRefreshButton
                ? Visibility.Visible
                : Visibility.Collapsed;
        this.ToolbarShortcuts.Visibility = this.Settings.ToolbarButtons == ToolbarButtons.None ? Visibility.Collapsed : Visibility.Visible;
        this.ToolbarShortcuts.MaxWidth = Math.Clamp(this.LayoutRoot.ActualWidth - 700, 40, 360);
        const double buttonWidth = 36;
        var count = this._window.ToolbarCommands.Count(item => item.Command.IsVisible);
        var spacing = this.ToolbarShortcuts.Spacing;
        var width = count * buttonWidth + Math.Max(0, count - 1) * spacing;
        this.ToolbarShortcuts.SetMaxPrimaryCommands(
            width <= this.ToolbarShortcuts.MaxWidth
                ? int.MaxValue
                : (int)((this.ToolbarShortcuts.MaxWidth - buttonWidth - spacing) / (buttonWidth + spacing)));
    }

    private void ToolsMenu_Opening(object sender, object args)
    {
        this._toolsMenuPins.Clear();
        this._window.FillToolsMenu(((MenuFlyout)sender).Items, this.AttachToolsMenuPin);
        this.UpdateToolsMenuPins();
    }

    private void AttachToolsMenuPin(ToolbarButtons button, ToolCommand command, MenuFlyoutItem item)
    {
        var pin = new Button
        {
            Style = (Style)this.Resources["ToolsMenuPinButtonStyle"],
            Content = new FontIcon { FontSize = 14 },
            Command = this.Preferences.ToggleToolbarButtonCommand,
            CommandParameter = button,
            AllowFocusOnInteraction = false
        };
        // The pin handles pointer input separately from the menu item.
        pin.AddHandler(
            PointerEnteredEvent, new PointerEventHandler((_, _) => VisualStateManager.GoToState(item, "PointerOver", false)), true);
        pin.AddHandler(
            PointerExitedEvent, new PointerEventHandler((_, args) =>
            {
                var point = args.GetCurrentPoint(item).Position;
                if (point.X < 0 || point.X >= item.ActualWidth || point.Y < 0 || point.Y >= item.ActualHeight)
                {
                    VisualStateManager.GoToState(item, "Normal", false);
                }
            }), true);
        AutomationProperties.SetAutomationId(pin, $"ToolsMenuPin{button}");
        AutomationProperties.SetAutomationId(item, $"ToolsMenu{button}");
        item.Template = (ControlTemplate)this.Resources["PinnableToolsMenuItemTemplate"];
        item.Tag = pin;
        item.KeyDown += ToolsMenuItem_KeyDown;
        this._toolsMenuPins.Add((command, pin));
    }

    private static void ToolsMenuItem_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var item = (MenuFlyoutItem)sender;
        var pin = (Button)item.Tag;
        var towardPin = item.FlowDirection == FlowDirection.RightToLeft ? VirtualKey.Left : VirtualKey.Right;
        var towardItem = towardPin == VirtualKey.Left ? VirtualKey.Right : VirtualKey.Left;
        if (args.Key == towardPin && item.FocusState != FocusState.Unfocused)
        {
            args.Handled = pin.Focus(FocusState.Keyboard);
        }
        else if (args.Key == towardItem && pin.FocusState != FocusState.Unfocused)
        {
            args.Handled = item.Focus(FocusState.Keyboard);
        }
    }

    private void UpdateToolsMenuPins()
    {
        foreach (var (command, pin) in this._toolsMenuPins)
        {
            var label = command.IsVisible ? $"Unpin {command.Label} from toolbar" : $"Pin {command.Label} to toolbar";
            ((FontIcon)pin.Content).Glyph = command.IsVisible ? "\uE77A" : "\uE718";
            AutomationProperties.SetName(pin, label);
            ToolTipService.SetToolTip(pin, label);
        }
    }
}
