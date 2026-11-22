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

    private void StartSearch(SavedSearch search, string? input = null)
    {
        RegistrySearch.Validate(search);
        this._search = search;
        this.Query = input ?? SearchQueryText.Format(search.Options);
        this.ResultsRequested?.Invoke(search);
    }

    [RelayCommand]
    private void QuickSearch()
    {
        if (this._closed || string.IsNullOrWhiteSpace(this.Query))
        {
            return;
        }

        try
        {
            var target = location();
            var options = SearchQueryText.Parse(this.Query);
            var roots = target.GetScopeRoots(target.IsComputer, includeAliases: options.Activity != SearchActivity.None);
            this.StartSearch(new SavedSearch(this.Query, roots, options), this.Query);
        }
        catch (ArgumentException ex)
        {
            this.Notice?.Invoke("Could not start search", ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private async Task FindAsync(string? query)
    {
        if (this.FindRequested is null || this._closed)
        {
            return;
        }

        try
        {
            var typed = SearchQueryText.ParseDraft(query ?? this.Query, out var error, out var hasMatchSelection);
            var form = new FindOptionsViewModel(location(), typed.Text, this._search?.Options, typed) { InitialError = error };
            if (hasMatchSelection)
            {
                form.Keys = typed.Keys;
                form.Values = typed.Values;
                form.Data = typed.Data;
                form.Notes = typed.Notes;
            }

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
