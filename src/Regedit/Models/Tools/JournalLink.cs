// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Journal;

namespace JPSoftworks.Regedit.Models.Tools;

public sealed record JournalLink(string Label, string Description, JournalEntry Target, IRelayCommand<Guid> Command)
{
    public Guid EntryId => this.Target.Id;
}
