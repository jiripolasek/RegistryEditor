// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Snapshots;


namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed class JournalDifferenceCache
{
    private readonly Dictionary<JournalEntry, IReadOnlyList<RegistryDifference>> _differences = new(ReferenceEqualityComparer.Instance);

    private HashSet<JournalEntry> _selected = new(ReferenceEqualityComparer.Instance);

    internal int Count => this._differences.Count;

    internal void SetEntries(IEnumerable<JournalEntry> entries)
    {
        this._selected = new HashSet<JournalEntry>(entries, ReferenceEqualityComparer.Instance);
        foreach (var entry in this._differences.Keys.Where(entry => !this._selected.Contains(entry)).ToArray())
        {
            this._differences.Remove(entry);
        }
    }

    internal IReadOnlyList<RegistryDifference> Get(JournalEntry entry)
    {
        if (!this._selected.Contains(entry))
        {
            return entry.Differences;
        }

        if (!this._differences.TryGetValue(entry, out var changes))
        {
            changes = entry.Differences;
            this._differences.Add(entry, changes);
        }

        return changes;
    }
}
