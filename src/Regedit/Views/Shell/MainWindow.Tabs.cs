// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private void ReopenTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!this.Dialogs.IsOpen && this.ViewModel.ReopenClosedTabCommand.CanExecute(null))
        {
            this.ViewModel.ReopenClosedTabCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void Tab_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not TabViewItem { DataContext: WindowTab tab } item || !this.ViewModel.Tabs.Contains(tab))
        {
            return;
        }

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        if (tab is BrowserTab browser)
        {
            ListContextMenu.Add(
                menu, "Duplicate tab", () => this.ViewModel.DuplicateTab(browser),
                iconSource: new FontIconSource
                {
                    FontFamily = new("/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"), Glyph = "\uEBE9"
                });
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (tab is SearchResultsViewModel or ToolTab { Tool: ToolKind.Journal or ToolKind.Watches })
        {
            ListContextMenu.Add(menu, "Move to bottom panel", () => this.ViewModel.DockTab(tab), icon: Symbol.DockBottom);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        ListContextMenu.Add(
            menu, "Reopen closed tab", () => this.ViewModel.ReopenClosedTabCommand.Execute(null),
            this.ViewModel.ReopenClosedTabCommand.CanExecute(null), icon: Symbol.Undo);
        ListContextMenu.Add(menu, "Close tab", () => this.ViewModel.CloseTab(tab), icon: Symbol.Cancel);
        ListContextMenu.Add(menu, "Close other tabs", () => this.ViewModel.CloseOtherTabs(tab), this.ViewModel.Tabs.Count > 1);
        ListContextMenu.Add(menu, "Close tabs to the right", () => this.ViewModel.CloseTabsToRight(tab), this.ViewModel.Tabs[^1] != tab);
        if (args.TryGetPosition(item, out var point))
        {
            menu.ShowAt(item, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(item);
        }

        args.Handled = true;
    }
}
