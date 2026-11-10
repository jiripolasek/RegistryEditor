// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Snapshots;

namespace JPSoftworks.Regedit.Core.Journal;

public sealed record JournalState(JournalScope Scope, RegistryBranchSnapshot Snapshot, JournalSecurity[] Security);
