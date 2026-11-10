// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
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
        this.BackButton.Visibility = this.Settings.ShowBackButton ? Visibility.Visible : Visibility.Collapsed;
        this.ForwardButton.Visibility = this.Settings.ShowForwardButton ? Visibility.Visible : Visibility.Collapsed;
        this.UpButton.Visibility = this.Settings.ShowUpButton ? Visibility.Visible : Visibility.Collapsed;
        this.RefreshButton.Visibility = this.Settings.ShowRefreshButton ? Visibility.Visible : Visibility.Collapsed;
        this.NavigationButtons.Visibility = this.Settings.ShowBackButton || this.Settings.ShowForwardButton ||
                                            this.Settings.ShowUpButton || this.Settings.ShowRefreshButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        this.ToolbarShortcuts.Visibility = this.Settings.ToolbarButtons == ToolbarButtons.None
            ? Visibility.Collapsed
            : Visibility.Visible;
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
        this._window.FillToolsMenu(((MenuFlyout)sender).Items);
    }
}
