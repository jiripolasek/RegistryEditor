// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class FindOptionsViewModel(
    RegistryLocation location, string query, SearchOptions? previous, SearchOptions? typed = null) : ObservableObject
{
    public string Query { get; set; } = query;

    internal string? InitialError { get; init; }

    public bool Keys { get; set; } = previous is { Activity: SearchActivity.None } ? previous.Keys : true;

    public bool Values { get; set; } = previous?.Values ?? true;

    public bool Data { get; set; } = previous?.Data ?? true;
    public bool Notes { get; set; } = previous?.Notes ?? false;

    public bool Whole { get; set; } = previous?.Whole ?? false;

    public bool MatchCase { get; set; } = previous?.MatchCase ?? false;

    public string Types { get; set; } = previous?.Types is { } types ? string.Join(", ", types.Select(type => (uint)type)) : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity), nameof(ShowInspectionHint), nameof(CanMatchKeyFields), nameof(MatchKeyNames), nameof(MatchNotes))]
    public partial int ActivityIndex { get; set; } = (int)((typed ?? previous)?.Activity ?? SearchActivity.None);

    public string Within { get; set; } = (typed ?? previous)?.ActivityWithin is { } within
        ? SearchQueryText.FormatWindow(within)
        : string.Empty;

    public bool HasActivity => this.ActivityIndex > 0;
    public bool ShowInspectionHint => (SearchActivity)this.ActivityIndex is SearchActivity.Inspected or SearchActivity.Either;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyModified), nameof(HasCustomModifiedRange))]
    public partial int KeyModifiedIndex { get; set; } = ModifiedIndex((typed ?? previous)?.KeyModified);

    public bool HasKeyModified => this.KeyModifiedIndex > 0;

    public bool HasCustomModifiedRange => this.KeyModifiedIndex == KeyModifiedFilter.Presets.Count + 1;

    public DateTimeOffset? ModifiedFrom { get; set; } = PickerDate((typed ?? previous)?.KeyModified?.From);

    public DateTimeOffset? ModifiedThrough { get; set; } = PickerDate((typed ?? previous)?.KeyModified?.Through);

    public bool CanMatchKeyFields => !this.HasActivity;

    public bool MatchKeyNames
    {
        get => this.CanMatchKeyFields && this.Keys;
        set
        {
            if (this.CanMatchKeyFields)
            {
                this.Keys = value;
            }
        }
    }

    public bool MatchNotes
    {
        get => this.CanMatchKeyFields && this.Notes;
        set
        {
            if (this.CanMatchKeyFields)
            {
                this.Notes = value;
            }
        }
    }

    public bool CanChooseScope => !location.IsComputer;

    public int ScopeIndex { get; set; } = location.IsComputer ? 1 : 0;

    public string AllHivesLabel { get; }
        = $"All hives on {location.Machine ?? "this computer"} · {(location.View == RegistryView.Registry32 ? "32" : "64")}-bit";

    internal SavedSearch CreateSearch()
    {
        var activity = (SearchActivity)this.ActivityIndex;
        TimeSpan? within = this.HasActivity && !string.IsNullOrWhiteSpace(this.Within)
            ? SearchQueryText.ParseWindow(this.Within.AsSpan().Trim())
            : null;
        var modified = this.HasCustomModifiedRange
            ? new KeyModifiedFilter(From: LocalDate(this.ModifiedFrom), Through: LocalDate(this.ModifiedThrough))
            : this.HasKeyModified ? new KeyModifiedFilter(Within: KeyModifiedFilter.Presets[this.KeyModifiedIndex - 1]) : null;
        var options = new SearchOptions(
            this.Query, this.MatchKeyNames, this.Values, this.Data, this.MatchCase, this.Whole,
            RegistrySearch.ParseTypes(this.Types), activity, within, modified, Notes: this.MatchNotes);
        var name = string.IsNullOrWhiteSpace(this.Query) && !this.HasActivity && modified is null
            ? "Registry search"
            : SearchQueryText.Format(options);
        var roots = location.GetScopeRoots(this.ScopeIndex == 1, includeAliases: this.HasActivity);
        var search = new SavedSearch(name, roots, options);
        RegistrySearch.Validate(search);
        return search;
    }

    private static int ModifiedIndex(KeyModifiedFilter? filter) => filter is null ? 0
        : filter.Within is { } window ? KeyModifiedFilter.Presets.IndexOf(window) + 1 : KeyModifiedFilter.Presets.Count + 1;

    private static DateTimeOffset? PickerDate(DateOnly? date) => date is { } day
        ? new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Local))
        : null;

    private static DateOnly? LocalDate(DateTimeOffset? date) => date is { } day ? DateOnly.FromDateTime(day.LocalDateTime) : null;
}
