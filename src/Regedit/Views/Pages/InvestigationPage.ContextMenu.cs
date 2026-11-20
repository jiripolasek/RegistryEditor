// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class InvestigationPage
{
    private MenuFlyout CreateResultMenu()
    {
        var menu = new MenuFlyout();
        var search = this.Search!;
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Open result",
                Command = search.OpenCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Forward) },
                KeyboardAcceleratorTextOverride = "Enter"
            });
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Open in new tab",
                Command = search.OpenNewTabCommand,
                Icon = new IconSourceElement { IconSource = (IconSource)Application.Current.Resources["OpenInNewTabIcon"] }
            });
        ListContextMenu.Add(
            menu, "Show details", () =>
            {
                this._detailsExpanded = true;
                this._detailsCollapsed = false;
                this.ShowSelection(this.ViewModel.Selected);
                this.Details.FocusComparison();
            }, this.ViewModel.Selected.Count == 1, icon: Symbol.View, shortcut: this.IsWide ? null : "Alt+P");
        if (this.ViewModel.Selected is [{ Source: RegistrySearchMatch { Value: { } value } }])
        {
            ListContextMenu.Add(menu, "Inspect value", () => this.InspectValue(value), icon: Symbol.View);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(
            new MenuFlyoutItem { Text = "Copy path", Command = this.ViewModel.CopyPathsCommand, Icon = new FontIcon { Glyph = "\uE62F" } });
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy results",
                Command = this.ViewModel.CopyResultsCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Copy) }
            });
        menu.Items.Add(new MenuFlyoutSeparator());
        ListContextMenu.Add(
            menu, "Select all", () => this.Results.SelectAll(), this.ViewModel.DisplayRows.Count > 0, icon: Symbol.SelectAll,
            shortcut: "Ctrl+A");
        return menu;
    }
}
