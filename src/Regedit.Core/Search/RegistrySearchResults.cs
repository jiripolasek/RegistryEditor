// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Search;

public sealed record RegistrySearchResults(RegistrySearchMatch[] Matches, int SkippedKeys, bool Truncated);
