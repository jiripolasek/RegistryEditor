// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// Core exposes the list through IReadOnlyList, so the XAML binding cannot infer its concrete WinRT projection.
[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(List<RegistryDifference>))]

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class SnapshotComparisonView : UserControl
{
    internal SnapshotComparisonViewModel ViewModel { get; }

    internal SnapshotComparisonView(SnapshotComparisonViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        viewModel.Notice += (title, message, kind) => { StatusNotice.Show(this.ResultMessage, title, message, (InfoBarSeverity)kind); };
        this.SizeChanged += (_, _) => this.BeforeData.MaxHeight = this.AfterData.MaxHeight = Math.Clamp(this.ActualHeight / 4, 80, 170);
    }

    internal async Task ShowAsync(FrameworkElement owner, WindowDialogPresenter dialogs)
    {
        var dialog = new SnapshotComparisonDialog(this);

        void UpdateSize(XamlRoot root, XamlRootChangedEventArgs? args)
        {
            this.Width = Math.Min(950, Math.Max(240, root.Size.Width - 96));
            this.Height = Math.Min(600, Math.Max(240, root.Size.Height - 220));
        }

        UpdateSize(owner.XamlRoot, null);
        var root = owner.XamlRoot;
        root.Changed += UpdateSize;
        try
        {
            await dialogs.ShowAsync(dialog);
        }
        finally
        {
            root.Changed -= UpdateSize;
            this.ViewModel.ExportCommand.Cancel();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs args)
    {
        this.DifferencesList.SelectAll();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs args)
    {
        this.DifferencesList.SelectedItems.Clear();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs args)
    {
        this.ViewModel.Select(
            this.DifferencesList.SelectedItems.OfType<RegistryDifference>().ToArray(),
            args.AddedItems.OfType<RegistryDifference>().LastOrDefault());
    }
}
