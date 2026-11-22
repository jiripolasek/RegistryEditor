// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Pages;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<WindowTab, FrameworkElement> _views = [];
    private readonly OperationNotification _exportNotification;

    private string? _startupError;
    private NotificationHandle? _preferenceNotice;

    public AppSettings Settings { get; }

    public RegistryService Registry { get; }

    public RegistryJournal Journal => this.JournalHistory.Journal;

    internal JournalViewModel JournalHistory { get; }

    internal FavoritesStore Favorites { get; }

    internal SettingsViewModel Preferences { get; }

    internal ColumnLayoutPersistence ColumnLayouts { get; }

    internal Func<WatchSurfaceViewModel> CreateWatchSurface { get; }

    public bool IsAdministrator => this.Journal.AdministratorOnly;

    internal ILogger<MainWindow> Logger { get; }

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public FrameworkElement Root => this.Shell;

    internal WindowDialogPresenter Dialogs { get; }

    internal MainWindowViewModel ViewModel { get; }

    internal ApplicationRegistryViewModel RegistryMenu { get; }

    internal NotificationSession Notifications { get; } = new(TimeSpan.FromSeconds(new UISettings().MessageDuration));

    internal bool IsClosed { get; private set; }

    internal MainWindow(
        ILogger<MainWindow> logger,
        AppSettings settings,
        RegistryService registry,
        JournalViewModel journal,
        FavoritesStore favorites,
        SettingsViewModel preferences,
        MainWindowViewModel viewModel,
        Func<WatchSurfaceViewModel> createWatchSurface,
        RegistryLocation? initialLocation,
        string? startupError)
    {
        this.Logger = logger;
        this.Settings = settings;
        this.Registry = registry;
        this.JournalHistory = journal;
        this.Favorites = favorites;
        this.Preferences = preferences;
        this.ViewModel = viewModel;
        this.CreateWatchSurface = createWatchSurface;
        viewModel.DispatchWatchUpdate = this.DispatchWatchUpdate;
        viewModel.WatchProposed += this.ShowWatchProposal;
        viewModel.WatchOpened += session =>
        {
            if ((this.BottomContent?.Content ?? this.ActiveTabContent.Content) is ToolView view)
            {
                view.SelectWatch(session);
                view.RequestFocus(true);
            }
        };
        viewModel.WatchFailed += session =>
        {
            this.ShowApplicationNotice(
                "Watch stopped unexpectedly", session.Target + "\n" + session.Error!.Message, NoticeKind.Error, (NotificationAction[])
                [
                    new NotificationAction(
                        "Open watch", _ =>
                        {
                            viewModel.RevealWatchCommand.Execute(session);
                            return Task.CompletedTask;
                        })
                ]);
        };
        var dialogs = new RegistryDialogs(this);
        journal.ConfirmRevert = entry => dialogs.ConfirmAsync(
            "Revert this edit?",
            entry.Summary
            + "\n"
            + entry.Path
            + "\n\nThe current values must still match the recorded after-state. Registry writes are not atomic. Recreated keys restore owner, group, and access rules; auditing and key metadata are not restored.",
            "Check and revert");
        journal.RefreshBrowser = () => viewModel.ContextBrowser?.RefreshCommand.ExecuteAsync(null) ?? Task.CompletedTask;
        journal.Notice += (title, message, kind) => this.ShowApplicationNotice(title, message, kind);
        var branches = new RegistryBranchCommands(registry) { Dialogs = dialogs };
        branches.Notice += this.ShowBranchNotice;
        this._exportNotification = new OperationNotification(this.Notifications, "application-branch:export", this.CancelApplicationExport);
        branches.OperationStarted += this._exportNotification.Start;
        branches.ExportNotice += this._exportNotification.ShowResult;
        branches.ExportCompleted += this._exportNotification.AddOpenFolderAction;
        branches.Failed += ex => logger.LogFailure(LogLevel.Warning, "Application registry export", ex);
        this.RegistryMenu = new ApplicationRegistryViewModel(
            viewModel, registry, journal, dialogs, branches, logger, journal.Journal.AdministratorOnly);
        this.RegistryMenu.Notice += (title, message, kind) => this.ShowApplicationNotice(title, message, kind);
        this.RegistryMenu.OpenFilePreviewAsync = this.OpenRegistryFileAsync;
        this.RegistryMenu.ExportAllCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(this.RegistryMenu.ExportAllCommand.IsRunning) && !this.RegistryMenu.ExportAllCommand.IsRunning)
            {
                this._exportNotification.Complete();
            }
        };
        this._startupError = startupError;
        if (this.Settings.LoadError is not null)
        {
            this.Logger.LogWarning("Could not load saved settings");
        }

        this.InitializeComponent();
        this.NotificationOverlay.Attach(this.Notifications);
        this.NotificationOverlay.ShadowReceiver = this.Shell;
        this.NotificationOverlay.ActionFailed += error => this.Logger.LogFailure(LogLevel.Warning, "Notification action", error);
        this.Activated += (_, args) => this.NotificationOverlay.IsWindowActive
            = args.WindowActivationState != WindowActivationState.Deactivated;
        this.ColumnLayouts = new ColumnLayoutPersistence(this.DispatcherQueue, preferences.ColumnLayout, preferences.SaveColumnLayout);
        this.ApplyListDensity();
        this.SuppressMenuChime();
        // VK_OEM_COMMA has no named VirtualKey member.
        var settingsShortcut = new KeyboardAccelerator { Key = (VirtualKey)0xBC, Modifiers = VirtualKeyModifiers.Control };
        settingsShortcut.Invoked += this.Settings_Invoked;
        this.Shell.KeyboardAccelerators.Add(settingsShortcut);
        this.Shell.PreviewKeyDown += this.Shortcuts_KeyDown;
        this.Dialogs = new WindowDialogPresenter(
            this.Shell, this.ViewModel,
            () => this.ShowApplicationNotice(
                viewModel.IsDialogOpen ? "Finish the open dialog first" : "The window is closing",
                viewModel.IsDialogOpen
                    ? "Close the open dialog, then try again."
                    : "A new dialog can't be opened while the window is closing.", NoticeKind.Warning));
        this.Shell.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(this.Shell_PointerReleased), true);
        this.Shell.AddHandler(UIElement.DragOverEvent, new DragEventHandler(this.RegistryFile_DragOver), true);
        this.Shell.AddHandler(UIElement.DropEvent, new DragEventHandler(this.RegistryFile_Drop), true);
        this.ViewModel.Initialize(initialLocation);
        this.InitializeTools();
        this.AdministratorShield.Visibility = this.IsAdministrator ? Visibility.Visible : Visibility.Collapsed;
        this.ExtendsContentIntoTitleBar = true;
        var titleBarInput = InputNonClientPointerSource.GetForWindowId(this.AppWindow.Id);
        this.WindowIconArea.LayoutUpdated += this.SetWindowIconRegion;
        this.Activated += this.SetWindowIconRegion;
        titleBarInput.WindowRectChanged += this.SetWindowIconRegion;
        var placement = new WindowPlacementController(this.AppWindow, this.Handle, viewModel.Placement);
        viewModel.CapturePlacement = placement.Capture;
        this.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        this.ApplyTheme(this.Settings.Theme);
        this.ApplyBackdrop();
        preferences.PreferenceSaved += property =>
        {
#if REGEDIT_EXPERIMENTAL_TERMINAL
            if (property == nameof(SettingsViewModel.OfferWindowsPowerShellFallback))
            {
                foreach (var terminal in this._views.Values.OfType<PowerShellView>())
                {
                    terminal.RefreshRecoveryActions();
                }
            }
#endif
            if (property == nameof(SettingsViewModel.ThemeIndex))
            {
                this.ApplyTheme(this.Settings.Theme);
            }
            else if (property == nameof(SettingsViewModel.BackdropIndex))
            {
                this.ApplyBackdrop();
            }
            else if (property is nameof(SettingsViewModel.EditorFontFamily) or nameof(SettingsViewModel.EditorFontSize))
            {
                foreach (var preview in this._registryFileWindows)
                {
                    preview.ApplyEditorFont(this.Settings.EditorFont);
                }
            }
            else if (property is not (nameof(SettingsViewModel.ColumnLayout) or nameof(SettingsViewModel.EditorLayoutIndex)))
            {
                this.ApplyViewSettings();
            }
        };
        preferences.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.DataFont))
            {
                this.ApplyViewSettings();
            }
        };
        preferences.SaveFailed += ex =>
        {
            logger.LogFailure(LogLevel.Warning, "Save preference", ex);
            if (this.ActiveTabContent.Content is SettingsPage page)
            {
                page.RevealSaveError();
            }
            else
            {
                this.ShowApplicationNotice("Settings could not be saved", ex.Message, NoticeKind.Error);
            }
        };
        this.ViewModel.PropertyChanged += this.ShellStateChanged;
        this.ViewModel.SettingsSectionRequested += shortcuts => ((SettingsPage)this.ActiveTabContent.Content).ShowShortcuts(shortcuts);
        this.ViewModel.RegistryFileEditorSettingsRequested += () => ((SettingsPage)this.ActiveTabContent.Content).ShowRegistryFileEditor();
        this.ViewModel.TabClosed += this.ReleaseView;
        this.ViewModel.SearchFocusRequested += () => (this.SelectedTabContent as BrowserPage)?.FocusSearch();
        this.ViewModel.CloseRequested += this.RequestClose;
#if REGEDIT_EXPERIMENTAL_TERMINAL
        this.InitializeTerminalMenu();
#endif
        this.ApplyPanelTabPosition();
        this.ShellStateChanged(this.ViewModel, new PropertyChangedEventArgs(nameof(MainWindowViewModel.ActiveTab)));
        this.ShowBottomContent();
        this.Closed += (_, _) =>
        {
            this.IsClosed = true;
            foreach (var browser in this._views.Values.OfType<BrowserPage>())
            {
                browser.CancelWork();
            }

            this.ColumnLayouts.Flush();
            if (this.Shell.XamlRoot is { } root)
            {
                root.Changed -= this.TitleBarRoot_Changed;
            }

            this.WindowIconArea.LayoutUpdated -= this.SetWindowIconRegion;
            this.Activated -= this.SetWindowIconRegion;
            titleBarInput.WindowRectChanged -= this.SetWindowIconRegion;
            this.ViewModel.CapturePlacement = null;
            this.JournalHistory.RevertCommand.Cancel();
            this.ViewModel.WindowClosed();
            this.RegistryMenu.ExportAllCommand.Cancel();
            this._exportNotification.Dispose();
            this.NotificationOverlay.Dispose();
            this.Notifications.Dispose();
        };
    }

    internal void CancelApplicationExport()
    {
        this.RegistryMenu.ExportAllCommand.Cancel();
    }

    private void ApplicationEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.RegistryMenu.ExportAllCommand.IsRunning)
        {
            this.CancelApplicationExport();
            args.Handled = true;
        }
    }

    private void ShowWatchProposal(WatchDefinition proposal)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Content = new WatchProposalView(proposal, this.ViewModel.StartProposedWatchCommand, flyout.Hide);
        flyout.ShowAt(this.ApplicationMenuButton ?? (FrameworkElement)this.Shell);
    }

    public Visibility RunningWatchVisibility(int count)
    {
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowApplicationNotice(string title, string message, NoticeKind kind, IReadOnlyList<NotificationAction>? actions = null)
    {
        if (!this.IsClosed)
        {
            StatusNotifications.Show(this.Notifications, title, message, kind, actions);
        }
    }

    private void ShowBranchNotice(string title, string message, NoticeKind kind)
    {
        if (this.IsClosed)
        {
            return;
        }

        StatusNotifications.Show(this.Notifications, title, message, kind, key: "application-branch");
    }

    private void NotificationHistory_Click(object sender, RoutedEventArgs args)
    {
        this.DispatcherQueue.TryEnqueue(() =>
            this.NotificationOverlay.ShowHistory(this.ApplicationMenuButton ?? (FrameworkElement)this.Shell));
    }

    private async void Shell_Loaded(object sender, RoutedEventArgs args)
    {
        this.EnableWindowIconGestures();
        this.Shell.XamlRoot.Changed += this.TitleBarRoot_Changed;
        this.UpdateCaptionInsets();
        _ = this.Preferences.LoadDefaultDataFontAsync(SystemFonts.GetDefaultMonospaceFontFamily);
        if (this._startupError is not { } error)
        {
            return;
        }

        this._startupError = null;
        await this.Dialogs.ShowAsync(new AppDialog("Startup notice", error, close: "OK", defaultButton: ContentDialogButton.Close));
    }

    private void TitleBarRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        this.UpdateCaptionInsets();
    }

    private void TitleBarDragRegion_Loaded(object sender, RoutedEventArgs args)
    {
        // The footer joins the XAML tree when the TabView template loads.
        this.SetTitleBar(this.TitleBarDragRegion);
        this.UpdateCaptionInsets();
    }

    private void UpdateCaptionInsets()
    {
        if (this.Shell.XamlRoot is not { } root)
        {
            return;
        }

        var scale = root.RasterizationScale;
        this.CaptionInset.Width = new GridLength(CaptionLayout.InsetWidth(this.AppWindow.TitleBar.RightInset, scale));
        this.TabStripHeader.Margin = new Thickness(CaptionLayout.InsetWidth(this.AppWindow.TitleBar.LeftInset, scale) + 8, 0, 4, 0);
    }

    private void SetWindowIconRegion(object? sender, object args)
    {
        if (this.WindowIconArea.XamlRoot is not { } root || this.WindowIconArea.ActualWidth == 0)
        {
            return;
        }

        var origin = this.WindowIconArea.TransformToVisual(null).TransformPoint(default);
        var scale = root.RasterizationScale;
        var left = (int)Math.Floor(origin.X * scale);
        var top = (int)Math.Floor(origin.Y * scale);
        var right = (int)Math.Ceiling((origin.X + this.WindowIconArea.ActualWidth) * scale);
        var bottom = (int)Math.Ceiling((origin.Y + this.WindowIconArea.ActualHeight) * scale);
        var bounds = new RectInt32(left, top, right - left, bottom - top);
        var input = InputNonClientPointerSource.GetForWindowId(this.AppWindow.Id);
        // WinUI can clear the icon region when the title bar is activated or rearranged.
        if (input.GetRegionRects(NonClientRegionKind.Icon) is not [var current] || !current.Equals(bounds))
        {
            input.SetRegionRects(NonClientRegionKind.Icon, [bounds]);
        }
    }

    private void ApplyBackdrop()
    {
        this.SystemBackdrop = this.Settings.Backdrop == "Acrylic"
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop { Kind = MicaKind.BaseAlt };
        foreach (var preview in this._registryFileWindows)
        {
            preview.ApplyBackdrop(this.Settings.Backdrop);
        }
    }

    public void ApplyTheme(string theme)
    {
        this.Shell.RequestedTheme = Enum.TryParse<ElementTheme>(theme, out var selected) ? selected : ElementTheme.Default;
        this.AppWindow.TitleBar.PreferredTheme = this.Shell.RequestedTheme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode
        };
        foreach (var preview in this._registryFileWindows)
        {
            preview.ApplyTheme(this.Shell.RequestedTheme);
        }
    }

    public void ApplyViewSettings()
    {
        this.ApplyListDensity();
        this.ApplyPanelTabPosition();
        foreach (var page in this._views.Values.OfType<BrowserPage>())
        {
            page.ApplyViewSettings();
        }
    }

    private void ApplyPanelTabPosition()
    {
        var atBottom = this.Settings.PanelTabsAtBottom;
        this.BottomUpperRow.Height = atBottom ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        this.BottomLowerRow.Height = atBottom ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(this.BottomContentSurface, atBottom ? 0 : 1);
        Grid.SetRow(this.BottomTabs, atBottom ? 1 : 0);
    }

    private void ApplyListDensity()
    {
        Application.Current.Resources["ListRowPadding"] = new Thickness(0, this.Settings.ListRowPadding, 0, this.Settings.ListRowPadding);
        Application.Current.Resources["ListRowMinHeight"] = 22d + 2 * this.Settings.ListRowPadding;
        foreach (var view in this._views.Values.Concat(this._toolPanels.Values.OfType<FrameworkElement>()))
        {
            switch (view)
            {
                case InvestigationPage page: page.ApplyListSpacing(this.Settings.ListRowPadding); break;
                case ToolView tool: tool.ApplyListSpacing(this.Settings.ListRowPadding); break;
                case FavoritesView favorites: favorites.ApplyListSpacing(this.Settings.ListRowPadding); break;
            }
        }
    }

    private FrameworkElement GetView(WindowTab tab)
    {
        if (this._views.TryGetValue(tab, out var view))
        {
            return view;
        }

        view = tab switch
        {
            BrowserTab browser => new BrowserPage(this, browser.Browser, browser.InitialValue),
            SettingsTab => new SettingsPage(this.Preferences),
            ToolTab tool => (FrameworkElement)this.CreateTool(tool.Tool),
#if REGEDIT_EXPERIMENTAL_TERMINAL
            PowerShellTab shell => this.CreatePowerShellView(shell),
#endif
            InvestigationViewModel investigation => new InvestigationPage(
                investigation, this.Dialogs, this.Notifications, this.Settings.ListRowPadding, this.ColumnLayouts),
            _ => throw new InvalidOperationException("Unknown tab type.")
        };
        if (view is InvestigationPage page)
        {
            if (tab is SearchResultsViewModel)
            {
                page.SetDockAction(() => this.ViewModel.DockTab(tab));
            }
            else
            {
                page.SetToolsAction(button => this.ToggleTools(button));
            }

            page.MenuButton.Click += this.ApplicationMenu_Click;
        }

        if (view is SettingsPage settingsPage)
        {
            settingsPage.MenuButton.Click += this.ApplicationMenu_Click;
        }

        this._views.Add(tab, view);
        return view;
    }

    private void ReleaseView(WindowTab tab)
    {
        if (!this._views.Remove(tab, out var view))
        {
            return;
        }

        switch (view)
        {
            case BrowserPage browser:
                browser.CancelWork();
                break;
            case InvestigationPage investigation:
                investigation.DismissNotification();
                break;
#if REGEDIT_EXPERIMENTAL_TERMINAL
            case PowerShellView terminal:
                terminal.Dispose();
                break;
#endif
        }
    }

    private void ShellStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainWindowViewModel.IsPaneOpen) or nameof(MainWindowViewModel.IsBottomPaneOpen))
        {
            this.UpdateToolbarCommands();
            foreach (var page in this._views.Values.OfType<BrowserPage>())
            {
                page.UpdateToolbar();
            }
        }

        if (args.PropertyName == nameof(this.ViewModel.ActiveTab) && this.ViewModel.ActiveTab is { } tab)
        {
            this.Tabs.SelectedItem = tab;
            this.ActiveTabContent.Content = this.GetView(tab);
            (this.SelectedTabContent as BrowserPage)?.RefreshFavoritesBar();
            this.ConfigureDockedView(this.ActiveTabContent.Content, false);
            this.UpdateToolContext();
        }
        else if (args.PropertyName == nameof(this.ViewModel.ContextLocation))
        {
            this.UpdateToolContext();
        }
        else if (args.PropertyName == nameof(this.ViewModel.IsPaneOpen) && this.ViewModel.IsPaneOpen)
        {
            this.ShowTools(this.ViewModel.SelectedTool);
        }
        else if (args.PropertyName is nameof(MainWindowViewModel.ActiveBottomTab) or nameof(MainWindowViewModel.IsBottomPaneOpen))
        {
            this.ShowBottomContent();
        }
#if REGEDIT_EXPERIMENTAL_TERMINAL
        else if (args.PropertyName == nameof(MainWindowViewModel.IsDialogOpen))
        {
            this.UpdateTerminalVisibility();
        }
#endif
    }

    private void UpdateTitle()
    {
        var prefix = this.IsAdministrator && this.ViewModel.VisibleBrowser is not null ? "Administrator: " : string.Empty;
        this.Title = $"{prefix}{this.ViewModel.ActiveTab?.Title} - Registry Editor";
    }

    private void Shell_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (this.ViewModel.IsClosing || this.SelectedTabContent is not BrowserPage browser)
        {
            return;
        }

        var kind = args.GetCurrentPoint(this.Shell).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released))
        {
            return;
        }

        if (browser.TryNavigateHistory(kind == PointerUpdateKind.XButton2Released))
        {
            args.Handled = true;
        }
    }

    private void AddTab_Click(TabView sender, object args)
    {
        this.ViewModel.NewTabCommand.Execute(null);
    }

    private void Tab_CloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is WindowTab tab)
        {
            this.ViewModel.CloseTab(tab);
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (this.Tabs.SelectedItem is WindowTab tab)
        {
            this.ViewModel.ActiveTab = tab;
        }
    }

    private void Settings_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ViewModel.OpenSettingsCommand.Execute(null);
        args.Handled = true;
    }

    private void Shortcuts_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        static bool Down(VirtualKey key)
        {
            return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        }

        if (args.Handled || this.Dialogs.IsOpen || !Down(VirtualKey.Control) ||
            Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
        {
            return;
        }

        bool Matches(short key) =>
            key != -1 && args.Key == (VirtualKey)(key & 0xFF) &&
            Down(VirtualKey.Shift) == ((key & 0x100) != 0) && Down(VirtualKey.Menu) == ((key & 0x400) != 0);

        // Resolve punctuation against the current keyboard layout.
        var slash = args.Key == VirtualKey.Divide ? (short)VirtualKey.Divide : VkKeyScan('/');
        if (Matches(slash))
        {
            this.ViewModel.OpenShortcutsCommand.Execute(null);
            args.Handled = true;
            return;
        }

#if REGEDIT_EXPERIMENTAL_TERMINAL
        var semicolon = VkKeyScan(';');
        // AltGr can synthesize left Ctrl. Require right Ctrl when typing a semicolon needs AltGr.
        if (Matches(semicolon) && ((semicolon & 0x600) != 0x600 || Down(VirtualKey.RightControl)))
        {
            if (!args.KeyStatus.WasKeyDown)
            {
                this.OpenPowerShell();
            }

            args.Handled = true;
        }
#endif
    }

    private void NewTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ViewModel.NewTabCommand.Execute(null);
        args.Handled = true;
    }

    private void FindNextResult_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (this.ViewModel.FindNextResultCommand.CanExecute(null))
        {
            this.ViewModel.FindNextResultCommand.Execute(null);
        }
    }

    private void CloseTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.IsBottomFocused() && this.ViewModel.ActiveBottomTab is { } tab)
        {
            this.ViewModel.CloseBottomTab(tab);
        }
        else
        {
            this.ViewModel.CloseCurrentTabCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void NextTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!this.CycleBottomTab(1))
        {
            this.ViewModel.NextTabCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void PreviousTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!this.CycleBottomTab(-1))
        {
            this.ViewModel.PreviousTabCommand.Execute(null);
        }

        args.Handled = true;
    }

    private bool IsBottomFocused()
    {
        var focused = FocusManager.GetFocusedElement(this.Shell.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, this.BottomPane))
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private bool CycleBottomTab(int offset)
    {
        if (!this.IsBottomFocused() || this.ViewModel.BottomTabs.Count == 0)
        {
            return false;
        }

        var tabs = this.ViewModel.BottomTabs;
        this.ViewModel.ActiveBottomTab = tabs[(tabs.IndexOf(this.ViewModel.ActiveBottomTab!) + offset + tabs.Count) % tabs.Count];
        return true;
    }
}
