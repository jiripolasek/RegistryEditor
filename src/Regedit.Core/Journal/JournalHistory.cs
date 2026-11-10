// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Journal;

public sealed record JournalHistory(JournalEntry[] Entries, string[] DamagedFiles);
