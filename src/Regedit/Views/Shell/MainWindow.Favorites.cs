// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Views.Pages;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private void AddFavoritesMenuActions(MenuFlyout menu, FavoritesView? panel = null)
    {
        var separator = new MenuFlyoutSeparator();
        var showBar = new ToggleMenuFlyoutItem
        {
            Text = "Show favorites bar",
            Icon = new SymbolIcon(Symbol.Favorite),
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+B"
        };
        showBar.Click += (_, _) => this.ToggleFavoritesBar();
        var manage = new MenuFlyoutItem
        {
            Text = "Manage favorites…",
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        manage.Click += (_, _) => this.OpenToolTab(ToolKind.Favorites, panel);
        menu.Items.Add(separator);
        menu.Items.Add(showBar);
        menu.Items.Add(manage);

        menu.Opening += (_, _) =>
        {
            showBar.IsChecked = this.Preferences.ShowFavoritesBar;
            separator.Visibility = showBar.Visibility = manage.Visibility =
                panel is null || menu.Target is Button ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private void ToggleFavoritesBar()
    {
        this.Preferences.ShowFavoritesBar = !this.Preferences.ShowFavoritesBar;
    }

    private void FavoritesBar_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ToggleFavoritesBar();
        args.Handled = true;
    }

    private void FavoritesSidebar_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ShowTools(ToolKind.Favorites, true);
        args.Handled = true;
    }

    private void Favorites_Changed(object? sender, EventArgs args)
    {
        foreach (var page in this._views.Values.OfType<BrowserPage>())
        {
            page.InvalidateFavoritesBar();
        }

        (this.SelectedTabContent as BrowserPage)?.RefreshFavoritesBar();
    }

    internal MenuFlyout CreateFavoritesMenu(string? folderId = null, Func<HashSet<FavoriteItemId>>? rootItems = null)
    {
        var menu = new MenuFlyout
        {
            Placement = rootItems is null ? FlyoutPlacementMode.BottomEdgeAlignedLeft
                : FlyoutPlacementMode.BottomEdgeAlignedRight
        };
        menu.Opening += (_, _) =>
        {
            var folders = this.Favorites.Folders.ToLookup(folder => folder.ParentId);
            var favorites = this.Favorites.Favorites.ToLookup(favorite => favorite.FolderId);
            menu.Items.Clear();
            Fill(menu.Items, folderId, rootItems?.Invoke());
            if (folderId is null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var sidebar = new MenuFlyoutItem
                {
                    Text = "Open Favorites sidebar",
                    Icon = new SymbolIcon(Symbol.DockRight),
                    KeyboardAcceleratorTextOverride = "Ctrl+Shift+O"
                };
                sidebar.Click += (_, _) => this.ShowTools(ToolKind.Favorites, true);
                menu.Items.Add(sidebar);
            }

            void Fill(IList<MenuFlyoutItemBase> items, string? parent, HashSet<FavoriteItemId>? included = null)
            {
                foreach (var folder in folders[parent])
                {
                    if (included is not null && !included.Contains(new FavoriteItemId(folder.Id, true)))
                    {
                        continue;
                    }

                    var submenu = new MenuFlyoutSubItem { Text = folder.Name, Icon = new SymbolIcon(Symbol.Folder) };
                    AutomationProperties.SetAutomationId(submenu, $"FavoriteMenuFolder_{folder.Id}");
                    Fill(submenu.Items, folder.Id);
                    items.Add(submenu);
                }

                foreach (var favorite in favorites[parent])
                {
                    if (included is not null && !included.Contains(new FavoriteItemId(favorite.Id, false)))
                    {
                        continue;
                    }

                    var item = new MenuFlyoutItem { Text = favorite.Name, Icon = new SymbolIcon(Symbol.Favorite) };
                    AutomationProperties.SetAutomationId(item, $"FavoriteMenuBookmark_{favorite.Id}");
                    AutomationProperties.SetHelpText(item, favorite.Address);
                    item.Click += async (_, _) => await this.OpenFavoriteAsync(favorite);
                    items.Add(item);
                }

                if (items.Count == 0)
                {
                    items.Add(new MenuFlyoutItem
                    {
                        Text = parent is null ? "No favorites yet" : "This folder is empty",
                        IsEnabled = false
                    });
                }
            }
        };
        return menu;
    }

    internal Task OpenFavoriteAsync(Favorite favorite, bool newTab = false)
    {
        return this.RunFavoriteActionAsync(
            "Open favorite", () => this.ViewModel.NavigateFromToolsAsync(favorite.GetLocation(), newTab));
    }

    internal void ShowFavoriteInOrganizer(string id)
    {
        var favorite = this.Favorites.Bookmark(id);
        var organizer = (FavoritesView)this.OpenToolTab(ToolKind.Favorites);
        organizer.ViewModel.OpenFolder(favorite.FolderId ?? string.Empty);
        organizer.ViewModel.SelectRows((FavoriteItemId[])[new(id, false)]);
    }

    internal async Task RunFavoriteActionAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            this.Logger.LogFailure(LogLevel.Warning, operation, ex);
            this.ShowApplicationNotice("Couldn't complete Favorites action", ex.Message, NoticeKind.Error);
        }
    }
}
