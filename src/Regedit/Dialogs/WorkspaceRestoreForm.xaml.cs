// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class WorkspaceRestoreForm : UserControl
{
    internal CheckBox ReplaceTabs => this.ReplaceInput;

    internal WorkspaceRestoreOptions Options => new(this.ReplaceInput.IsChecked == true, this.StartInput.IsChecked == true);

    internal WorkspaceRestoreForm(RegistryWorkspace workspace)
    {
        this.InitializeComponent();
        this.SummaryText.Text = workspace.Name
                                + " · "
                                + WorkspacesViewModel.CountSummary(
                                    workspace.Tabs.Length, workspace.Searches.Length, workspace.Watches.Length);
        this.Targets.ItemsSource = workspace.Tabs.Select(location => "Tab: " + WorkspacesViewModel.Target(location))
            .Concat(
                workspace.Searches.Select(search =>
                    "Search: "
                    + search.Name
                    + " · Find: "
                    + search.Options.Text
                    + "\n"
                    + string.Join("\n", search.Roots.Select(WorkspacesViewModel.Target)))).Concat(
                workspace.Watches.Select(watch => "Watch: " + WatchSurfaceViewModel.FormatTarget(watch.Location, watch.ValueName)))
            .ToArray();
    }
}
