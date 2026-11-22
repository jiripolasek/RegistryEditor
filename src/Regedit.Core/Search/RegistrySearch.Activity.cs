// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Search;

public sealed record RegistryValueInspection(RegistryLocation Location, string ValueName, DateTimeOffset Time);

public static partial class RegistrySearch
{
    private static Dictionary<RegistryLocation, Dictionary<string, SearchActivity>> GetActivityCandidates(
        RegistryService registry, SavedSearch search, IEnumerable<RegistryValueInspection>? inspections, CancellationToken token,
        out int skippedJournalEntries)
    {
        var candidates = new Dictionary<RegistryLocation, Dictionary<string, SearchActivity>>(LocationComparer);
        var paths = new Dictionary<RegistryLocation, string?>(LocationComparer);
        var destinations = new Dictionary<string, RegistryLocation>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        skippedJournalEntries = 0;

        if (search.Options.Activity is SearchActivity.Edited or SearchActivity.Either)
        {
            var history = registry.Journal?.ReadHistory(token) ?? throw new InvalidOperationException("The change journal is unavailable.");
            skippedJournalEntries = history.DamagedFiles.Length;
            foreach (var entry in history.Entries.Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (!InWindow(entry.Time) || !entry.Before.Any(state => search.Roots.Any(root =>
                        Project(state.Scope.Location, root) is not null || Project(root, state.Scope.Location) is not null)))
                {
                    continue;
                }

                foreach (var difference in entry.GetDifferences(token))
                {
                    token.ThrowIfCancellationRequested();
                    if ((difference.After ?? difference.Before) is { } value)
                    {
                        Add(difference.Location, value.Name, SearchActivity.Edited);
                    }
                }
            }
        }

        if (search.Options.Activity is SearchActivity.Inspected or SearchActivity.Either)
        {
            foreach (var inspection in inspections ?? throw new InvalidOperationException("Inspection history is unavailable."))
            {
                token.ThrowIfCancellationRequested();
                if (InWindow(inspection.Time))
                {
                    Add(inspection.Location, inspection.ValueName, SearchActivity.Inspected);
                }
            }
        }

        return candidates;

        bool InWindow(DateTimeOffset time) => search.Options.ActivityWithin is not { } within || now - time <= within;

        void Add(RegistryLocation location, string name, SearchActivity activity)
        {
            var target = search.Roots.Any(location.IsAtOrBelow) ? location
                : search.Roots.Select(root => Project(location, root)).FirstOrDefault(result => result is not null);
            if (target is null)
            {
                return;
            }

            if (BackingPath(location) is { } backing)
            {
                var identity = $"{location.View}:{backing}";
                destinations.TryAdd(identity, target);
                target = destinations[identity];
            }

            if (!candidates.TryGetValue(target, out var values))
            {
                values = new Dictionary<string, SearchActivity>(StringComparer.OrdinalIgnoreCase);
                candidates.Add(target, values);
            }

            if (!values.TryAdd(name, activity) && values[name] != activity)
            {
                values[name] = SearchActivity.Either;
            }
        }

        RegistryLocation? Project(RegistryLocation location, RegistryLocation root)
        {
            if (location.IsAtOrBelow(root))
            {
                return location;
            }

            if (location.View != root.View || BackingPath(location) is not { } child || BackingPath(root) is not { } parent
                || !RegistryKeyIdentity.AtOrBelow(child, parent))
            {
                return null;
            }

            return child.Length == parent.Length ? root
                : root with { SubKey = root.SubKey.Length == 0 ? child[(parent.Length + 1)..] : root.SubKey + child[parent.Length..] };
        }

        string? BackingPath(RegistryLocation location)
        {
            if (!RegistryKeyIdentity.IsLocal(location.Machine))
            {
                return null;
            }

            if (!paths.TryGetValue(location, out var path))
            {
                try
                {
                    // A merged hive may represent more than one backing key.
                    path = RegistryKeyIdentity.Paths(location) is [var single] ? single : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
                {
                    path = null;
                }

                paths.Add(location, path);
            }

            return path;
        }
    }

    private static string ActivityLabel(SearchActivity activity) => activity switch
    {
        SearchActivity.Edited => "Edited",
        SearchActivity.Inspected => "Inspected",
        _ => "Edited and inspected"
    };
}
