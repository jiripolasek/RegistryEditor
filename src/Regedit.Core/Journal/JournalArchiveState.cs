// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Journal;

public sealed record JournalArchiveState(IReadOnlySet<Guid> ArchivedIds, string[] DamagedFiles);
