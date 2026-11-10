// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Search;

public sealed record SavedSearch(string Name, RegistryLocation[] Roots, SearchOptions Options);
