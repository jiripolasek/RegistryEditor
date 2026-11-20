// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Browser;
using JPSoftworks.Regedit.Infrastructure.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
    internal void FocusSearch()
    {
        this.QuickSearch.Focus(FocusState.Keyboard);
    }

    internal IEnumerable<(FrameworkElement Root, Action Focus)> GetFocusPanes()
    {
        if (this.NavigationPane.Visibility == Visibility.Visible)
        {
            yield return (this.TreePane, this.TreePane.FocusTree);
        }

        yield return (this.EntryList, () => this.EntryList.FocusList(FocusState.Keyboard));
        if (this.ViewModel.IsInspectorOpen)
        {
            yield return (this.ValueInspectorPane, () =>
            {
                var field = FocusManager.FindFirstFocusableElement(this.ValueInspectorContent) as UIElement ?? this.ValueInspectorScroll;
                field.Focus(FocusState.Keyboard);
                field.StartBringIntoView();
            });
        }
    }

    internal void FocusToolsButton()
    {
        this.ToolsButton.Focus(FocusState.Keyboard);
    }

    private void HistoryMenu_Opening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        foreach (var entry in this.ViewModel.GetHistory(menu.Target == this.ForwardButton))
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{entry.Location.Address} · {(entry.Location.View == RegistryView.Registry32 ? "32-bit" : "64-bit")}",
                Icon = new FontIcon { Glyph = RegistryTreePane.TreeGlyph(entry.Location) }
            };
            item.Click += async (_, _) => await this.GuardAsync(
                "Couldn't open the registry key", "Navigation canceled",
                () => this.ViewModel.NavigateHistoryAsync(entry.Index, entry.Location));
            menu.Items.Add(item);
        }
    }

    internal bool TryNavigateHistory(bool forward)
    {
        if (this._window.Dialogs.IsOpen
            || this.XamlRoot is not { } root
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is ContentDialog))
        {
            return false;
        }

        var command = forward ? this.ViewModel.ForwardCommand : this.ViewModel.BackCommand;
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private async void Browser_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Z or VirtualKey.Y)
        {
            if (this._window.Dialogs.IsOpen || !RegistryKeyboardShortcuts.IsPlainControl(this.XamlRoot))
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(this.XamlRoot) as DependencyObject;
            var inList = IsInside(focused, this.EntryList);
            if (inList || IsInside(focused, this.TreePane) || IsInside(focused, this.Commands))
            {
                args.Handled = true;
                var command = args.Key == VirtualKey.Z ? this.ViewModel.UndoCommand : this.ViewModel.RedoCommand;
                if (command.CanExecute(null))
                {
                    if (inList)
                    {
                        await this.GuardAsync(
                            "Couldn't complete the registry action", "Registry action canceled",
                            () => this.EntryList.KeepPlaceAsync(() => command.ExecuteAsync(null)));
                    }
                    else
                    {
                        command.Execute(null);
                    }
                }
            }

            return;
        }

        if (args.Key is not (VirtualKey.C or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Back))
        {
            return;
        }

        static bool Down(VirtualKey key)
        {
            return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        }

        var alt = Down(VirtualKey.Menu);
        var control = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var copyPath = args.Key == VirtualKey.C && control && shift && !alt;
        var navigate = args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up && alt && !control && !shift;
        var back = args.Key == VirtualKey.Back
                   && !alt
                   && !control
                   && !shift
                   && !Down(VirtualKey.LeftWindows)
                   && !Down(VirtualKey.RightWindows)
                   && FocusManager.GetFocusedElement(this.XamlRoot) is not (TextBox or RichEditBox or PasswordBox);
        if (!copyPath && !navigate && !back)
        {
            return;
        }

        // Text editors and tree items can consume these shortcuts before a page accelerator runs.
        args.Handled = true;
        if (this._window.Dialogs.IsOpen
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(this.XamlRoot).Any(popup => popup.Child is ContentDialog))
        {
            return;
        }

        if (copyPath)
        {
            this.CopyFocusedKeyPath();
        }
        else if (back && IsInside(FocusManager.GetFocusedElement(this.XamlRoot) as DependencyObject, this.TreePane))
        {
            await this.GuardAsync("Couldn't open the parent key", "Navigation canceled", this.TreePane.NavigateParentAsync);
        }
        else if (args.Key == VirtualKey.Up)
        {
            if (this.ViewModel.UpCommand.CanExecute(null))
            {
                this.ViewModel.UpCommand.Execute(null);
            }
        }
        else
        {
            this.TryNavigateHistory(args.Key == VirtualKey.Right);
        }
    }

    private void SearchArea_FocusChanged(object sender, RoutedEventArgs args)
    {
        if (this.XamlRoot is not { } root)
        {
            return;
        }

        var focused = IsInside(FocusManager.GetFocusedElement(root) as DependencyObject, this.SearchArea);
        VisualStateManager.GoToState(this, focused ? "SearchFocused" : "SearchUnfocused", false);
    }

    private void QuickSearch_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        this.ViewModel.Search.Query = args.QueryText;
        this.ViewModel.Search.QuickSearchCommand.Execute(null);
    }

    private void Commands_AccessKeyInvoked(UIElement sender, AccessKeyInvokedEventArgs args)
    {
        this.Commands.IsOpen = true;
        args.Handled = true;
    }

    private void NewItem_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.NewItemMenu.ShowAt(this.EntryList, new FlyoutShowOptions { Position = new Point(0, 0) });
        args.Handled = true;
    }

    private void Filter_Click(object sender, RoutedEventArgs args)
    {
        this.FilterArea.Visibility = Visibility.Visible;
        this.FilterBox.Focus(FocusState.Keyboard);
        this.FilterBox.SelectAll();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.Filter = string.Empty;
        this.FilterArea.Visibility = Visibility.Collapsed;
        this.EntryList.FocusList(FocusState.Keyboard);
    }

    private void Browser_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || this._window.Dialogs.IsOpen)
        {
            return;
        }

        args.Handled = true;
        if (this.FilterArea.Visibility == Visibility.Visible)
        {
            this.ClearFilter_Click(this, new RoutedEventArgs());
        }
        else
        {
            this._window.ViewModel.FindNextResultCommand.Cancel();
            this.ViewModel.Branches.ExportCommand.Cancel();
            this.ViewModel.Branches.PrintCommand.Cancel();
            this._window.CancelApplicationExport();
            this.ViewModel.Snapshots.Cancel();
        }
    }

    private async void Filter_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            this.ClearFilter_Click(sender, new RoutedEventArgs());
        }
        else if (args.Key == VirtualKey.Down)
        {
            args.Handled = true;
            var pending = this.ViewModel.PendingFilter;
            await pending;
            if (pending == this.ViewModel.PendingFilter
                && this.XamlRoot is { } root
                && IsInside(FocusManager.GetFocusedElement(root) as DependencyObject, this.FilterBox))
            {
                this.EntryList.FocusList(FocusState.Keyboard);
            }
        }
    }

    private void ViewMenu_Opening(object sender, object args)
    {
        this.NavigationPaneMenu.IsChecked = this.ListNavigationPaneMenu.IsChecked = !this.ViewModel.Layout.TreeHidden;
        this.Registry32Menu.IsChecked = this.ListRegistry32Menu.IsChecked = this.ViewModel.Location.View == RegistryView.Registry32;
        this.Registry64Menu.IsChecked = this.ListRegistry64Menu.IsChecked = !this.Registry32Menu.IsChecked;
    }

    private void ListSpacing_Click(object sender, RoutedEventArgs args)
    {
        this._window.ShowListSpacing();
    }

    private void SyncTree_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.SetTreeLayout(this.ViewModel.Layout.TreeWidth, false);
        this.UpdateNavigationPane(this.LayoutRoot.ActualWidth);
        this.TreePane.TrackLocation(this.ViewModel.Location);
    }
}
