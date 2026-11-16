// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Views.Tools;

public sealed partial class ToolView
{
    private void UpdateCount()
    {
        this.Count.Text = $"{this.Rows.Count:N0} {(this.Rows.Count == 1 ? "item" : "items")}" +
                          (this.Selected.Length > 0 ? $" · {this.Selected.Length:N0} selected" : string.Empty) +
                          (this.Tool == ToolKind.Journal ? " · Retained until removed" : string.Empty);
    }

    private void UpdateDetails()
    {
        this.UpdateCount();
        var selected = this.Selected;
        this.SyncResultSelection();
        var detailTool = this.Tool is ToolKind.Journal or ToolKind.Watches;
        var wide = !this._compact && !this._docked &&
                   this.Layout.ActualWidth - this.Layout.Padding.Left - this.Layout.Padding.Right >= 1000;
        var visible = detailTool && selected.Length > 0 && (wide ? !this._detailsCollapsed : this._detailsExpanded);
        this.Details.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (this.Tool == ToolKind.Notes)
        {
            visible = this._compact || this.Notes?.HasNote == true && !this._detailsCollapsed;
            this.NotePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (this._inspectCommand is { } inspect)
        {
            inspect.IsEnabled = this.Tool == ToolKind.Notes ? this.Notes?.HasNote == true : selected.Length > 0;
            inspect.IsChecked = visible;
            inspect.Label = visible ? "Hide details" : "Show details";
        }

        if (visible && detailTool)
        {
            if (selected.Length == 0)
            {
                this.Details.Show("Select an item", string.Empty, "Select an item to inspect its details.");
            }
            else if (selected.Length > 1)
            {
                this.Details.Show(
                    $"{selected.Length:N0} items selected", string.Empty, this.Tool switch
                    {
                        ToolKind.Journal =>
                            "Copy a script for the selected changes. Select one edit to inspect or revert it.",
                        ToolKind.Watches =>
                            "Copy a script for the selected observations. Select one observation to inspect it.",
                        _ => "Use the collection actions to organize these items."
                    });
            }
            else
            {
                var row = selected[0];
                switch (row.Source)
                {
                    case JournalEntry entry:
                        var changes = this.ResultActions.Differences(entry);
                        var conflict = this.JournalHistory.BlockingEdit(entry);
                        var views = entry.Before.Select(state => state.Scope.Location)
                            .Concat(entry.ActivityLocation is { } location ? (RegistryLocation[])[location] : []).Select(location =>
                                location.View == RegistryView.Registry32 ? "32-bit" : "64-bit").Distinct();
                        this.Details.Show(
                            entry.Operation, entry.Path, $"{row.Status}\n{string.Join(", ", views)}",
                            entry.Activity is not null
                                ? (ToolComparison[])[new ToolComparison("Activity", entry.Activity, string.Empty, "Details")]
                                : changes.Take(30).Select(change =>
                                    changes.Count == 1
                                        ? ToolPresentation.Compare(change) with { Name = string.Empty }
                                        : ToolPresentation.Compare(change)),
                            changes.Count > 30
                                ? $"Showing the first 30 of {changes.Count:N0} differences. Script copying includes all selected differences."
                                : string.Empty, entry.Time, conflict?.Reason ?? string.Empty);
                        break;
                    case WatchObservation observation:
                        this.Details.Show(
                            observation.Change.Change, observation.Change.Location.Address,
                            observation.Change.Location.View == RegistryView.Registry32 ? "32-bit" : "64-bit",
                            (ToolComparison[])[ToolPresentation.Compare(observation.Change) with { Name = string.Empty }],
                            timestamp: observation.Time);
                        break;
                }
            }
        }

        this.UpdateLayoutForWidth();
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (this.Tool is ToolKind.Journal or ToolKind.Watches)
        {
            this.UpdateDetails();
            return;
        }

        this.UpdateLayoutForWidth();
    }

    private void UpdateLayoutForWidth()
    {
        if (this.Workspace is null)
        {
            return;
        }

        var width = this.Layout.ActualWidth;
        var stacked = this._compact || this._docked || width < 900;
        this.CaptureStatus.MaxWidth = this.Tool == ToolKind.Watches ? (stacked ? 220 : 360) : double.PositiveInfinity;
        this.Layout.Padding = this._compact || this._docked
            ? new Thickness(0)
            : new Thickness(stacked ? 16 : 24, 24, 12, 16);
        this.PageHeader.Margin = new Thickness(0, 0, 0, 16);
        this.LocalControls.Margin = new Thickness(0, 0, 0, 12);

        var compactNotes = this.Tool == ToolKind.Notes && this._compact;
        this.ListWidth.Width = compactNotes ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        this.Collection.Visibility = compactNotes ? Visibility.Collapsed : Visibility.Visible;
        var inspector = this.Tool == ToolKind.Notes ? (FrameworkElement)this.NotePanel : this.Details;
        var visible = inspector.Visibility == Visibility.Visible;
        var beside = this._docked || !this._compact && width - this.Layout.Padding.Left - this.Layout.Padding.Right >=
            (this.Tool == ToolKind.Notes ? 760 : 1000);
        this.InspectorWidth.Width = visible && (beside || compactNotes)
            ? this.Tool == ToolKind.Notes ? new GridLength(1, GridUnitType.Star) : new GridLength(285)
            : new GridLength(0);
        this.InspectorHeight.Height = visible && !beside && !compactNotes
            ? new GridLength(2, GridUnitType.Star)
            : new GridLength(0);
        Grid.SetColumn(inspector, beside || compactNotes ? 1 : 0);
        Grid.SetRow(inspector, beside || compactNotes ? 0 : 1);
        Grid.SetColumnSpan(inspector, beside || compactNotes ? 1 : 2);
        this.Workspace.ColumnSpacing = 0;
        this.Workspace.RowSpacing = visible && !beside && !compactNotes ? 12 : 0;
        inspector.Margin = new Thickness(beside ? 16 : 0, 0, 0, 0);
        this.DetailsSplitter.Arrange(
            inspector, this.InspectorWidth, this.InspectorHeight, beside, visible && !compactNotes,
            () => this.ListWidth.ActualWidth + this.InspectorWidth.ActualWidth);
    }
}
