// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private async Task ShowRecentInspectionsAsync()
    {
        if (this.ViewModel.InspectionHistory is not { } store)
        {
            return;
        }

        try
        {
            var history = await store.ReadAsync();
            var form = new RecentInspectionsView(store, history);
            var dialog = new DialogLayout("Recently inspected", form, "Open value", "Close")
            {
                OnOpened = form.FocusInitial,
                CanAccept = form.Selected is not null
            };
            form.SelectionChanged += () => dialog.CanAccept = form.Selected is not null;
            if (await this.Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary && form.Selected is { } selected)
            {
                this.ViewModel.OpenBrowser(selected.Location, selected.ValueName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            this.Logger.LogFailure(LogLevel.Warning, "Open inspection history", ex);
            this.ShowApplicationNotice("Couldn’t open inspection history", "Try again shortly.", NoticeKind.Warning);
        }
    }
}
