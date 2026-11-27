// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Tools;

public sealed partial class FavoritesView : UserControl, IToolSurface
{
    private readonly ToolCommand _addCommand, _searchCommand, _folderActionsCommand;

    private readonly Dictionary<FavoriteItemId, TreeViewNode> _nodes = [];

    private readonly INotificationService _notifications;
    private readonly string _errorKey = "favorites-" + Guid.NewGuid().ToString("N");
    private NotificationHandle? _errorNotice;

    private ToolCommand? _previewCommand;
    private FavoriteRow? _renderedRoot;
    private bool _syncing, _loaded, _focusPending, _focusSelection;

    private ListView Results => this.ResultColumns.ListView;

    private TextBox Search => this.LocalControls.SearchBox;

    internal FavoritesViewModel ViewModel { get; }

    internal MenuFlyout FolderActionsMenu => this.ItemMenu;

    internal FavoritesView(
        FavoritesStore store,
        WindowDialogPresenter dialogs,
        bool compact,
        Func<RegistryLocation, bool, Task> navigate,
        Action<RegistryLocation> openBackground,
        Action<IToolSurface> showInPanel,
        ILogger logger,
        INotificationService notifications,
        ColumnLayoutPersistence? layouts = null)
    {
        this._notifications = notifications;
        this.ViewModel = new FavoritesViewModel(store, compact, new FavoriteDialogs(dialogs), navigate, openBackground);
        this.ViewModel.ActionFailed += (operation, error) => logger.LogFailure(LogLevel.Warning, operation, error);
        this.InitializeComponent();
        this.Results.ItemContainerStyle = (Style)Application.Current.Resources["ToolListItemStyle"];
        AutomationProperties.SetName(this.Results, "Tool items");
        AutomationProperties.SetAutomationId(this.Results, "Results");
        this.Results.DoubleTapped += this.Results_DoubleTapped;
        this.Results.AllowDrop = true;
        this.Results.DragOver += this.Results_DragOver;
        this.Results.Drop += this.Results_Drop;
        this.Results.PreviewKeyDown += this.Results_KeyDown;
        this.Results.SelectionChanged += this.Selection_Changed;
        this.Search.KeyDown += this.Search_KeyDown;
        this.LocalControls.PresentationChanged += (_, _) => this.ResultColumns.ShowColumns = this.LocalControls.ShowColumns;
        this.ResultColumns.ItemAutomationName = item => (item as FavoriteRow)?.AccessibleName;
        layouts?.Attach(this.ResultColumns, "Favorites");
        this._addCommand = new ToolCommand(this.ViewModel.AddLabel, Symbol.Add)
        {
            Command = this.ViewModel.AddCommand,
            ToolTip = this.ViewModel.AddHelp,
            IconSource = (IconSource)Application.Current.Resources["AddFavoriteIcon"]
        };
        this._searchCommand = new ToolCommand("Search favorites", Symbol.Find)
        {
            IsToggle = true, AutomationId = "FavoritesSearchButton", ShortcutText = "Ctrl+F"
        };
        this._searchCommand.Command = new RelayCommand(() => this.ShowSearch(this._searchCommand.IsChecked));
        this._folderActionsCommand = new ToolCommand("Folder actions", Symbol.More) { Flyout = this.ItemMenu };
        this._previewCommand = new ToolCommand("Show details", Symbol.View)
        {
            Command = this.ViewModel.ToggleDetailsCommand, IsToggle = true
        };
        this.LocalControls.SetPreviewCommand(this._previewCommand);
        this.PanelCommands = (ToolCommand[])
        [
            this._addCommand,
            new ToolCommand("New folder", Symbol.NewFolder) { Command = this.ViewModel.NewFolderCommand },
            this._searchCommand,
            this._folderActionsCommand
        ];
        this.PageHeader.Title = this.Tool.Choice().Title;
        this.PageHeader.Description = "Organize saved registry locations";
        this.PageHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact)
        {
            this.LocalControls.Visibility = this.Commands.Visibility = this.FolderHeader.Visibility = Visibility.Collapsed;
            this.LocalControls.ShowSearchOnly();
            this.LocalControls.Margin = new Thickness(0, 0, 0, 8);
            this.FolderPanel.RowSpacing = 0;
        }
        else
        {
            this.Layout.Style = (Style)Application.Current.Resources["ToolPageSurfaceStyle"];
            this.PageHeader.SetAction("Show in panel", "ShowInPanelButton", _ => showInPanel(this), Symbol.DockRight);
        }

        this.Results.ContextFlyout = this.BackgroundMenu;
        this.FavoriteTree.ItemContainerStyle = new Style(typeof(TreeViewItem))
        {
            Setters = { new Setter(ContextFlyoutProperty, this.ItemMenu) }
        };
        this.Results.ItemContainerStyle = new Style(typeof(ListViewItem))
        {
            BasedOn = this.Results.ItemContainerStyle, Setters = { new Setter(ContextFlyoutProperty, this.ItemMenu) }
        };

        MiddleClick.Attach(
            this.OpenNewTabButton,
            _ => this.ViewModel.Selected is [{ IsFolder: false } item] ? this.ViewModel.Store.Bookmark(item.Id) : null,
            favorite => this.ViewModel.OpenBackgroundCommand.Execute(new FavoriteItemId(favorite.Id, false)));
        MiddleClick.Attach(
            this.Results, args => !this.ViewModel.IsBusy
                                  && MiddleClick.Container<ListViewItem>(this.Results, args)?.Content is FavoriteRow
                                  {
                                      Key.IsFolder: false
                                  } row
                ? row
                : null, row => this.ViewModel.OpenBackgroundCommand.Execute(row.Key));
        MiddleClick.Attach(
            this.FavoriteTree,
            args => !this.ViewModel.IsBusy
                    && MiddleClick.Container<TreeViewItem>(this.FavoriteTree, args) is { } container
                    && this.FavoriteTree.NodeFromContainer(container)?.Content is FavoriteRow { Key.IsFolder: false } row
                ? row
                : null, row => this.ViewModel.OpenBackgroundCommand.Execute(row.Key));
        this.UpdateSurfaceLayout();
    }

    public ToolKind Tool => ToolKind.Favorites;

    public Button MenuButton => this.PageHeader.MenuButton;

    public IReadOnlyList<ToolCommand> PanelCommands { get; }

    void IToolSurface.CopyContextFrom(IToolSurface source)
    {
        this.ViewModel.CopyFrom(((FavoritesView)source).ViewModel);
        this.LocalControls.ShowColumns = ((FavoritesView)source).LocalControls.ShowColumns;
    }

    public void RequestFocus(bool preferSelection = false)
    {
        this._focusPending = true;
        this._focusSelection = preferSelection;
        if (!this._loaded)
        {
            return;
        }

        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!this._loaded || !this._focusPending)
                {
                    return;
                }

                this._focusPending = false;
                if (this._focusSelection && this.ViewModel.Compact && this.FavoriteTree.SelectedNode is { } node)
                {
                    (this.FavoriteTree.ContainerFromNode(node) as Control ?? this.FavoriteTree).Focus(FocusState.Keyboard);
                }
                else if (this._focusSelection && this.Results.SelectedItem is { } row)
                {
                    this.Results.ScrollIntoView(row);
                    this.Results.Focus(FocusState.Keyboard);
                }
                else if (this.ViewModel.Compact && this.LocalControls.Visibility == Visibility.Collapsed)
                {
                    this.FavoriteTree.Focus(FocusState.Keyboard);
                }
                else
                {
                    this.Search.Focus(FocusState.Keyboard);
                }
            });
    }

    internal void ApplyListSpacing(double spacing)
    {
        ListDensity.SetSpacing(this.Results, spacing);
    }

    public static Visibility TextVisibility(string text)
    {
        return text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ItemMenu_Opening(object sender, object args)
    {
        if (this.ItemMenu.Target is ListViewItem { Content: FavoriteRow row })
        {
            if (!this.Results.SelectedItems.Contains(row))
            {
                this.Results.SelectedItem = row;
            }

            this.MenuNewFolder.Visibility = Visibility.Collapsed;
        }
        else
        {
            var node = this.ItemMenu.Target is TreeViewItem container
                ? this.FavoriteTree.NodeFromContainer(container)
                : this.FavoriteTree.SelectedNode;
            if (node?.Content is FavoriteRow treeRow)
            {
                this.ViewModel.SelectTree(treeRow.Key);
            }

            this.MenuNewFolder.Visibility = this.ViewModel.HasSelectedBookmark ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    internal void SetCurrentLocation(RegistryLocation? location)
    {
        this.ViewModel.CurrentLocation = location;
        this._addCommand.ToolTip = this.ViewModel.AddHelp;
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FavoritesViewModel.Error))
        {
            this.ShowError();
        }
        else if (args.PropertyName == nameof(FavoritesViewModel.ShowDetails))
        {
            this.UpdateDetails();
        }
    }

    private void ShowError()
    {
        this._errorNotice = StatusNotifications.UpdateError(
            this._notifications, this._errorNotice, "Could not complete the action", this.ViewModel.Error, this._errorKey);
    }

    private static bool TryExecute(ICommand? command)
    {
        if (command?.CanExecute(null) != true)
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        if (this._loaded)
        {
            return;
        }

        this._loaded = true;
        this.ViewModel.ItemsChanged += this.ItemsChanged;
        this.ViewModel.SelectionChanged += this.ModelSelectionChanged;
        this.ViewModel.PropertyChanged += this.ModelPropertyChanged;
        this.ViewModel.Activate();
        this.ShowError();
        if (this._focusPending)
        {
            this.RequestFocus(this._focusSelection);
        }
    }

    private void View_Unloaded(object sender, RoutedEventArgs args)
    {
        this._loaded = this._focusPending = false;
        this.ViewModel.Deactivate();
        this.ViewModel.PropertyChanged -= this.ModelPropertyChanged;
        this.ViewModel.ItemsChanged -= this.ItemsChanged;
        this.ViewModel.SelectionChanged -= this.ModelSelectionChanged;
    }

    private void ItemsChanged(object? sender, EventArgs args)
    {
        this._itemDrag.Clear();
        this._syncing = true;
        try
        {
            if (this.Search.Text != this.ViewModel.Query)
            {
                this.Search.Text = this.ViewModel.Query;
            }

            this.Results.ItemsSource = this.ViewModel.Rows;
            if (!ReferenceEquals(this._renderedRoot, this.ViewModel.Root))
            {
                this._nodes.Clear();
                this.FavoriteTree.RootNodes.Clear();
                foreach (var row in this.ViewModel.Root.Children)
                {
                    this.FavoriteTree.RootNodes.Add(this.CreateNode(row));
                }

                this._renderedRoot = this.ViewModel.Root;
            }

            this.SyncSelection();
        }
        finally
        {
            this._syncing = false;
        }

        var searching = this.ViewModel.Query.Length > 0;
        var rows = this.ViewModel.Rows.Count;
        this.CollectionTitle.Text = searching ? "Search results" : this.ViewModel.FolderTitle;
        this.RootFavorites.Visibility = this.ViewModel.FolderId is not null || searching ? Visibility.Visible : Visibility.Collapsed;
        this.Empty.Visibility = rows == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.TreeEmpty.Visibility = this.ViewModel.Root.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.EmptyTitle.Text = searching ? "No matches" : "No favorites yet";
        this.TreeEmptyTitle.Text = this.ViewModel.Compact ? this.EmptyTitle.Text : "No folders";
        this.TreeEmptyDescription.Text = !this.ViewModel.Compact ? "Create a folder to organize favorites." :
            searching ? "Try another name or saved location." : "Add the current key or create a folder to get started.";
        this.EmptyDescription.Text = searching
            ? "Try another name or registry path."
            : "Add a favorite or create a folder to organize saved locations.";
        if (!searching && rows == 0 && (this.ViewModel.Store.Favorites.Count > 0 || this.ViewModel.Store.Folders.Count > 0))
        {
            var hasChildren = this.ViewModel.Store.Folders.Any(folder => folder.ParentId == this.ViewModel.FolderId);
            this.EmptyTitle.Text = hasChildren ? "No favorites in this folder" : "This folder is empty";
            this.EmptyDescription.Text = hasChildren
                ? "Choose a subfolder in the tree, or add a favorite here."
                : "Add a favorite or create a subfolder here.";
        }

        this.UpdateDetails();
    }

    private TreeViewNode CreateNode(FavoriteRow row)
    {
        var node = new TreeViewNode
        {
            Content = row,
            IsExpanded = row.Key.IsFolder
                         && (this.ViewModel.Compact && this.ViewModel.Query.Length > 0
                             || this.ViewModel.ExpandedFolders.Contains(row.Key.Id))
        };
        this._nodes.Add(row.Key, node);
        foreach (var child in row.Children)
        {
            node.Children.Add(this.CreateNode(child));
        }

        return node;
    }

    private void SyncSelection()
    {
        var model = this.ViewModel;
        var treeKey = model.TreeSelection && model.Selected is [var key] ? key : new FavoriteItemId(model.FolderId ?? string.Empty, true);
        if (this._nodes.TryGetValue(treeKey, out var node))
        {
            if (model.TreeSelection && model.Selected.Count > 0 && this.FavoriteTree.SelectedNode != node)
            {
                for (var parent = node.Parent; parent?.Content is FavoriteRow row; parent = parent.Parent)
                {
                    if (!parent.IsExpanded)
                    {
                        parent.IsExpanded = true;
                    }

                    if (!model.Compact || model.Query.Length == 0)
                    {
                        model.ExpandedFolders.Add(row.Key.Id);
                    }
                }
            }

            if (this.FavoriteTree.SelectedNode != node)
            {
                this.FavoriteTree.SelectedNode = node;
            }
        }
        else
        {
            this.FavoriteTree.SelectedNode = null;
        }

        var selected = model.TreeSelection ? [] : model.Selected.ToHashSet();
        foreach (var row in this.Results.SelectedItems.OfType<FavoriteRow>().ToArray())
        {
            if (!selected.Contains(row.Key))
            {
                this.Results.SelectedItems.Remove(row);
            }
        }

        foreach (var row in model.Rows)
        {
            if (selected.Contains(row.Key) && !this.Results.SelectedItems.Contains(row))
            {
                this.Results.SelectedItems.Add(row);
            }
        }
    }

    private void ModelSelectionChanged(object? sender, EventArgs args)
    {
        this._syncing = true;
        try
        {
            this.SyncSelection();
        }
        finally
        {
            this._syncing = false;
        }

        this.UpdateDetails();
    }

    private void UpdateDetails()
    {
        var model = this.ViewModel;
        this.Count.Text = $"{model.Rows.Count:N0} {(model.Rows.Count == 1 ? "item" : "items")}"
                          + (model.Selected.Count > 0 ? $" · {model.Selected.Count:N0} selected" : string.Empty);
        this.Details.Visibility = model.ShowDetails ? Visibility.Visible : Visibility.Collapsed;
        if (this._previewCommand is { } preview)
        {
            preview.IsChecked = model.ShowDetails;
        }

        if (model.ShowDetails)
        {
            if (model.Selected is [{ IsFolder: false } item])
            {
                var bookmark = model.Store.Bookmark(item.Id);
                this.Details.Show(
                    bookmark.Name, bookmark.Address,
                    $"{(bookmark.Is32Bit ? "32-bit" : "64-bit")}\nFolder: {model.Store.FolderPath(bookmark.FolderId)}");
            }
            else if (model.Selected is [var folder])
            {
                this.Details.Show(model.Store.Folders.First(f => f.Id == folder.Id).Name, model.Store.FolderPath(folder.Id), string.Empty);
            }
            else if (model.Selected.Count > 1)
            {
                this.Details.Show(
                    $"{model.Selected.Count:N0} items selected", string.Empty, "Use the collection actions to organize these items.");
            }
            else
            {
                this.Details.Show("Select an item", string.Empty, "Saved location details appear here.");
            }
        }

        this.UpdateSurfaceLayout();
        var label = model.Compact && model.HasSelectedBookmark ? "Favorite actions" : "Folder actions";
        AutomationProperties.SetName(this.FolderActions, label);
        ToolTipService.SetToolTip(this.FolderActions, label);
        this._folderActionsCommand.Label = label;
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateSurfaceLayout();
    }

    private void UpdateSurfaceLayout()
    {
        var width = this.Layout.ActualWidth;
        var compact = this.ViewModel.Compact;
        this.Layout.Padding = ToolPageLayout.GetPadding(width, compact);
        this.TreeWidth.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(width < 760 ? 180 : 220);
        this.ListWidth.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        this.Collection.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var beside = !compact && width - this.Layout.Padding.Left - this.Layout.Padding.Right >= 1000;
        this.InspectorWidth.Width = this.ViewModel.ShowDetails && beside ? new GridLength(285) : new GridLength(0);
        this.InspectorHeight.Height = this.ViewModel.ShowDetails && !beside ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(this.Details, beside ? 2 : 0);
        Grid.SetRow(this.Details, beside ? 0 : 1);
        Grid.SetColumnSpan(this.Details, beside ? 1 : 3);
        this.Workspace.ColumnSpacing = 0;
        this.Workspace.RowSpacing = this.ViewModel.ShowDetails && !beside ? 12 : 0;
        this.FolderPanel.Margin = new Thickness(0, 0, compact ? 0 : 16, 0);
        this.Details.Margin = new Thickness(beside ? 16 : 0, 0, 0, 0);
        this.DetailsSplitter.Arrange(
            this.Details, this.InspectorWidth, this.InspectorHeight, beside, this.ViewModel.ShowDetails && !compact,
            () => this.ListWidth.ActualWidth + this.InspectorWidth.ActualWidth);
    }

    private void Search_Changed(object sender, TextChangedEventArgs args)
    {
        if (this.ViewModel.Compact && this.Search.Text.Length > 0)
        {
            this._searchCommand.IsChecked = true;
            this.LocalControls.Visibility = Visibility.Visible;
        }

        if (this._loaded && !this._syncing)
        {
            this.ViewModel.Search(this.Search.Text);
        }
    }

    internal void ShowSearch(bool visible)
    {
        if (this.ViewModel.Compact)
        {
            this._searchCommand.IsChecked = visible;
            this.LocalControls.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                this.Search.Text = string.Empty;
                this.FavoriteTree.Focus(FocusState.Keyboard);
                return;
            }
        }

        this.Search.Focus(FocusState.Keyboard);
    }

    private void Search_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ShowSearch(true);
        args.Handled = true;
    }

    private void Search_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (this.ViewModel.Compact && args.Key == VirtualKey.Escape)
        {
            this.ShowSearch(false);
            args.Handled = true;
        }
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!this._syncing && this._loaded && (args.AddedItems.Count > 0 || !this.ViewModel.TreeSelection))
        {
            this.ViewModel.SelectRows(this.Results.SelectedItems.OfType<FavoriteRow>().Select(row => row.Key));
        }
    }

    private void RootFavorites_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.OpenFolder(string.Empty);
    }

    private void FavoriteTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (!this._loaded || this._syncing || args.AddedItems is not [TreeViewNode { Content: FavoriteRow row } node])
        {
            return;
        }

        this.QueueTreeSelection(node, row);
    }

    private void QueueTreeSelection(TreeViewNode node, FavoriteRow row, bool activate = false)
    {
        // Let WinUI finish changing selection before refreshing the view.
        this.DispatcherQueue.TryEnqueue(
            () =>
            {
                if (this._loaded
                    && this._nodes.GetValueOrDefault(row.Key) == node
                    && (activate || Equals(this.FavoriteTree.SelectedItem, node))
                    && (this.ViewModel.Compact || row.Key.Id != this.ViewModel.FolderId || (activate && this.ViewModel.Query.Length > 0)))
                {
                    this.ViewModel.SelectTree(row.Key);
                }
            });
    }

    private void FavoriteFolder_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is FrameworkElement
            { DataContext: TreeViewNode { Content: FavoriteRow { Key.IsFolder: true } row } node })
        {
            this.QueueTreeSelection(node, row, activate: true);
        }
    }

    private void FavoriteTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!this._syncing && (!this.ViewModel.Compact || this.ViewModel.Query.Length == 0) && args.Node.Content is FavoriteRow row)
        {
            this.ViewModel.ExpandedFolders.Add(row.Key.Id);
        }
    }

    private void FavoriteTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (!this._syncing && (!this.ViewModel.Compact || this.ViewModel.Query.Length == 0) && args.Node.Content is FavoriteRow row)
        {
            this.ViewModel.ExpandedFolders.Remove(row.Key.Id);
        }
    }

    private void FavoriteTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode { Content: FavoriteRow row })
        {
            return;
        }

        if (this.ViewModel.Compact && !row.Key.IsFolder)
        {
            this.ViewModel.SelectTree(row.Key);
            TryExecute(this.ViewModel.OpenCommand);
        }
        else if (!this.ViewModel.Compact && row.Key.IsFolder && this.ViewModel.Query.Length > 0)
        {
            this.ViewModel.SelectTree(row.Key);
        }
    }

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        TryExecute(this.ViewModel.OpenCommand);
    }

    private string OpenShortcut(bool compact)
    {
        return compact ? string.Empty : "Enter";
    }

    private void Results_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        ICommand? command = args.Key switch
        {
            VirtualKey.Enter when ReferenceEquals(sender, this.Results) || !this.ViewModel.Compact => this.ViewModel.OpenCommand,
            VirtualKey.F2 => this.ViewModel.RenameCommand,
            VirtualKey.Delete => this.ViewModel.RemoveCommand,
            _ => null
        };
        if (TryExecute(command))
        {
            args.Handled = true;
        }
    }

    private void InspectDetails_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!this.ViewModel.Compact && TryExecute(this.ViewModel.ToggleDetailsCommand))
        {
            args.Handled = true;
        }
    }
}
