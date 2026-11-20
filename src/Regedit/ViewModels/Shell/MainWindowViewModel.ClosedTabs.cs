// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Settings;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed partial class MainWindowViewModel
{
    private readonly List<ClosedBrowserTab> _closedBrowserTabs = [];

    private bool CanReopenClosedTab => !this.IsClosing && this._closedBrowserTabs.Count > 0;

    private void RememberClosedTab(BrowserTab tab, int index)
    {
        this._closedBrowserTabs.Add(
            new ClosedBrowserTab(
                tab.Browser.Location, tab.Browser.CaptureLayout(),
                tab.Browser.SelectedEntries.Count == 1 ? tab.Browser.SelectedEntry?.Value?.Name : null, index));
        if (this._closedBrowserTabs.Count > 20)
        {
            this._closedBrowserTabs.RemoveAt(0);
        }

        this.ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanReopenClosedTab))]
    private void ReopenClosedTab()
    {
        if (!this.CanReopenClosedTab)
        {
            return;
        }

        var state = this._closedBrowserTabs[^1];
        var tab = this.OpenBrowser(state.Location, state.Value);
        tab.Browser.RestoreLayout(state.Layout);
        this.Tabs.Move(this.Tabs.IndexOf(tab), Math.Min(state.Index, this.Tabs.Count - 1));
        this._closedBrowserTabs.RemoveAt(this._closedBrowserTabs.Count - 1);
        this.ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    private sealed record ClosedBrowserTab(RegistryLocation Location, BrowserLayout Layout, string? Value, int Index);
}
