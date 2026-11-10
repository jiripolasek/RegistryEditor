// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json.Serialization;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Journal;

public sealed record JournalScope(RegistryLocation Location, string[]? ValueNames = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? KeyPaths { get; init; }
}
