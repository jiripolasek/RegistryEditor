// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;

namespace JPSoftworks.Regedit.Models.Tools;

public sealed record JournalOperationChoice(JournalOperation? Operation, string Label);
