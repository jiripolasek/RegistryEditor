// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Omnibox;

/// <summary>A prefix, its editor labels, and a suggestion provider that receives the unchanged query on the UI thread.</summary>
public sealed record OmniboxScope(
    string Keyword,
    string Label,
    string Placeholder,
    Func<string, IReadOnlyList<OmniboxItem>> Suggest,
    string? Alias = null)
{
    public string? IconGlyph { get; init; }

    public string? Footer { get; init; }

    public string EmptyMessage { get; init; } = "No results found";

    /// <summary>Initial result counts for named groups. Unlisted groups show all results.</summary>
    public IReadOnlyDictionary<string, int>? GroupPreviewCounts { get; init; }
}
