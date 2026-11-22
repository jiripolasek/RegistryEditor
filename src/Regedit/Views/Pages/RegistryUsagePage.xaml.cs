// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class RegistryUsagePage : Page
{
    private readonly MainWindow _window;

    internal RegistryUsageTab ViewModel { get; }

    internal Button MenuButton => this.PageHeader.MenuButton;

    internal RegistryUsagePage(MainWindow window, RegistryUsageTab viewModel)
    {
        this.ViewModel = viewModel;
        this._window = window;
        this.InitializeComponent();
        this.UsageToolbar.HideUtilities();

        this.Subkeys.Columns.Add(new ListColumn
        {
            Id = "Subkey", Header = "Subkey", MinWidth = 120, FillWeight = 5, CanHide = false,
            CellText = item => (item as RegistryUsageEntry)?.Location.Name
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
            RegistryUsageEntry { KeyCount: 0 } row => $"{row.Location.Name}, unavailable. {RowStatusHelp(row.KeyCount, row.SkippedBranches)}",
            RegistryUsageEntry row => $"{row.Location.Name}, {RegistryUsageTab.FormatCount(row.ValueDataBytes, "byte")}"
                                      + $", {RegistryUsageTab.FormatCount(row.KeyCount, "key")}, {RegistryUsageTab.FormatCount(row.ValueCount, "value")}"
                                      + (row.SkippedBranches > 0 ? $", partial results. {RowStatusHelp(row.KeyCount, row.SkippedBranches)}" : string.Empty),
            _ => null
        };
        var list = this.Subkeys.ListView;
        AutomationProperties.SetName(list, "Subkeys by value data size");
        AutomationProperties.SetAutomationId(list, "RegistryUsageSubkeys");
        list.SelectionChanged += (_, _) => this.OpenSubkeyButton.CommandParameter = (list.SelectedItem as RegistryUsageEntry)?.Location;
        list.DoubleTapped += this.Subkey_DoubleTapped;
        list.PreviewKeyDown += this.Subkey_PreviewKeyDown;
        ListContextMenu.Attach(list, this.CreateSubkeyMenu);
    }

    private MenuFlyout? CreateSubkeyMenu()
    {
        if (this.Subkeys.ListView.SelectedItem is not RegistryUsageEntry row)
        {
            return null;
        }

        var menu = new MenuFlyout();
        ListContextMenu.Add(
            menu, "Open subkey", () => this._window.RunAfterMenuCloses(menu, () => this.TryOpenSubkey(row)),
            this.ViewModel.ScanCommand.CanExecute(row.Location), icon: Symbol.OpenFile, shortcut: "Enter");
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

    private void Subkey_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        for (var source = args.OriginalSource as DependencyObject;
             source is not null && !ReferenceEquals(source, sender);
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is ListViewItem { Content: RegistryUsageEntry row })
            {
                args.Handled = this.TryOpenSubkey(row);
                return;
            }
        }
    }

    private void Subkey_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && sender is ListView { SelectedItem: RegistryUsageEntry row })
        {
            args.Handled = this.TryOpenSubkey(row);
        }
    }

    private bool TryOpenSubkey(RegistryUsageEntry row)
    {
        if (!this.ViewModel.ScanCommand.CanExecute(row.Location))
        {
            return false;
        }

        this.ViewModel.ScanCommand.Execute(row.Location);
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
