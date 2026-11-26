// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class RegistryUsagePage : Page
{
    private readonly MainWindow _window;

    internal RegistryUsageTab ViewModel { get; }

    private SettingsViewModel Preferences => this._window.Preferences;

    internal Button MenuButton => this.PageHeader.MenuButton;

    internal ListView ResultsList => this.Subkeys.ListView;

    internal void FocusResults() => this.ResultsList.Focus(FocusState.Keyboard);

    internal RegistryUsagePage(MainWindow window, RegistryUsageTab viewModel)
    {
        this.ViewModel = viewModel;
        this._window = window;
        this.InitializeComponent();
        this.UsageToolbar.HideUtilities();
        this.Subkeys.KeepFocusOnItemsSourceChange = true;
        this.Loaded += (_, _) =>
        {
            this.Preferences.PropertyChanged += this.Preferences_PropertyChanged;
            this.ViewModel.ShowParentKeyInList = this.Preferences.ShowParentKeyInList;
        };
        this.Unloaded += (_, _) => this.Preferences.PropertyChanged -= this.Preferences_PropertyChanged;
        viewModel.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(RegistryUsageTab.Rows))
            {
                this.Subkeys.SelectFirstOnNextItemsSourceChange = true;
            }
        };

        this.Subkeys.Columns.Add(new ListColumn
        {
            Id = "Subkey", Header = "Subkey", MinWidth = 120, FillWeight = 5, CanHide = false,
            CellText = item => item is RegistryUsageEntry row
                ? this.ViewModel.IsParentRow(row) ? ".." : row.Location.Name
                : null
        });
        this.Subkeys.Columns.Add(NumberColumn("ValueData", "Value data (bytes)", 120, row => row.ValueDataBytes));
        this.Subkeys.Columns.Add(NumberColumn("Keys", "Keys", 72, row => row.KeyCount));
        this.Subkeys.Columns.Add(NumberColumn("Values", "Values", 72, row => row.ValueCount));
        this.Subkeys.Columns.Add(new ListColumn
        {
            Id = "Status", Header = "Status", MinWidth = 104, DefaultWidth = 112,
            CellTemplate = (DataTemplate)this.Resources["UsageStatusCell"]
        });
        this.Subkeys.ItemAutomationName = item => item switch
        {
            RegistryUsageEntry row when this.ViewModel.IsParentRow(row) => ".., Parent key",
            RegistryUsageEntry { KeyCount: 0 } row => $"{row.Location.Name}, unavailable. {RowStatusHelp(row.KeyCount, row.SkippedBranches)}",
            RegistryUsageEntry row => $"{row.Location.Name}, {RegistryUsageTab.FormatCount(row.ValueDataBytes, "byte")}"
                                      + $", {RegistryUsageTab.FormatCount(row.KeyCount, "key")}, {RegistryUsageTab.FormatCount(row.ValueCount, "value")}"
                                      + (row.SkippedBranches > 0 ? $", partial results. {RowStatusHelp(row.KeyCount, row.SkippedBranches)}" : string.Empty),
            _ => null
        };
        var list = this.ResultsList;
        AutomationProperties.SetName(list, "Subkeys by value data size");
        AutomationProperties.SetAutomationId(list, "RegistryUsageSubkeys");
        list.Loaded += this.ResultsList_FirstLoaded;
        list.SelectionChanged += (_, _) => this.ViewModel.SelectedSubkey = (list.SelectedItem as RegistryUsageEntry)?.Location;
        viewModel.SelectionRequested += location =>
            list.SelectedItem = viewModel.Rows.FirstOrDefault(row => location.IsSameLocation(row.Location));
        list.DoubleTapped += this.Subkey_DoubleTapped;
        list.PreviewKeyDown += this.Subkey_PreviewKeyDown;
        ListContextMenu.Attach(list, this.CreateSubkeyMenu);
    }

    private void SubkeyIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var children = ((Grid)sender).Children;
        var subkeyIcon = (FontIcon)children[0];
        var parentIcon = (PathIcon)children[1];
        var isParentRow = args.NewValue is RegistryUsageEntry row && this.ViewModel.IsParentRow(row);
        subkeyIcon.Visibility = isParentRow ? Visibility.Collapsed : Visibility.Visible;
        parentIcon.Visibility = isParentRow ? Visibility.Visible : Visibility.Collapsed;
        if (isParentRow && parentIcon.Data is null)
        {
            parentIcon.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry),
                Application.Current.Resources["RegistryParentIconData"]);
        }
    }

    private void ResultsList_FirstLoaded(object sender, RoutedEventArgs args)
    {
        this.ResultsList.Loaded -= this.ResultsList_FirstLoaded;
        if (this.ResultsList.XamlRoot is not { } root)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(root);
        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (this.ResultsList.IsLoaded && this.ResultsList.XamlRoot == root
                && ReferenceEquals(this._window.ViewModel.ActiveTab, this.ViewModel) && !this._window.Dialogs.IsOpen
                && ReferenceEquals(FocusManager.GetFocusedElement(root), focused))
            {
                this.FocusResults();
            }
        });
    }

    private void Preferences_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.ShowParentKeyInList))
        {
            this.ViewModel.ShowParentKeyInList = this.Preferences.ShowParentKeyInList;
        }
    }

    private MenuFlyout? CreateSubkeyMenu()
    {
        if (this.Subkeys.ListView.SelectedItem is not RegistryUsageEntry row)
        {
            return null;
        }

        var menu = new MenuFlyout();
        var open = this.ViewModel.IsParentRow(row) ? "Open parent key" : "Open subkey";
        var target = new RegistryUsageTab.ScanTarget(row.Location);
        ListContextMenu.Add(
            menu, open, () => this._window.RunAfterMenuCloses(menu, () => this.TryScan(target)),
            this.ViewModel.ScanCommand.CanExecute(target), icon: Symbol.OpenFile, shortcut: "Enter");
        ListContextMenu.Add(
            menu, "Open in browser", () => this._window.RunAfterMenuCloses(menu, () => this._window.ViewModel.OpenBrowser(row.Location)),
            iconSource: (IconSource)Application.Current.Resources["OpenInNewTabIcon"]);
        return menu;
    }

    private static ListColumn NumberColumn(string id, string header, double minimumWidth, Func<RegistryUsageEntry, long> value)
    {
        return new ListColumn
        {
            Id = id,
            Header = header,
            MinWidth = minimumWidth,
            DefaultWidth = minimumWidth,
            HeaderAlignment = HorizontalAlignment.Right,
            CellText = item => item is RegistryUsageEntry { KeyCount: > 0 } row ? $"{value(row):N0}" : null
        };
    }

    internal static Visibility RowStatusVisibility(long skippedBranches) => skippedBranches > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal static string RowStatusText(long keyCount) => keyCount == 0 ? "Unavailable" : "Partial";

    internal static string RowStatusHelp(long keyCount, long skippedBranches) => keyCount == 0
        ? "This subkey could not be opened during the scan. Its contents are excluded from the totals."
        : skippedBranches > 0 ? RegistryUsageTab.DescribePartialResults(skippedBranches) : string.Empty;

    private static string ScanProgressText(bool isCancellationRequested) =>
        isCancellationRequested ? "Canceling scan…" : "Scanning registry branch…";

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Back or VirtualKey.F5)
            || this._window.Dialogs.IsOpen)
        {
            return;
        }

        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        var needsAlt = args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up;
        if (Down(VirtualKey.Menu) != needsAlt || Down(VirtualKey.Control) || Down(VirtualKey.Shift)
            || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
        {
            return;
        }

        this.TryScan(args.Key switch
        {
            VirtualKey.Left or VirtualKey.Back => this.ViewModel.BackTarget,
            VirtualKey.Right => this.ViewModel.ForwardTarget,
            VirtualKey.Up => this.ViewModel.UpTarget,
            _ => this.ViewModel.CurrentTarget
        });
        args.Handled = true;
    }

    private void Subkey_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        for (var source = args.OriginalSource as DependencyObject;
             source is not null && !ReferenceEquals(source, sender);
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is ListViewItem { Content: RegistryUsageEntry row })
            {
                args.Handled = this.TryScan(new(row.Location));
                return;
            }
        }
    }

    private void Subkey_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || sender is not ListView list)
        {
            return;
        }

        var focused = (FocusManager.GetFocusedElement(list.XamlRoot) as ListViewItem)?.Content as RegistryUsageEntry;
        if ((focused ?? list.SelectedItem as RegistryUsageEntry) is { } row)
        {
            list.SelectedItem = row;
            args.Handled = this.TryScan(new(row.Location));
        }
    }

    private bool TryScan(RegistryUsageTab.ScanTarget? target)
    {
        if (!this.ViewModel.ScanCommand.CanExecute(target))
        {
            return false;
        }

        this.ViewModel.ScanCommand.Execute(target);
        return true;
    }

    private void CancelScan_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.ViewModel.ScanCancelCommand.CanExecute(null))
        {
            this.ViewModel.ScanCancelCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void OpenKey_Click(object sender, RoutedEventArgs args)
    {
        this._window.ViewModel.OpenBrowser(this.ViewModel.Location);
    }
}
