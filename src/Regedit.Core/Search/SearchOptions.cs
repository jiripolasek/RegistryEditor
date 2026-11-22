// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Search;

public sealed record SearchOptions(
    string Text,
    bool Keys = true,
    bool Values = true,
    bool Data = true,
    bool MatchCase = false,
    bool Whole = false,
    RegistryValueType[]? Types = null,
    SearchActivity Activity = SearchActivity.None,
    TimeSpan? ActivityWithin = null,
    KeyModifiedFilter? KeyModified = null,
    bool Notes = false)
{
    internal bool MatchKeyResults => this.Keys && (this.Text.Length > 0 || this.Types is not { Length: > 0 });

    internal bool MatchValueResults => (this.Values || this.Data) && (this.Text.Length > 0 || !this.MatchKeyResults);

    internal bool MatchNoteResults => this.Notes && this.Text.Length > 0;
}

public enum SearchActivity
{
    None,
    Edited,
    Inspected,
    Either
}
