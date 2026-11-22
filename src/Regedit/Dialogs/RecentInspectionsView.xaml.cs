// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RecentInspectionsView : UserControl
{
    private readonly InspectionHistoryStore _store;
    private InspectionHistory _history;
    private bool _updating;

    internal event Action? SelectionChanged;

    internal InspectionVisit? Selected => this.Visits.SelectedItem as InspectionVisit;

    internal RecentInspectionsView(InspectionHistoryStore store, InspectionHistory history)
    {
        this._store = store;
        this._history = history;
        this._updating = true;
        this.InitializeComponent();
        this.ApplyHistory();
        this._updating = false;
    }

    internal void FocusInitial()
    {
        (this.Selected is null ? (Control)this.Recording : this.Visits).Focus(FocusState.Programmatic);
    }

    private async void Recording_Toggled(object sender, RoutedEventArgs args)
    {
        if (!this._updating)
        {
            var enabled = this.Recording.IsOn;
            await this.UpdateAsync(() => this._store.SetRecordingAsync(enabled));
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs args)
    {
        await this.UpdateAsync(this._store.ClearAsync);
        this.FocusInitial();
    }

    private void Visits_SelectionChanged(object sender, SelectionChangedEventArgs args) => this.SelectionChanged?.Invoke();

    private async Task UpdateAsync(Func<Task<InspectionHistory>> update)
    {
        this._updating = true;
        this.Recording.IsEnabled = this.ClearButton.IsEnabled = false;
        this.ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            this._history = await update();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            this.ErrorText.Text = "Couldn’t update inspection history. Try again.";
            this.ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            this.ApplyHistory();
            this.Recording.IsEnabled = true;
            this._updating = false;
        }
    }

    private void ApplyHistory()
    {
        var hasVisits = this._history.Visits.Length > 0;
        this.Recording.IsOn = this._history.IsRecording;
        this.ClearButton.IsEnabled = hasVisits;
        this.StatusText.Text = hasVisits ? string.Empty : "No inspections recorded.";
        this.StatusText.Visibility = hasVisits ? Visibility.Collapsed : Visibility.Visible;
        this.Visits.Visibility = hasVisits ? Visibility.Visible : Visibility.Collapsed;
        this.Visits.ItemsSource = this._history.Visits;
        this.Visits.SelectedIndex = hasVisits ? 0 : -1;
    }
}
