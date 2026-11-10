// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Search;

public sealed record SearchResult(SearchHit? Hit, int SkippedKeys);
