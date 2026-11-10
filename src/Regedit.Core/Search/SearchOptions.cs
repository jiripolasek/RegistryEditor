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
    RegistryValueType[]? Types = null);
