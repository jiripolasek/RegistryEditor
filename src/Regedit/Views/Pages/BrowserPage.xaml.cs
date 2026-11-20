// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using System.Windows.Input;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Controls.Browser;
using JPSoftworks.Regedit.Controls.Omnibox;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage : Page
{
    private readonly MainWindow _window;
    private readonly RegistryDialogs _dialogs;
    private readonly string _notificationKey = "browser-" + Guid.NewGuid().ToString("N");
    private readonly OperationNotification _exportNotification;
    private readonly OperationNotification _snapshotNotification;
    private bool _initialized;
    private RegistryLocation? _treeContextLocation;
    private bool _keepRenameRejectionNotice;
    private bool _closed;
    private NotificationHandle? _notification;
    private Notification? _notificationContent;

    public BrowserViewModel ViewModel { get; }

    public AppSettings Settings => this._window.Settings;

    internal SettingsViewModel Preferences => this._window.Preferences;

    internal Button MenuButton => this.GlobalMenuButton;

    public BrowserPage(MainWindow window, BrowserViewModel viewModel, string? selectedValue = null)
    {
        this._window = window;
        this._dialogs = new RegistryDialogs(window);
        this.ViewModel = viewModel;
        viewModel.AddFavoriteRequested = this.AddFavoriteAsync;
        this.InitializeComponent();
        this.InitializeToolbar();
        this.SearchSplitter.DragDelta += (_, args) => this.ResizeSearch(args.HorizontalChange);
        this.PaneSplitter.DragDelta += (_, args) => this.ResizeTree(args.HorizontalChange);
        this.PaneSplitter.DragCompleted += (_, _) =>
            this.ViewModel.SetTreeLayout(this.TreeColumn.Width.Value, this.ViewModel.Layout.TreeHidden);
        this.GlobalMenuButton.Click += this._window.ApplicationMenu_Click;
        this.TreePane.Initialize(viewModel, (MenuFlyout)this.Resources["RegistryTreeMenu"]);
        this.EntryList.Initialize(
            viewModel, this.Preferences, (MenuFlyout)this.Resources["RegistryEntryMenu"], (MenuFlyout)this.Resources["RegistryListMenu"]);
        this.EntryList.RenameRejected += error =>
        {
            this.ShowError(error, "Couldn't rename the item");
            this._keepRenameRejectionNotice = true;
        };
        this.TreePane.OpenInBackground += this._window.ViewModel.OpenInBackground;
        this.EntryList.OpenInBackground += entry => this._window.ViewModel.OpenBrowser(this.EntryLocation(entry), entry.Value?.Name, false);
        viewModel.Search.FindRequested += this.ShowFindDialogAsync;
        viewModel.Search.Notice += this.ShowNotice;
        viewModel.Search.StatusChanged += message =>
        {
            var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other, AutomationNotificationProcessing.CurrentThenMostRecent, message, "RegistrySearchStatus");
        };
        viewModel.Branches.Dialogs = this._dialogs;
        viewModel.Branches.Notice += this.ShowNotice;
        this._exportNotification = new OperationNotification(
            window.Notifications, this._notificationKey + ":export", viewModel.Branches.ExportCommand.Cancel);
        viewModel.Branches.OperationStarted += this._exportNotification.Start;
        viewModel.Branches.ExportNotice += this._exportNotification.ShowResult;
        viewModel.Branches.ExportCompleted += this._exportNotification.AddOpenFolderAction;
        viewModel.Branches.HiveSaved += file =>
        {
            if (!this._closed && this._notificationContent is not null)
            {
                this._notificationContent = StatusNotifications.WithOpenFolderAction(this._notificationContent, file);
                this._notification?.Update(this._notificationContent);
            }
        };
        viewModel.Branches.ExportCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Branches.ExportCommand.IsRunning) && !viewModel.Branches.ExportCommand.IsRunning)
            {
                this._exportNotification.Complete();
            }
        };
        viewModel.Branches.Failed += ex => this._window.Logger.LogFailure(LogLevel.Warning, "Registry branch operation", ex);
        viewModel.Snapshots.SaveFileRequested += () => this._dialogs.PickSaveAsync(
            "Registry snapshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"), ".regsnap", "Registry branch snapshot");
        viewModel.Snapshots.OpenFileRequested += () => this._dialogs.PickOpenAsync(".regsnap");
        viewModel.Snapshots.RemoteConfirmationRequested += root => this.ConfirmAsync(
            "Compare remote snapshot?", $"Connect to {root.Machine} using your Windows credentials and read {root.Path}?",
            "Connect and compare");
        viewModel.Snapshots.ComparisonRequested += comparison => this.GuardAsync(
            "Couldn't open the comparison", "Opening the comparison was canceled", async () =>
            {
                var content = new SnapshotComparisonView(
                    new SnapshotComparisonViewModel(
                        comparison, () => this._dialogs.PickSaveAsync("Registry differences", ".reg", "Registry differences"),
                        ClipboardText.Set));
                await content.ShowAsync(this._window.Root, this._window.Dialogs);
            });
        this._snapshotNotification = new OperationNotification(
            window.Notifications, this._notificationKey + ":snapshot", () => viewModel.Snapshots.CancelCommand.Execute(null));
        viewModel.Snapshots.Notice += this._snapshotNotification.ShowResult;
        viewModel.Snapshots.OperationStarted += this._snapshotNotification.Start;
        viewModel.Snapshots.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Snapshots.IsBusy) && !viewModel.Snapshots.IsBusy)
            {
                this._snapshotNotification.Complete();
            }
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(viewModel.Inspector) or nameof(viewModel.IsInspectorOpen))
            {
                this.UpdateValueInspector();
            }
        };
        viewModel.InspectorRequested += this.ShowValueInspectorAsync;
        this.UpdateValueInspector();
        viewModel.NotesRequested += () => this._window.ShowNotesForKey(viewModel.Location);
        viewModel.ValueSelectionRequested += this.SelectValue;
        viewModel.IsAdministrator = window.IsAdministrator;
        viewModel.ElevationRequested += window.RegistryMenu.ElevateLocationAsync;
        viewModel.EditPromptRequested += this.ShowEditPromptAsync;
        viewModel.ConfirmHistoryChange = (title, message) => this.ConfirmAsync(title, message, "Check and apply");
        viewModel.HistoryJournalRequested += id => this.GuardAsync(
            "Couldn't open the Journal", "Opening the Journal was canceled", () => this._window.ShowHistoryJournalAsync(id));
        viewModel.HistoryNotice += (title, message, needsReview) =>
        {
            this.ShowMessage(
                title, message, needsReview ? NoticeKind.Warning : NoticeKind.Success, (NotificationAction[])
                [
                    new NotificationAction(
                        needsReview ? "Show in Journal" : "Go to change", _ =>
                        {
                            (needsReview ? viewModel.ShowHistoryJournalCommand : viewModel.GoToHistoryChangeCommand).Execute(null);
                            return Task.CompletedTask;
                        })
                ]);
        };
        viewModel.CopyTextRequested += this.CopyText;
        viewModel.EditFailed += ex => this._window.Logger.LogFailure(LogLevel.Warning, "Registry edit", ex);
        viewModel.DetailsLoadFailed += ex => this._window.Logger.LogFailure(LogLevel.Warning, "Load registry list details", ex);
        viewModel.FileRevealFailed += ex => this._window.Logger.LogFailure(LogLevel.Warning, "Reveal registry file", ex);
        viewModel.WebSearchFailed += ex => this._window.Logger.LogFailure(LogLevel.Warning, "Web search", ex);
        this.TreePane.LoadFailed += ex =>
        {
            this._window.Logger.LogFailure(LogLevel.Warning, "Load registry tree", ex);
            this.ShowError(ex.Message, "Couldn't load the registry tree");
        };
        this.EntryList.FilterTyped += character =>
        {
            this.FilterArea.Visibility = Visibility.Visible;
            this.FilterBox.Focus(FocusState.Keyboard);
            this.ViewModel.Filter += character;
            this.FilterBox.SelectionStart = this.FilterBox.Text.Length;
        };
        viewModel.RegistryChanged += (_, _) => this._window.JournalHistory.RegistryActivityCompleted();
        this.AddressBar.SuggestionProvider = (text, token) => viewModel.Addresses.SuggestAsync(text, viewModel.Location.View, token);
        this.AddressBar.ChildrenProvider = (address, token) => viewModel.Addresses.ChildrenAsync(address, viewModel.Location, token);
        this.ApplyViewSettings();
        this.ViewModel.Navigated += this.ViewModel_Navigated;
        if (selectedValue is { } name)
        {
            var initialLocation = this.ViewModel.Location;
            var initialNavigation = this.ViewModel.NavigationVersion;
            this.ViewModel.Navigated += SelectInitialValue;

            void SelectInitialValue(object? sender, EventArgs args)
            {
                this.ViewModel.Navigated -= SelectInitialValue;
                if (initialLocation.IsSameLocation(this.ViewModel.Location) && initialNavigation == this.ViewModel.NavigationVersion)
                {
                    this.SelectValue(name);
                }
            }
        }

        this.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(this.ViewModel.Error) && this.ViewModel.Error is { } error)
            {
                this.ShowError(error);
            }

            if (args.PropertyName == nameof(this.ViewModel.VisibleEntryCount))
            {
                this.FilterCount.Text = $"{this.ViewModel.VisibleEntryCount:N0} shown";
            }

            if (args.PropertyName is nameof(BrowserViewModel.Filter) or nameof(BrowserViewModel.Location))
            {
                var empty = this.ViewModel.Filter.Length == 0;
                var focused = this.XamlRoot is { } root
                              && IsInside(FocusManager.GetFocusedElement(root) as DependencyObject, this.FilterArea);
                var keepEditing = focused && args.PropertyName == nameof(BrowserViewModel.Filter);
                if (empty && focused && !keepEditing)
                {
                    this.EntryList.FocusList(FocusState.Keyboard);
                }

                this.FilterArea.Visibility = empty && !keepEditing ? Visibility.Collapsed : Visibility.Visible;
            }
        };
        this.AddShortcut(VirtualKey.L, VirtualKeyModifiers.Control, this.AddressBar.BeginEdit);
        this.AddShortcut(VirtualKey.D, VirtualKeyModifiers.Menu, this.AddressBar.BeginEdit);
        this.AddShortcut(
            VirtualKey.L, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift, () => this.SyncTree_Click(this, new RoutedEventArgs()));
        this.AddShortcut(
            VirtualKey.H, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
            {
                if (!this._window.Dialogs.IsOpen && viewModel.SwitchHiveCommand.CanExecute(null))
                {
                    viewModel.SwitchHiveCommand.Execute(null);
                }
            });
        this.AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => this.QuickSearch.Focus(FocusState.Keyboard));
        this.AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, viewModel.Search.FindCommand);
        this.AddShortcut(
            VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => this.Filter_Click(this, new RoutedEventArgs()));
        var favoriteShortcut = new KeyboardAccelerator { Key = VirtualKey.D, Modifiers = VirtualKeyModifiers.Control };
        favoriteShortcut.Invoked += this.Favorite_Invoked;
        this.LayoutRoot.KeyboardAccelerators.Add(favoriteShortcut);
        this.AddShortcut(
            VirtualKey.P, VirtualKeyModifiers.Menu, () =>
            {
                if (!this._window.Dialogs.IsOpen)
                {
                    this.ViewModel.IsInspectorOpen = !this.ViewModel.IsInspectorOpen;
                    this.EntryList.FocusList(FocusState.Keyboard);
                }
            });
        this.AddShortcut(VirtualKey.F5, VirtualKeyModifiers.None, viewModel.RefreshCommand);
        this.LayoutRoot.KeyDown += this.Browser_KeyDown;
        this.AccessStatus.Content = this._window.IsAdministrator ? "Administrator" : "Standard access";
        this.AccessModePrompt.Text = this._window.IsAdministrator
            ? "Open this location in a new standard-access window using the desktop user's account? The current window will stay open."
            : "Open this location in a new administrator window? Windows will ask for permission. The current window will stay open.";
        this.AccessModeLaunchButton.Content = this._window.IsAdministrator ? "Open with standard access" : "Open as administrator";
        this.AccessModeLaunchButton.Command = this._window.IsAdministrator
            ? this._window.RegistryMenu.OpenStandardAccessCommand
            : this._window.RegistryMenu.ElevateCommand;
    }

    public void SelectValue(string name)
    {
        this.EntryList.SelectEntry(
            this.ViewModel.Entries.FirstOrDefault(entry => entry.Value?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true));
    }

    private void RegistryViewMenu_Opening(object sender, object args)
    {
        this.StatusRegistry32Menu.IsChecked = this.ViewModel.Is32Bit;
        this.StatusRegistry64Menu.IsChecked = !this.ViewModel.Is32Bit;
    }

    public string RegistryViewLabel(bool is32Bit)
    {
        return is32Bit ? "32-bit registry" : "64-bit registry";
    }

    private void AccessModeLaunch_Click(object sender, RoutedEventArgs args)
    {
        this.AccessModeFlyout.Hide();
    }

    private async void Favorite_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(this.XamlRoot) as DependencyObject;
        if (this._window.Dialogs.IsOpen || focused is TextBox or RichEditBox or PasswordBox)
        {
            return;
        }

        var treeMenuOpen = ((MenuFlyout)this.Resources["RegistryTreeMenu"]).IsOpen;
        var treeLocation = treeMenuOpen ? this._treeContextLocation : this.TreePane.SelectedKey;
        var target = IsInside(focused, this.EntryList) || ((MenuFlyout)this.Resources["RegistryEntryMenu"]).IsOpen
            ?
            this.ViewModel.SelectedFavoriteLocation
            : IsInside(focused, this.TreePane) || treeMenuOpen
                ? treeLocation ?? this.ViewModel.Location
                : this.ViewModel.Location;
        if (this.ViewModel.ToggleFavoriteCommand.CanExecute(target))
        {
            args.Handled = true;
            await this.ViewModel.ToggleFavoriteCommand.ExecuteAsync(target);
        }
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, ICommand command)
    {
        this.AddShortcut(
            key, modifiers, () =>
            {
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }
            });
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        this.LayoutRoot.KeyboardAccelerators.Add(accelerator);
    }

    public void ApplyViewSettings()
    {
        this.UpdateToolbar();
        this.AddressBar.IsTextMode = this.Settings.AlwaysShowAddressAsText;
        this.StatusBar.Visibility = this.Settings.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        this.ViewModel.ShowKeysInList = this.Settings.ShowKeysInList;
        this.ViewModel.ShowParentKeyInList = this.Settings.ShowParentKeyInList;
        this.Commands.Visibility = this.Settings.ShowListToolbar ? Visibility.Visible : Visibility.Collapsed;
        this.CommandRow.Padding = this.Settings.ShowListToolbar ? new Thickness(6, 4, 6, 4) : new Thickness(0);
        this.EntryList.ApplySettings(this.Preferences.DataFont, this.Settings.ShowValuePreview);
        this.TreePane.SetTracking(this.Settings.AutoSyncTree);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (this._initialized)
        {
            return;
        }

        this._initialized = true;
        this.UpdateAddressBar();
        var navigation = this.ViewModel.NavigationVersion;
        if (!await this.TreePane.InitializeNodesAsync() || navigation != this.ViewModel.NavigationVersion)
        {
            return;
        }

        if (this.ViewModel.HasSnapshot)
        {
            this.TreePane.SynchronizeLocation();
        }
        else
        {
            await this.ViewModel.NavigateAsync(this.ViewModel.Location, false);
        }

        if (this.Settings.LoadError is { } error)
        {
            this.ShowError(error, "Couldn't load settings");
        }
    }

    private void ViewModel_Navigated(object? sender, EventArgs args)
    {
        // The click that dismisses a rejected rename can also navigate away from its key.
        if (!this._keepRenameRejectionNotice && this._notificationContent?.Actions.Count is not > 0)
        {
            this._notification?.Dismiss();
        }

        this.UpdateAddressBar();
    }

    private async void RegistryTreeMenu_Opening(object sender, object args)
    {
        this._treeContextLocation = this.TreePane.SelectContextNode(((MenuFlyout)sender).Target);
        UpdateHiveSwitchMenu(this.TreeSwitchHiveMenu, this._treeContextLocation);
        this.TreeWebSearchMenu.IsEnabled = this._treeContextLocation is { IsComputer: false };
        this.TreeExportMenu.CommandParameter = this.TreePermissionsMenu.CommandParameter = this._treeContextLocation;
        this.TreeRenameMenu.CommandParameter = this.TreeDeleteMenu.CommandParameter = this._treeContextLocation;
        if (this._treeContextLocation is not { } location)
        {
            return;
        }

        this.PopulateCopyPathMenu(this.TreeCopyPathAsMenu.Items, location);
        this.TreeFavoriteMenu.Text = this._window.Favorites.Contains(location) ? "Remove from favorites" : "Add to favorites…";
        await this.ViewModel.NavigateAsync(location);
    }

    private static bool IsInside(DependencyObject? element, DependencyObject parent)
    {
        while (element is not null)
        {
            if (element == parent)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void UpdateAddressBar()
    {
        var location = this.ViewModel.Location;
        var breadcrumbs = new List<OmniboxItem>();
        for (RegistryLocation? ancestor = location; ancestor is not null; ancestor = ancestor.GetNavigationParent())
        {
            breadcrumbs.Insert(0, new OmniboxItem(ancestor.Name, ancestor.Address));
        }

        breadcrumbs[^1] = breadcrumbs[^1] with { HasChildren = this.ViewModel.HasSubKeys };
        this.AddressBar.LocationIconSource = new FontIconSource { Glyph = RegistryTreePane.TreeGlyph(location), FontSize = 16 };
        this.AddressBar.SetLocation(location.Address, breadcrumbs);
        this.QuickSearch.PlaceholderText = location.IsComputer ? "Search all hives" : $"Search {location.Name} and subkeys";
        ToolTipService.SetToolTip(this.QuickSearch, $"{location.Address} · {this.ViewModel.Connection}");
    }

    private async void Address_NavigationRequested(object? sender, OmniboxNavigationRequestedEventArgs args)
    {
        await this.GuardAsync(
            "Couldn't open the registry key", "Navigation canceled",
            () => this.ViewModel.NavigateAsync(RegistryLocation.Parse(args.Address, this.ViewModel.Location.View)));
    }


    private void Page_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateToolbar();
        this.UpdateNavigationPane(args.NewSize.Width);
    }

    private void UpdateNavigationPane(double width)
    {
        var showTree = !this.ViewModel.Layout.TreeHidden && width >= 520;
        var treeWidth = Math.Min(this.ViewModel.Layout.TreeWidth, Math.Max(160, width - 300));
        this.TreeColumn.Width = new GridLength(showTree ? treeWidth : 0);
        this.SplitterColumn.Width = new GridLength(showTree ? 6 : 0);
        this.NavigationPane.Visibility = this.PaneSplitter.Visibility = showTree ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResizeSearch(double delta)
    {
        var total = this.AddressColumn.ActualWidth + this.SearchColumn.ActualWidth;
        if (total < 360)
        {
            return;
        }

        var width = total * this.AddressColumn.Width.Value / (this.AddressColumn.Width.Value + this.SearchColumn.Width.Value);
        width = PaneResize.Apply(width, delta, 180, total - 180);
        this.AddressColumn.Width = new GridLength(width, GridUnitType.Star);
        this.SearchColumn.Width = new GridLength(total - width, GridUnitType.Star);
    }

    private void ResizeTree(double delta)
    {
        var maximum = Math.Min(600, Math.Max(160, this.ContentGrid.ActualWidth - 300));
        this.TreeColumn.Width = new GridLength(PaneResize.Apply(this.TreeColumn.Width.Value, delta, 160, maximum));
    }

    private void Splitter_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var delta = args.Key switch { VirtualKey.Left => -24, VirtualKey.Right => 24, _ => 0 };
        if (delta == 0)
        {
            return;
        }

        args.Handled = true;
        if (ReferenceEquals(sender, this.PaneSplitter))
        {
            this.ResizeTree(delta);
            this.ViewModel.SetTreeLayout(this.TreeColumn.Width.Value, this.ViewModel.Layout.TreeHidden);
        }
        else
        {
            this.ResizeSearch(delta);
        }
    }

    private void TogglePane_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.SetTreeLayout(this.ViewModel.Layout.TreeWidth, !this.ViewModel.Layout.TreeHidden);
        this.UpdateNavigationPane(this.LayoutRoot.ActualWidth);
    }

    private void RegistryEntryMenu_Opening(object sender, object args)
    {
        this.EntryList.SelectContextItem(((MenuFlyout)sender).Target);
        UpdateHiveSwitchMenu(
            this.EntrySwitchHiveMenu, this.ViewModel.SelectedEntries.Count == 1 ? this.ViewModel.SelectedEntry?.Key : null);
        if (this.ViewModel.SelectedEntry is not { } entry)
        {
            return;
        }

        var single = this.ViewModel.SelectedEntries.Count == 1;
        var itemActions = single && !entry.IsParent;
        var valueActions = itemActions && !entry.IsKey;
        var keyActions = single && entry.IsKey;

        static Visibility Visible(bool value)
        {
            return value ? Visibility.Visible : Visibility.Collapsed;
        }

        this.EntryOpenMenu.Visibility = this.EntryNewTabMenu.Visibility = this.EntryCopyPathMenu.Visibility
            = this.EntryCopyPathAsMenu.Visibility = this.EntryWebSearchMenu.Visibility
                = this.EntryOpenSeparator.Visibility = Visible(single);
        this.EntryWebSearchMenu.IsEnabled = single && !this.EntryLocation(entry).IsComputer;
        if (single)
        {
            this.PopulateCopyPathMenu(this.EntryCopyPathAsMenu.Items, this.EntryLocation(entry));
        }

        this.EntryCopyMenu.Visibility = this.EntryRenameMenu.Visibility = this.EntryCopySeparator.Visibility
            = this.EntryToolsSeparator.Visibility = Visible(itemActions);
        this.EntryDeleteMenu.Visibility = Visible(!single || !entry.IsParent);
        this.EntryBinaryMenu.Visibility = this.EntryInspectMenu.Visibility = this.EntryWatchMenu.Visibility = Visible(valueActions);
        this.EntryRevealMenu.Visibility = Visible(valueActions && this.ViewModel.RevealFileCommand.CanExecute(null));
        this.EntryDuplicateMenu.Visibility = Visible(valueActions && entry.Value is not null);
        this.EntryOpenTargetMenu.Visibility = Visible(valueActions && entry.IsLink);
        this.EntryFavoriteMenu.Visibility = this.EntryExportMenu.Visibility = this.EntryPermissionsMenu.Visibility = Visible(keyActions);
        this.EntryFavoriteMenu.Text = entry.Key is { } key && this._window.Favorites.Contains(key)
            ? "Remove from favorites"
            : "Add to favorites…";
        this.EntryOpenMenu.Text = entry.IsParent ? "Open parent key" : entry.IsKey ? "Open" : "Modify…";
        this.EntryOpenMenu.Icon = new FontIcon
        {
            Glyph = SymbolGlyphs.Get(entry.IsParent ? Symbol.Up : entry.IsKey ? Symbol.OpenFile : Symbol.Edit)
        };
        this.EntryNewTabMenu.Text = entry.IsKey || entry.IsParent ? "Open in new tab" : "Open key in new tab";
        this.EntryNewTabMenu.IsEnabled = this.EntryCopyPathMenu.IsEnabled = single;
    }

    private void RegistryListMenu_Opening(object sender, object args)
    {
        this.ViewMenu_Opening(sender, args);
        this.PopulateCopyPathMenu(this.ListCopyPathAsMenu.Items, this.ViewModel.Location);
        UpdateHiveSwitchMenu(this.ListSwitchHiveMenu, this.ViewModel.Location);
    }

    private static void UpdateHiveSwitchMenu(MenuFlyoutItem item, RegistryLocation? source)
    {
        var target = source?.GetAlternateHiveLocation();
        item.CommandParameter = source;
        item.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        item.Text = target?.Hive == RegistryHive.CurrentUser ? "Go to HKCU" : "Go to HKLM";
    }

    private void ShowError(string message, string title = "Couldn't complete the operation")
    {
        this.ShowMessage(title, message, NoticeKind.Error);
    }

    private void ShowNotice(string title, string message, NoticeKind kind)
    {
        this.ShowMessage(title, message, kind);
    }

    private void ShowMessage(
        string title,
        string message,
        NoticeKind severity = NoticeKind.Information,
        IReadOnlyList<NotificationAction>? actions = null)
    {
        if (this._closed)
        {
            return;
        }

        this._keepRenameRejectionNotice = false;
        this._notificationContent = StatusNotifications.Create(title, message, severity, actions, this._notificationKey);
        this._notification = this._window.Notifications.Show(this._notificationContent);
    }

    private async Task GuardAsync(string failureTitle, string canceledTitle, Func<Task> action, [CallerMemberName] string operation = "")
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            this.ShowMessage(canceledTitle, "The operation was canceled.");
        }
        catch (Exception ex)
        {
            this._window.Logger.LogFailure(LogLevel.Warning, operation, ex);
            this.ShowError(ex.Message, failureTitle);
        }
        finally
        {
            this._window.JournalHistory.RegistryActivityCompleted();
        }
    }

    internal void CancelWork()
    {
        this._closed = true;
        this.ViewModel.InspectorRequested -= this.ShowValueInspectorAsync;
        this._notification?.Dismiss();
        this._exportNotification.Dispose();
        this._snapshotNotification.Dispose();
        this.ToolbarShortcuts.SetCommands(null);
        this.TreePane.Cancel();
    }

    private async Task<SavedSearch?> ShowFindDialogAsync(FindOptionsViewModel options)
    {
        SavedSearch? search = null;
        var form = new RegistryFindOptions(options);
        var accepted = await this._dialogs.ShowFormAsync(
            "Find in registry", form, "Find", () =>
            {
                search = options.CreateSearch();
                return Task.CompletedTask;
            }, initialFocus: form.Input);
        return accepted ? search : null;
    }

    private Task<bool> ConfirmAsync(string title, string message, string accept)
    {
        return this._dialogs.ConfirmAsync(title, message, accept);
    }

    private Task<bool> ShowEditPromptAsync(RegistryEditPrompt prompt)
    {
        return RegistryEditDialog.ShowAsync(this._window.Dialogs, prompt);
    }

    private void CopyFocusedKeyPath()
    {
        var focused = FocusManager.GetFocusedElement(this.XamlRoot) as DependencyObject;
        if (IsInside(focused, this.TreePane) && this.TreePane.SelectedKey is { } location)
        {
            this.CopyText(location.Address);
        }
        else if (IsInside(focused, this.EntryList))
        {
            this.CopyPath_Click(this, new RoutedEventArgs());
        }
        else
        {
            this.CopyCurrentPath_Click(this, new RoutedEventArgs());
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs args)
    {
        this.CopyText(
            (this.ViewModel.SelectedEntry?.ParentLocation ?? this.ViewModel.SelectedEntry?.Key ?? this.ViewModel.Location).Address);
    }

    private void CopyCurrentPath_Click(object sender, RoutedEventArgs args)
    {
        this.CopyText(this.ViewModel.Location.Address);
    }

    private void EntryWebSearch_Click(object sender, RoutedEventArgs args)
    {
        if (this.ViewModel.SelectedEntries.Count == 1 && this.ViewModel.SelectedEntry is { } entry)
        {
            this.ViewModel.SearchWebCommand.Execute((this.EntryLocation(entry), entry.IsDefaultValue ? string.Empty : entry.Value?.Name));
        }
    }

    private void TreeWebSearch_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.SearchWebCommand.Execute((this._treeContextLocation, null));
    }

    private void CurrentWebSearch_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.SearchWebCommand.Execute((this.ViewModel.Location, null));
    }

    private void CurrentCopyPathMenu_Opening(object sender, object args)
    {
        this.PopulateCopyPathMenu(((MenuFlyout)sender).Items, this.ViewModel.Location);
    }

    private void PopulateCopyPathMenu(IList<MenuFlyoutItemBase> items, RegistryLocation location)
    {
        items.Clear();
        foreach (var (label, format) in new[]
                 {
                     ("PowerShell drive path", RegistryPathFormat.PowerShellDrive),
                     ("PowerShell provider path", RegistryPathFormat.PowerShellProvider),
                     ("Registry file header", RegistryPathFormat.RegFileHeader)
                 })
        {
            var path = location.FormatPath(format);
            var item = new MenuFlyoutItem { Text = label, IsEnabled = path is not null, Icon = new FontIcon { Glyph = "\uE62F" } };
            item.Click += (_, _) =>
            {
                if (path is not null)
                {
                    this.CopyText(path);
                }
            };
            items.Add(item);
        }
    }

    private void CopyText(string text)
    {
        try
        {
            ClipboardText.Set(text);
        }
        catch (Exception ex)
        {
            this.ShowError(ex.Message, "Couldn't copy to the clipboard");
        }
    }

    private RegistryLocation EntryLocation(RegistryEntry entry)
    {
        return entry.ParentLocation ?? entry.Key ?? this.ViewModel.Location;
    }

    private void EntryNewTab_Click(object sender, RoutedEventArgs args)
    {
        if (this.ViewModel.SelectedEntry is { } entry)
        {
            this.OpenTabAfterMenuCloses((MenuFlyout)this.Resources["RegistryEntryMenu"], this.EntryLocation(entry), entry.Value?.Name);
        }
    }

    private void TreeNewTab_Click(object sender, RoutedEventArgs args)
    {
        if (this._treeContextLocation is { } location)
        {
            this.OpenTabAfterMenuCloses((MenuFlyout)this.Resources["RegistryTreeMenu"], location);
        }
    }

    private void OpenTabAfterMenuCloses(MenuFlyout menu, RegistryLocation location, string? value = null)
    {
        // Removing a page with a focused flyout item makes WinUI traverse the outgoing tree to restore focus.
        if (menu.IsOpen)
        {
            menu.Closed += Closed;
            menu.Hide();
        }
        else
        {
            QueueOpen();
        }

        void Closed(object? sender, object args)
        {
            menu.Closed -= Closed;
            QueueOpen();
        }

        void QueueOpen()
        {
            this.DispatcherQueue.TryEnqueue(() => this._window.ViewModel.OpenBrowser(location, value));
        }
    }

    private void TreeCopyPath_Click(object sender, RoutedEventArgs args)
    {
        if (this._treeContextLocation is { } location)
        {
            this.CopyText(location.Address);
        }
    }

    private Task AddFavoriteAsync(RegistryLocation location)
    {
        return FavoritesViewModel.AddFavoriteAsync(this._window.Favorites, new FavoriteDialogs(this._window.Dialogs), location, null, true);
    }

    private async void EntryFavorite_Click(object sender, RoutedEventArgs args)
    {
        await this.GuardAsync(
            "Couldn't update favorites", "Favorite update canceled",
            () => this.ViewModel.SelectedEntry?.Key is { } location
                ? this.ViewModel.ToggleFavoriteCommand.ExecuteAsync(location)
                : Task.CompletedTask);
    }

    private async void TreeFavorite_Click(object sender, RoutedEventArgs args)
    {
        await this.GuardAsync(
            "Couldn't update favorites", "Favorite update canceled",
            () => this._treeContextLocation is { } location
                ? this.ViewModel.ToggleFavoriteCommand.ExecuteAsync(location)
                : Task.CompletedTask);
    }
}
