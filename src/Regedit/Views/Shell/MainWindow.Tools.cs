// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.Views.Pages;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    public event EventHandler? ContextChanged;

    private readonly Dictionary<ToolKind, IToolSurface> _toolPanels = [];

    private Control? _toolsTrigger;
    private bool _toolsReady, _returnToolFocus;

    private object? SelectedTabContent => this.ActiveTabContent.Content;

    private Button? ApplicationMenuButton => this.SelectedTabContent switch
    {
        BrowserPage page => page.MenuButton,
        InvestigationPage page => page.MenuButton,
        RegistryUsagePage page => page.MenuButton,
        IToolSurface page => page.MenuButton,
        SettingsPage page => page.MenuButton,
        _ => null
    };

    internal void ApplicationMenu_Click(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        this.FillToolsMenu(this.ApplicationMenu, this.ApplicationTools.Items);
        this.ApplicationMenu.ShowAt(button);
    }

    internal void RunAfterMenuCloses(MenuFlyout menu, Action action)
    {
        // Removing a page with a focused flyout item makes WinUI traverse the outgoing tree to restore focus.
        if (menu.IsOpen)
        {
            menu.Closed += Closed;
            menu.Hide();
        }
        else
        {
            this.DispatcherQueue.TryEnqueue(() => action());
        }

        void Closed(object? sender, object args)
        {
            menu.Closed -= Closed;
            this.DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private void HelpAndAbout_Click(object sender, RoutedEventArgs args)
    {
        this.ShowHelpAndAbout();
    }

    private void HelpAndAbout_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ShowHelpAndAbout();
        args.Handled = true;
    }

    private void ShowHelpAndAbout()
    {
        this.ViewModel.OpenSettingsCommand.Execute(null);
        ((SettingsPage)this.ActiveTabContent.Content).ShowAbout();
    }

    private void FocusApplicationMenu_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.ApplicationMenuButton is not { } button)
        {
            return;
        }

        button.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void InitializeTools()
    {
        this.InitializeToolbarCommands();
        this.Favorites.Changed += this.Favorites_Changed;
        this.Closed += (_, _) => this.Favorites.Changed -= this.Favorites_Changed;
        if (this.ViewModel.SelectedTool is not (ToolKind.Favorites or ToolKind.Notes))
        {
            this.ViewModel.SelectedTool = ToolKind.Favorites;
        }

        this.UpdatePinButton();
        this.ToolSelector.ItemsSource = ToolPresentation.Tools.Where(tool => tool.Kind is ToolKind.Favorites or ToolKind.Notes).ToArray();
        this.ToolSelector.SelectedItem = this.ViewModel.SelectedTool.Choice();
        this._toolsReady = true;
        if (this.ViewModel.IsPaneOpen)
        {
            this.ShowTools(this.ViewModel.SelectedTool);
        }

        this.AppWindow.Closing += this.Window_Closing;
    }

    private IToolSurface CreateTool(ToolKind tool, bool compact = false)
    {
        if (tool != ToolKind.Favorites)
        {
            return new ToolView(this, tool, compact);
        }

        var view = new FavoritesView(
            this.Favorites, this.Dialogs, compact, this.ViewModel.NavigateFromToolsAsync, this.ViewModel.OpenInBackground,
            this.ShowToolInPanel, this.Logger, this.Notifications, this.ColumnLayouts);
        view.MenuButton.Click += this.ApplicationMenu_Click;
        if (compact)
        {
            this.AddFavoritesMenuActions(view.FolderActionsMenu, view);
        }

        view.ApplyListSpacing(this.Settings.ListRowPadding);
        return view;
    }

    private void UpdateToolContext()
    {
        if (this._toolPanels.GetValueOrDefault(ToolKind.Favorites) is FavoritesView favorites)
        {
            favorites.SetCurrentLocation(this.ViewModel.VisibleBrowser?.Location);
        }

#if REGEDIT_EXPERIMENTAL_TERMINAL
        (this.BottomContent.Content as PowerShellView)?.SetBrowserLocation(this.ViewModel.VisibleBrowser?.Location);
#endif
        this.UpdateTitle();
        this.ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleTools(Control? trigger = null)
    {
        if (this.ToolsSplit.IsPaneOpen)
        {
            this.CloseTools(true);
            return;
        }

        this._toolsTrigger = trigger ?? FocusManager.GetFocusedElement(this.Shell.XamlRoot) as Control;
        this.ShowTools(this.ViewModel.SelectedTool);
        ((IToolSurface)this.ToolContent.Content).RequestFocus();
    }

    public void ShowTools(ToolKind tool, bool focus = false)
    {
        if (tool is ToolKind.Journal or ToolKind.Watches)
        {
            this.ViewModel.IsPaneOpen = false;
            this.ViewModel.OpenBottomTool(tool);
            return;
        }

        if (!this._toolPanels.TryGetValue(tool, out var view))
        {
            this._toolPanels[tool] = view = this.CreateTool(tool, true);
        }

        if (view is FavoritesView favorites)
        {
            favorites.SetCurrentLocation(this.ViewModel.VisibleBrowser?.Location);
        }

        this.ToolContent.Content = view;
        this.SidebarActions.SetCommands(view.PanelCommands);
        this.ToolSelector.SelectedItem = tool.Choice();
        this.ViewModel.SelectedTool = tool;
        this._returnToolFocus = false;
        this.ViewModel.IsPaneOpen = true;
        this.UpdateToolLayout();
        if (focus)
        {
            view.RequestFocus();
        }
    }

    internal void ShowNotesForKey(RegistryLocation location)
    {
        if (location.IsComputer)
        {
            return;
        }

        this.ShowTools(ToolKind.Notes);
        var view = (ToolView)this.ToolContent.Content;
        view.Notes!.CurrentLocation = this.ViewModel.ContextLocation;
        view.Notes.Selected = this.ViewModel.GetNote(location);
        view.RequestFocus();
    }

    private void ToolSelector_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!this._toolsReady || this.ToolSelector.SelectedItem is not ToolChoice { Kind: var tool })
        {
            return;
        }

        this.ShowTools(tool);
        this.SaveToolPreferences();
    }

    internal void OpenToolTab(ToolKind tool, IToolSurface? source = null)
    {
        this.ViewModel.OpenToolTab(tool, source is not null);
        var target = (IToolSurface)this.GetView(this.ViewModel.ActiveTab!);

        if (source is not null)
        {
            target.CopyContextFrom(source);
        }

        target.RequestFocus(true);
    }

    internal void ShowToolInPanel(IToolSurface source)
    {
        if (source.Tool is ToolKind.Journal or ToolKind.Watches
            && this._views.FirstOrDefault(pair => ReferenceEquals(pair.Value, source)).Key is { } tab)
        {
            this.ViewModel.DockTab(tab);
            return;
        }

        this.ShowTools(source.Tool);
        var panel = (IToolSurface)this.ToolContent.Content;
        panel.CopyContextFrom(source);
        this.ViewModel.ShowToolInPanel(source.Tool, (source as ToolView)?.NoteLocation);

        panel.RequestFocus(true);
        this._toolsTrigger = null;
    }

    public WatchSession StartWatch(RegistryLocation location, string? valueName = null)
    {
        return this.ViewModel.StartWatch(location, valueName, this.DispatchWatchUpdate);
    }

    private Task DispatchWatchUpdate(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!this.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }))
        {
            done.TrySetCanceled();
        }

        return done.Task;
    }

    private void OpenToolTab_Click(object sender, RoutedEventArgs args)
    {
        this.OpenToolTab(this.ViewModel.SelectedTool, this.ToolContent.Content as IToolSurface);
    }

    private void Tools_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ToggleTools();
        args.Handled = true;
    }

    private void CloseTools(bool returnFocus)
    {
        this._returnToolFocus = returnFocus;
        this.ViewModel.IsPaneOpen = false;
    }

    private void CloseTools_Click(object sender, RoutedEventArgs args)
    {
        this.CloseTools(true);
    }

    private void ToolSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.ToolContent.Content is FavoritesView favorites)
        {
            favorites.ShowSearch(true);
            args.Handled = true;
        }
    }

    private void ToolPane_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape)
        {
            return;
        }

        args.Handled = true;
        this.CloseTools(true);
    }

    private void ToolsPane_Closing(SplitView sender, SplitViewPaneClosingEventArgs args)
    {
        args.Cancel = this.ViewModel.IsPanePinned
                      && this.ViewModel.IsPaneOpen
                      && !this.ViewModel.IsClosing
                      && !this.ViewModel.CanCloseWindow;
    }

    private void ToolsPane_Closed(SplitView sender, object args)
    {
        if (this.ToolsSplit.IsPaneOpen)
        {
            return;
        }

        this.ToolContent.Content = null;
        this.SidebarActions.SetCommands(null);
        if (this._returnToolFocus)
        {
            if (this._toolsTrigger is not { IsLoaded: true } || !this._toolsTrigger.Focus(FocusState.Keyboard))
            {
                if (this.SelectedTabContent is BrowserPage browser)
                {
                    browser.FocusToolsButton();
                }
                else if (this.SelectedTabContent is IToolSurface view)
                {
                    view.RequestFocus();
                }
                else
                {
                    this.ApplicationMenuButton?.Focus(FocusState.Keyboard);
                }
            }
        }

        this._returnToolFocus = false;
    }

    private void PinTools_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.IsPanePinned = !this.ViewModel.IsPanePinned;
        this.UpdatePinButton();
        this.UpdateToolLayout();
        this.SaveToolPreferences();
    }

    private void UpdatePinButton()
    {
        var label = this.ViewModel.IsPanePinned ? "Unpin tool panel" : "Pin tool panel";
        this.PinToolsIcon.Glyph = this.ViewModel.IsPanePinned ? "\uE77A" : "\uE718";
        AutomationProperties.SetName(this.PinTools, label);
        ToolTipService.SetToolTip(this.PinTools, label);
    }

    private void Shell_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateToolLayout();
    }

    private void UpdateToolLayout()
    {
        if (!this._toolsReady)
        {
            return;
        }

        this.ToolsSplit.DisplayMode = this.ViewModel.IsPanePinned && this.Shell.ActualWidth >= this.ViewModel.PaneWidth + 760
            ? SplitViewDisplayMode.Inline
            : SplitViewDisplayMode.Overlay;
        this.ToolPane.Style = (Style)Application.Current.Resources[
            this.ToolsSplit.DisplayMode == SplitViewDisplayMode.Overlay ? "OverlayToolPaneStyle" : "TabSurfaceStyle"];
        this.ToolsSplit.OpenPaneLength = Math.Min(this.ViewModel.PaneWidth, Math.Max(280, this.Shell.ActualWidth - 32));
        this.SidebarActions.SetMaxPrimaryCommands(this.ToolsSplit.OpenPaneLength < 360 ? 1 : 4);
        var bottomOpen = this.ViewModel.IsBottomPaneOpen && this.ViewModel.BottomTabs.Count > 0;
        this.BottomPane.Visibility = bottomOpen ? Visibility.Visible : Visibility.Collapsed;
        this.BottomPaneRow.Height = new GridLength(
            bottomOpen ? Math.Min(this.ViewModel.BottomPaneHeight, Math.Max(100, this.Shell.ActualHeight - 240)) : 0);
        this.BottomActions.MaxWidth = Math.Clamp(this.Shell.ActualWidth / 3, 160, 280);
        this.BottomFilter.Width = Math.Clamp(this.Shell.ActualWidth / 7, 120, 220);
    }

    private void ToolResize_DragDelta(object sender, DragDeltaEventArgs args)
    {
        this.ViewModel.ResizePane(-args.HorizontalChange);
        this.UpdateToolLayout();
    }

    private void ToolResize_DragCompleted(object sender, DragCompletedEventArgs args)
    {
        this.SaveToolPreferences();
    }

    private void ToolResize_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        args.Handled = true;
        this.ViewModel.ResizePane(args.Key == VirtualKey.Left ? 20 : -20);
        this.UpdateToolLayout();
        this.SaveToolPreferences();
    }

    private void SaveToolPreferences()
    {
        try
        {
            this.ViewModel.SaveLayout();
            this._preferenceNotice?.Dismiss();
        }
        catch (Exception ex)
        {
            this.Logger.LogFailure(LogLevel.Warning, "Save tool preferences", ex);
            this._preferenceNotice = StatusNotifications.Show(
                this.Notifications, "Could not save preferences", ex.Message, NoticeKind.Error, key: "tool-preferences");
        }
    }

    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (this.ViewModel.CanCloseWindow && !this.Dialogs.IsOpen)
        {
            return;
        }

        args.Cancel = true;
        // Leave the native closing callback before potentially destroying the window.
        this.DispatcherQueue.TryEnqueue(() => this.ViewModel.ExitCommand.Execute(null));
    }

    private async Task<NoteCloseChoice> ConfirmDirtyNotesAsync(string? error)
    {
        var dialog = new AppDialog(
            "Unsaved notes", error ?? "Save your note changes before closing?", "Save notes", defaultButton: ContentDialogButton.Close)
        {
            SecondaryButtonText = "Discard notes"
        };
        return await this.Dialogs.ShowCloseConfirmationAsync(dialog) switch
        {
            ContentDialogResult.Primary => NoteCloseChoice.Save,
            ContentDialogResult.Secondary => NoteCloseChoice.Discard,
            _ => NoteCloseChoice.Cancel
        };
    }

    private async void RequestClose()
    {
        try
        {
            if (this.Dialogs.IsOpen)
            {
                this.ShowApplicationNotice("Can't close yet", "Close the open dialog, then try again.", NoticeKind.Warning);
                return;
            }

            if (await this.ViewModel.TryCloseAsync(this.ConfirmDirtyNotesAsync))
            {
                this.Close();
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                this.Logger.LogFailure(LogLevel.Warning, "Close window", ex);
            }

            this.ShowApplicationNotice("Couldn't close the window", ex.Message, NoticeKind.Error);
        }
    }
}
