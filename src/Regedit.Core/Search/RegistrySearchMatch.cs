// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Search;

public sealed record RegistrySearchMatch(
    RegistryLocation Location,
    string? ValueName,
    RegistryValue? Value,
    string Fields,
    string MatchedText)
{
    public DateTimeOffset? KeyLastModifiedUtc { get; init; }
}
