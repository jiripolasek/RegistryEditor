// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Tools;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed partial class MainWindowViewModel
{
    internal event Action? SearchFocusRequested;

    private SearchResultsViewModel? _activeSearch;

    public ObservableCollection<WindowTab> BottomTabs { get; } = [];

    [ObservableProperty]
    public partial WindowTab? ActiveBottomTab { get; set; }

    [ObservableProperty]
    public partial bool IsBottomPaneOpen { get; set; }

    public double BottomPaneHeight { get; private set; } = 240;

    partial void OnActiveBottomTabChanged(WindowTab? value)
    {
        if (value is SearchResultsViewModel search)
        {
            this._activeSearch = search;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task FindNextResultAsync(CancellationToken token)
    {
        if (this.IsClosing || this.CanCloseWindow || this.IsDialogOpen)
        {
            return;
        }

        this.EnsureBrowserVisible();
        if (this._activeSearch is not { } search)
        {
            this.SearchFocusRequested?.Invoke();
            return;
        }

        var destination = (BrowserTab)this.ActiveTab!;
        var browser = destination.Browser;
        if (search.SelectNext() is not { } match)
        {
            browser.Search.ReportStatus($"{search.Title} · " + (search.IsBusy ? "Searching for more matches…" : "No more results."));
            return;
        }

        try
        {
            var selected = search.Selected;
            var navigation = browser.NavigateToMatchAsync(new(match.Location, match.ValueName), token);
            var version = browser.NavigationVersion;
            this._searchDestinations[search] = (destination, version);
            await navigation;
            token.ThrowIfCancellationRequested();
            if (this._activeSearch != search
                || this.VisibleBrowser != browser
                || !ReferenceEquals(search.Selected, selected)
                || browser.NavigationVersion != version)
            {
                return;
            }

            if (browser.Error is { } error)
            {
                throw new IOException(error);
            }

            browser.Search.ReportStatus(
                $"{search.Title} · {search.DisplayRows.IndexOf(search.Selected[0]) + 1:N0}/{search.DisplayRows.Count:N0}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (this._activeSearch == search && this.VisibleBrowser == browser)
            {
                browser.Search.ReportFailure(error);
            }
        }
    }

    internal void ShowBottomPane()
    {
        if (this.BottomTabs.Count == 0)
        {
            this.OpenBottomTool(ToolKind.Journal);
        }
        else
        {
            this.IsBottomPaneOpen = true;
        }
    }

    internal void ResizeBottomPane(double delta, double availableHeight)
    {
        var maximum = Math.Clamp(availableHeight - 240, 100, 600);
        this.BottomPaneHeight = PaneResize.Apply(this.BottomPaneHeight, delta, Math.Min(180, maximum), maximum);
    }

    internal void OpenBottomTool(ToolKind tool)
    {
        var tab = this.BottomTabs.Concat(this.Tabs).OfType<ToolTab>().FirstOrDefault(tab => tab.Tool == tool) ?? new ToolTab(tool);
        this.DockTab(tab);
    }

    internal void SelectBottomTab(WindowTab tab)
    {
        if (!this.BottomTabs.Contains(tab))
        {
            this.BottomTabs.Add(tab);
        }

        this.ActiveBottomTab = tab;
        this.IsBottomPaneOpen = true;
        this.EnsureBrowserVisible();
    }

    private void EnsureBrowserVisible()
    {
        if (this.ActiveTab is BrowserTab)
        {
            return;
        }

        if (this._contextTab is { } browser && this.Tabs.Contains(browser))
        {
            this.ActiveTab = browser;
        }
        else
        {
            this.OpenBrowser(new RegistryLocation());
        }
    }

    internal void CloseBottomTab(WindowTab tab)
    {
        var index = this.BottomTabs.IndexOf(tab);
        if (this.IsClosing || index < 0)
        {
            return;
        }

        this.BottomTabs.Remove(tab);
        if (this.ActiveBottomTab == tab)
        {
            this.ActiveBottomTab = this.BottomTabs.Count == 0 ? null : this.BottomTabs[Math.Min(index, this.BottomTabs.Count - 1)];
        }

        this.IsBottomPaneOpen &= this.BottomTabs.Count > 0;
        this.CancelTab(tab);
    }

    internal void PromoteBottomTab()
    {
        if (this.ActiveBottomTab is not { } tab)
        {
            return;
        }

        var index = this.BottomTabs.IndexOf(tab);
        this.BottomTabs.Remove(tab);
        this.ActiveBottomTab = this.BottomTabs.Count == 0 ? null : this.BottomTabs[Math.Min(index, this.BottomTabs.Count - 1)];
        this.IsBottomPaneOpen &= this.BottomTabs.Count > 0;
        this.SelectTab(tab);
    }

    internal void DockTab(WindowTab tab)
    {
        if (this.Tabs.Contains(tab))
        {
            if (this.ActiveTab == tab)
            {
                this.EnsureBrowserVisible();
            }

            this.Tabs.Remove(tab);
        }

        this.SelectBottomTab(tab);
    }
}
