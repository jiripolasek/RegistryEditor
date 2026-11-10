// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Snapshots;

namespace JPSoftworks.Regedit.Models.Tools;

public sealed record WatchObservation(RegistryDifference Change, DateTimeOffset Time);
