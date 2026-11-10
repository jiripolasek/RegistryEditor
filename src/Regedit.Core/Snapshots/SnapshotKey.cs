// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Snapshots;

public sealed record SnapshotKey(string Path, RegistryValue[] Values);
