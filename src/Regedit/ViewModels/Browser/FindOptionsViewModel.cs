// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed class FindOptionsViewModel(RegistryLocation location, string query, SearchOptions? previous)
{
    public string Query { get; set; } = query;

    public bool Keys { get; set; } = previous?.Keys ?? true;

    public bool Values { get; set; } = previous?.Values ?? true;

    public bool Data { get; set; } = previous?.Data ?? true;

    public bool Whole { get; set; } = previous?.Whole ?? false;

    public bool MatchCase { get; set; } = previous?.MatchCase ?? false;

    public string Types { get; set; } = previous?.Types is { } types
        ? string.Join(", ", types.Select(type => (uint)type))
        : string.Empty;

    public bool CanChooseScope => !location.IsComputer;

    public int ScopeIndex { get; set; } = location.IsComputer ? 1 : 0;

    public string AllHivesLabel { get; }
        = $"All hives on {location.Machine ?? "this computer"} · {(location.View == RegistryView.Registry32 ? "32" : "64")}-bit";

    internal SavedSearch CreateSearch()
    {
        if (this.Query.Length == 0 || !(this.Keys || this.Values || this.Data))
        {
            throw new ArgumentException("Enter text and choose at least one place to search.");
        }

        var options = new SearchOptions(
            this.Query, this.Keys, this.Values, this.Data, this.MatchCase, this.Whole,
            RegistrySearch.ParseTypes(this.Types));
        var name = string.IsNullOrWhiteSpace(this.Query) ? "Registry search" : this.Query;
        var search = new SavedSearch(
            name, location.GetScopeRoots(location.IsComputer || this.ScopeIndex == 1), options);
        RegistrySearch.Validate(search);
        return search;
    }
}
