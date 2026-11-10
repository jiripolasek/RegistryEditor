// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed partial class MainWindowViewModel
{
    private Task RegistryHistoryChangedAsync(BrowserViewModel source, IReadOnlyList<JournalEntry> changes)
    {
        this._journalHistory?.RegistryActivityCompleted();
        var computers = changes.SelectMany(entry => entry.Before).Select(state => state.Scope.Location.Machine)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.WhenAll(
            this.Tabs.OfType<BrowserTab>().Select(tab => tab.Browser)
                .Where(browser => browser != source && browser.HasSnapshot && browser.CanPrompt &&
                                  computers.Contains(browser.Location.Machine))
                .Select(browser => browser.RefreshAfterHistoryAsync(changes)));
    }
}
