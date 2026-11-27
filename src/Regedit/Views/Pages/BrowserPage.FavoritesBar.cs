// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
    private readonly FavoriteDrag _favoritesBarDrag = new();
    private readonly MenuFlyout _favoriteBarContextMenu = new();
    private MenuFlyout? _openFavoriteBarMenu;
    private bool _favoritesBarDirty = true;
    private int _visibleFavoriteBarItems;

    internal void InvalidateFavoritesBar()
    {
        this._favoritesBarDrag.Clear();
        this._favoritesBarDirty = true;
    }

    internal void RefreshFavoritesBar()
    {
        if (!this._favoritesBarDirty)
        {
            return;
        }

        this._favoritesBarDirty = false;
        this._openFavoriteBarMenu?.Hide();
        var items = this.FavoriteBarItems.Children;
        items.Clear();
        if (!this.Settings.ShowFavoritesBar)
        {
            this._visibleFavoriteBarItems = 0;
            this.FavoriteBarOverflowButton.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var folder in this._window.Favorites.Folders.Where(folder => folder.ParentId is null))
        {
            var button = this.CreateFavoriteBarButton(
                folder.Name, SymbolGlyphs.Get(Symbol.Folder), new FavoriteItemId(folder.Id, true));
            this.AttachFavoriteBarMenu(button, this._window.CreateFavoritesMenu(folder.Id));
            items.Add(button);
        }

        foreach (var favorite in this._window.Favorites.Favorites.Where(favorite => favorite.FolderId is null))
        {
            var button = this.CreateFavoriteBarButton(
                favorite.Name, SymbolGlyphs.Get(Symbol.Favorite), new FavoriteItemId(favorite.Id, false));
            var path = favorite.Address + (favorite.Is32Bit ? " (32-bit)" : " (64-bit)");
            AutomationProperties.SetHelpText(button, path);
            ToolTipService.SetToolTip(button, favorite.Name + Environment.NewLine + path);
            button.Click += async (_, _) => await this._window.OpenFavoriteAsync(favorite);
            items.Add(button);
        }

        if (items.Count == 0)
        {
            var button = this.CreateFavoriteBarButton("Open Favorites sidebar", "\uE728");
            AutomationProperties.SetAutomationId(button, "FavoriteBarOpenSidebar");
            button.Click += (_, _) => this._window.ShowTools(ToolKind.Favorites, true);
            items.Add(button);
        }

        this.UpdateFavoritesBarOverflow();
    }

    private void AttachFavoriteBarMenu(Button button, MenuFlyout menu)
    {
        menu.OverlayInputPassThroughElement = this.FavoriteBarHost;
        menu.Opening += (_, _) => this._openFavoriteBarMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(this._openFavoriteBarMenu, menu))
            {
                this._openFavoriteBarMenu = null;
            }
        };
        button.Flyout = menu;
        button.PointerEntered += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !args.Pointer.IsInContact &&
                this._openFavoriteBarMenu is { } openMenu && !ReferenceEquals(openMenu, menu))
            {
                // WinUI closes the previous flyout before opening its sibling.
                menu.ShowAt(button);
            }
        };
    }

    private void FavoriteBarHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateFavoritesBarOverflow();
    }

    private void UpdateFavoritesBarOverflow()
    {
        var available = this.FavoriteBarHost.ActualWidth;
        if (available <= 0 || !this.Settings.ShowFavoritesBar)
        {
            return;
        }

        var items = this.FavoriteBarItems.Children;
        var overflow = this.FavoriteBarOverflowButton;
        var focused = this.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Button : null;
        var focusedIndex = focused is null ? -1 : items.IndexOf(focused);
        var widths = new double[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            item.Visibility = Visibility.Visible;
            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[index] = item.DesiredSize.Width;
        }

        var visible = FavoritesBarLayout.VisibleCount(
            widths, available, this.FavoriteBarItems.Spacing, overflow.Width + overflow.Margin.Left);
        if (visible != this._visibleFavoriteBarItems)
        {
            overflow.Flyout?.Hide();
            this._visibleFavoriteBarItems = visible;
        }

        if (visible < items.Count)
        {
            overflow.Visibility = Visibility.Visible;
            if (focusedIndex >= visible)
            {
                overflow.Focus(FocusState.Programmatic);
            }

            for (var index = visible; index < items.Count; index++)
            {
                items[index].Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            if (ReferenceEquals(focused, overflow) && items.Count > 0)
            {
                ((Button)items[^1]).Focus(FocusState.Programmatic);
            }

            overflow.Visibility = Visibility.Collapsed;
        }
    }

    private HashSet<FavoriteItemId> OverflowFavoriteItems()
    {
        return this.FavoriteBarItems.Children.Skip(this._visibleFavoriteBarItems).OfType<Button>()
            .Select(button => button.Tag).OfType<FavoriteItemId>().ToHashSet();
    }

    private Button CreateFavoriteBarButton(string name, string glyph, FavoriteItemId? item = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock
        {
            Text = name,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var button = new Button { Content = content, Style = (Style)this.Resources["FavoritesBarButtonStyle"] };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        if (item is { } entry)
        {
            button.Tag = entry;
            AutomationProperties.SetAutomationId(button,
                entry.IsFolder ? $"FavoriteBarFolder_{entry.Id}" : $"FavoriteBarBookmark_{entry.Id}");
            button.ContextFlyout = this._favoriteBarContextMenu;
            button.AllowDrop = true;
            this.WireFavoriteDragGesture(button);
            button.DragStarting += (_, args) =>
            {
                this._favoritesBarDrag.Start(entry, args.Data);
                args.AllowedOperations = DataPackageOperation.Move;
            };
            button.DropCompleted += (_, _) => this._favoritesBarDrag.Clear();
            button.DragOver += (_, args) => this.AcceptFavoriteBarDrop(button, args, preview: true);
            button.Drop += async (_, args) => await this._favoritesBarDrag.CompleteDropAsync(args, async () =>
            {
                if (this.AcceptFavoriteBarDrop(button, args) is { } move)
                {
                    await this._window.RunFavoriteActionAsync("Move favorite", () =>
                    {
                        if (move.Folder is { } folder)
                        {
                            this._window.Favorites.Move((FavoriteItemId[])[move.Item], folder);
                        }
                        else
                        {
                            this._window.Favorites.Reorder(move.Item, move.Offset);
                        }

                        return Task.CompletedTask;
                    });
                }
            });
        }

        return button;
    }

    private void WireFavoriteDragGesture(Button button)
    {
        Point origin = default;
        uint? pointerId = null;
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(button);
            if (point.Properties.IsLeftButtonPressed)
            {
                origin = point.Position;
                pointerId = point.PointerId;
            }
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, args) =>
        {
            var point = args.GetCurrentPoint(button);
            if (pointerId != point.PointerId || !point.Properties.IsLeftButtonPressed ||
                Math.Abs(point.Position.X - origin.X) < 6 && Math.Abs(point.Position.Y - origin.Y) < 6)
            {
                return;
            }

            // The button captures the press for Click, so release its capture before starting the drag.
            pointerId = null;
            button.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
            await this._window.RunFavoriteActionAsync("Drag favorite", async () =>
            {
                await button.StartDragAsync(args.GetCurrentPoint(null));
            });
        }), true);
        var reset = new PointerEventHandler((_, _) => pointerId = null);
        button.AddHandler(UIElement.PointerReleasedEvent, reset, true);
        button.AddHandler(UIElement.PointerCanceledEvent, reset, true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, reset, true);
    }

    private void PopulateFavoriteBarContextMenu()
    {
        var menu = this._favoriteBarContextMenu;
        var anchor = menu.Target as Button;
        FavoriteItemId? item = anchor?.Tag is FavoriteItemId entryId ? entryId : null;
        menu.Items.Clear();
        var store = this._window.Favorites;
        var dialogs = new FavoriteDialogs(this._window.Dialogs);
        if (item is { IsFolder: false } bookmark)
        {
            Add("Open", Symbol.OpenFile, () => this._window.OpenFavoriteAsync(store.Bookmark(bookmark.Id)));
            Add("Open in new tab", Symbol.NewWindow,
                () => this._window.OpenFavoriteAsync(store.Bookmark(bookmark.Id), true));
        }
        else
        {
            Add("Add favorite…", Symbol.Add, () => FavoritesViewModel.AddFavoriteAsync(
                store, dialogs, this.ViewModel.Location.IsComputer ? null : this.ViewModel.Location, item?.Id));
            Add("New folder…", Symbol.NewFolder, () => FavoritesViewModel.CreateFolderAsync(store, dialogs, item?.Id));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (item is { } entry)
        {
            Add("Rename…", Symbol.Rename, () => FavoritesViewModel.RenameItemAsync(store, dialogs, entry));
            Add("Move to folder…", Symbol.MoveToFolder,
                () => FavoritesViewModel.MoveItemsToFolderAsync(store, dialogs, (FavoriteItemId[])[entry], null));
            var siblings = store.SiblingIds(entry);
            var index = Array.IndexOf(siblings, entry.Id);
            Add("Move earlier", Symbol.Back, () => Reorder(-1), index > 0);
            Add("Move later", Symbol.Forward, () => Reorder(1), index >= 0 && index < siblings.Length - 1);
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(entry.IsFolder ? "Remove folder…" : "Remove favorite…", Symbol.Delete,
                () => FavoritesViewModel.RemoveItemsAsync(store, dialogs, (FavoriteItemId[])[entry]));

            Task Reorder(int offset)
            {
                store.Reorder(entry, offset);
                return Task.CompletedTask;
            }
        }
        else
        {
            Add("Open Favorites sidebar", Symbol.DockRight, () =>
            {
                this._window.ShowTools(ToolKind.Favorites, true);
                return Task.CompletedTask;
            });
        }

        void Add(string label, Symbol symbol, Func<Task> action, bool enabled = true)
        {
            var command = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(symbol), IsEnabled = enabled };
            command.Click += async (_, _) =>
            {
                var buttons = this.FavoriteBarItems.Children;
                var previousIndex = anchor is null ? -1 : buttons.IndexOf(anchor);
                await this._window.RunFavoriteActionAsync(label, action);
                if (previousIndex >= 0 && anchor is not null && !buttons.Contains(anchor))
                {
                    this.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        if (this.IsLoaded && this.Settings.ShowFavoritesBar && buttons.Count > 0)
                        {
                            var target = buttons.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, item)) ??
                                         (Button)buttons[Math.Min(previousIndex, buttons.Count - 1)];
                            if (target.Visibility == Visibility.Collapsed)
                            {
                                target = this.FavoriteBarOverflowButton;
                            }

                            target.Focus(FocusState.Programmatic);
                        }
                    });
                }
            };
            menu.Items.Add(command);
        }
    }

    private (FavoriteItemId Item, string? Folder, int Offset)? AcceptFavoriteBarDrop(
        Button button, DragEventArgs args, bool preview = false)
    {
        if (this._favoritesBarDrag.GetSource(args) is not { } source || button.Tag is not FavoriteItemId target ||
            source == target || button.ActualWidth <= 0)
        {
            return null;
        }

        var position = args.GetPosition(button).X / button.ActualWidth;
        if (target.IsFolder && (!source.IsFolder || position is >= 0.25 and <= 0.75))
        {
            args.AcceptedOperation = DataPackageOperation.Move;
            if (preview)
            {
                args.DragUIOverride.Caption = $"Move into {AutomationProperties.GetName(button)}";
            }

            return (source, target.Id, 0);
        }

        var after = (position >= 0.5) ^ (button.FlowDirection == FlowDirection.RightToLeft);
        var offset = this._window.Favorites.ReorderOffset(source, target, after);
        if (offset == 0)
        {
            return null;
        }

        args.AcceptedOperation = DataPackageOperation.Move;
        if (preview)
        {
            var title = AutomationProperties.GetName(button);
            args.DragUIOverride.Caption = after ? $"Move after {title}" : $"Move before {title}";
        }

        return (source, null, offset);
    }
}
