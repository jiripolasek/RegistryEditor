// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Registry;

public sealed record RegistryKeyDetails(DateTimeOffset? LastWriteTimeUtc, int SubKeyCount, int ValueCount, string? LinkTarget);
