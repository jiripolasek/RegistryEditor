// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class BrowserSearchViewModel(Func<RegistryLocation> location) : ObservableObject
{
    internal event Func<FindOptionsViewModel, Task<SavedSearch?>>? FindRequested;

    internal event Action<SavedSearch>? ResultsRequested;

    internal event Action<string, string, NoticeKind>? Notice;

    internal event Action<string>? StatusChanged;

    private SavedSearch? _search;
    private bool _closed;

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    private void StartSearch(SavedSearch search)
    {
        RegistrySearch.Validate(search);
        this._search = search;
        this.Query = search.Options.Text;
        this.ResultsRequested?.Invoke(search);
    }

    [RelayCommand]
    private void QuickSearch()
    {
        if (this._closed || string.IsNullOrWhiteSpace(this.Query))
        {
            return;
        }

        var target = location();
        this.StartSearch(
            new SavedSearch(
                this.Query, target.GetScopeRoots(target.IsComputer), new SearchOptions(this.Query, true, true, true, false, false)));
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (this.FindRequested is null || this._closed)
        {
            return;
        }

        try
        {
            var form = new FindOptionsViewModel(location(), this.Query, this._search?.Options);
            if (await this.FindRequested(form) is { } search && !this._closed)
            {
                this.StartSearch(search);
            }
        }
        catch (Exception ex)
        {
            this.Notice?.Invoke("Could not start search", ex.Message, NoticeKind.Error);
        }
    }

    internal void ReportStatus(string message)
    {
        if (!this._closed)
        {
            this.StatusChanged?.Invoke(message);
        }
    }

    internal void ReportFailure(Exception error)
    {
        if (!this._closed)
        {
            this.ReportStatus("Could not open search result.");
            this.Notice?.Invoke("Could not open search result", error.Message, NoticeKind.Error);
        }
    }

    internal void Close()
    {
        this._closed = true;
    }
}
