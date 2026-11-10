// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Services;

internal sealed class RegistryHistoryBroadcaster
{
    internal event Func<BrowserViewModel, IReadOnlyList<JournalEntry>, Task>? Changed;

    internal async Task PublishAsync(BrowserViewModel source, IReadOnlyList<JournalEntry> changes)
    {
        if (this.Changed is not { } subscribers)
        {
            return;
        }

        await Task.WhenAll(
            subscribers.GetInvocationList().Cast<Func<BrowserViewModel, IReadOnlyList<JournalEntry>, Task>>()
                .Select(async subscriber => await subscriber(source, changes)));
    }
}
