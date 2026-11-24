// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Search;

public static partial class RegistrySearch
{
    private static readonly EqualityComparer<RegistryLocation> LocationComparer = EqualityComparer<RegistryLocation>.Create(
        (left, right) => left?.IsSameLocation(right) ?? right is null,
        location => HashCode.Combine(location.Hive, location.View,
            StringComparer.OrdinalIgnoreCase.GetHashCode(location.Machine ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(location.SubKey)));

    internal static string ValueSearchText(RegistryValue value)
    {
        return value.Type switch
        {
            RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.Link or RegistryValueType.MultiString =>
                Encoding.Unicode.GetString(value.Data).TrimEnd('\0'),
            RegistryValueType.DWord or RegistryValueType.DWordBigEndian when value.Data.Length == 4 => value.DisplayData,
            RegistryValueType.QWord when value.Data.Length == 8 => value.DisplayData,
            _ => Convert.ToHexString(value.Data)
        };
    }

    public static RegistryValueType[]? ParseTypes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Split(',', StringSplitOptions.TrimEntries).Select(part =>
        {
            if (uint.TryParse(part, out var number))
            {
                return (RegistryValueType)number;
            }

            foreach (var type in Enum.GetValues<RegistryValueType>())
            {
                if (new RegistryValue(string.Empty, type, []).TypeName.Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }

            throw new ArgumentException(
                $"Unknown value type: {part}. Use REG_SZ, REG_DWORD, or a numeric type code; separate types with commas.");
        }).Distinct().ToArray();
    }

    public static void Validate(SavedSearch search)
    {
        if (search is null
            || string.IsNullOrWhiteSpace(search.Name)
            || search.Roots is null
            || search.Roots.Length == 0
            || search.Options is null)
        {
            throw new ArgumentException("A saved search needs a name, options, and a scope.");
        }

        var options = search.Options;
        if (!Enum.IsDefined(options.Activity) || options.Text is null
            || options.ActivityWithin is { } within && (options.Activity == SearchActivity.None
                                                         || within.Ticks <= 0 || within.Ticks % TimeSpan.TicksPerHour != 0))
        {
            throw new ArgumentException("Choose a valid activity and time window.");
        }

        options.KeyModified?.Validate();
        if (options.Activity == SearchActivity.None && (options.Text.Length == 0 && options.KeyModified is null
                                                        || !(options.MatchKeyResults || options.MatchValueResults || options.MatchNoteResults)))
        {
            throw new ArgumentException("Enter search text or choose a filter, then select where to look for matches.");
        }

        if (options.Activity != SearchActivity.None && options.Notes)
        {
            throw new ArgumentException("Activity filters match values only. Choose value names or value data.");
        }

        if (options.Activity != SearchActivity.None && options.Text.Length > 0 && !(options.Values || options.Data))
        {
            throw new ArgumentException("Choose value names or value data to match text in an activity search.");
        }

        foreach (var root in search.Roots)
        {
            RegistryWorkspace.ValidateLocation(root);
        }
    }

    public static RegistrySearchResults FindAll(
        RegistryService registry,
        SavedSearch search,
        CancellationToken token,
        int limit = 100000,
        Action<RegistrySearchMatch[]>? progress = null)
    {
        return FindAll(registry, search, token, inspections: null, limit, progress);
    }

    public static RegistrySearchResults FindAll(
        RegistryService registry,
        SavedSearch search,
        CancellationToken token,
        IEnumerable<RegistryValueInspection>? inspections,
        int limit = 100000,
        Action<RegistrySearchMatch[]>? progress = null,
        IReadOnlyList<KeyNote>? notes = null)
    {
        Validate(search);
        token.ThrowIfCancellationRequested();
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var matches = new List<RegistrySearchMatch>();
        var pending = new List<RegistrySearchMatch>();
        var published = Stopwatch.StartNew();

        void Publish(bool final = false)
        {
            if (progress is null || pending.Count == 0 || !final && pending.Count < 128 && published.ElapsedMilliseconds < 100)
            {
                return;
            }

            progress(pending.ToArray());
            pending.Clear();
            published.Restart();
        }

        void Add(RegistrySearchMatch match)
        {
            matches.Add(match);
            if (progress is not null)
            {
                pending.Add(match);
                Publish(matches.Count == 1);
            }
        }

        var skipped = 0;
        var skippedJournalEntries = 0;
        var skippedTimestamps = new HashSet<RegistryLocation>();
        var options = search.Options;
        var textless = options.Text.Length == 0;
        var searchData = !textless && options.Data;
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var now = DateTimeOffset.UtcNow;
        var localZone = TimeZoneInfo.Local;
        var modified = options.KeyModified;

        bool MatchesDate(RegistryLocation location, DateTimeOffset? time)
        {
            if (modified is null)
            {
                return true;
            }

            if (time is { } timestamp)
            {
                return modified.Matches(timestamp, now, localZone);
            }

            skippedTimestamps.Add(location);
            return false;
        }

        bool Matches(string text)
        {
            return search.Options.Whole ? text.Equals(search.Options.Text, comparison) : text.Contains(search.Options.Text, comparison);
        }

        string Excerpt(string text)
        {
            var start = Math.Max(0, text.IndexOf(search.Options.Text, comparison) - 60);
            var length = Math.Min(240, text.Length - start);
            return (start > 0 ? "…" : string.Empty) + text.Substring(start, length) + (start + length < text.Length ? "…" : string.Empty);
        }

        RegistrySearchResults Finish(bool truncated = false)
        {
            Publish(true);
            return new RegistrySearchResults(matches.ToArray(), skipped, truncated)
            {
                SkippedJournalEntries = skippedJournalEntries,
                SkippedUnknownTimestamps = skippedTimestamps.Count
            };
        }

        RegistryKey? OpenKey(RegistryLocation location, bool ignoreMissing = false)
        {
            try
            {
                return registry.Open(location);
            }
            catch (FileNotFoundException) when (ignoreMissing)
            {
                return null;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                skipped++;
                return null;
            }
        }

        bool AddKey(RegistryLocation location, DateTimeOffset? lastWriteTime, bool nameMatches, string? note)
        {
            if (matches.Count == limit)
            {
                return false;
            }

            var fields = note is null ? "Key name" : nameMatches ? "Key name and note" : "Key note";
            var text = note ?? location.Name;
            Add(new RegistrySearchMatch(location, null, null, fields, textless ? text : Excerpt(text))
            {
                KeyLastModifiedUtc = lastWriteTime
            });
            return true;
        }

        bool FindValues(RegistryLocation location, RegistryKey key, Dictionary<string, SearchActivity>? activities = null,
            List<KeyNote>? exactAtKey = null, List<KeyNote>? rulesAtKey = null)
        {
            IEnumerable<KeyNote>? valueNotes = exactAtKey is null ? rulesAtKey :
                rulesAtKey is null ? exactAtKey : exactAtKey.Concat(rulesAtKey);
            var notesByName = valueNotes?.ToLookup(note => note.ValueName!, StringComparer.OrdinalIgnoreCase);
            var filterTypes = options.Types is { Length: > 0 };
            DateTimeOffset? lastWriteTime;
            string[] names;
            try
            {
                lastWriteTime = modified is null ? null : RegistryService.ReadLastWriteTime(key);
                if (!MatchesDate(location, lastWriteTime))
                {
                    return true;
                }

                names = activities is null && !options.MatchValueResults && notesByName is not null
                    ? notesByName.Select(group => group.Key).ToArray()
                    : key.GetValueNames();
                if (activities is null)
                {
                    if (options.MatchValueResults && exactAtKey is not null)
                    {
                        names = names.Union(exactAtKey.Select(note => note.ValueName!), StringComparer.OrdinalIgnoreCase).ToArray();
                    }

                    Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                skipped++;
                return true;
            }

            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                var activity = SearchActivity.None;
                if (activities is not null && !activities.TryGetValue(name, out activity))
                {
                    continue;
                }

                var nameMatch = !textless && options.Values && Matches(name);
                var noteCandidates = notesByName?[name];
                if (!textless && !nameMatch && !searchData && notesByName?.Contains(name) != true)
                {
                    continue;
                }

                var exactNote = noteCandidates?.FirstOrDefault(candidate => candidate.MatchRule is null);
                var canMatchAbsentValue = exactNote is not null && !filterTypes;
                var canStopBeforeReading = !searchData && matches.Count == limit && (textless || nameMatch);
                RegistryValue? value;
                try
                {
                    if (filterTypes || canStopBeforeReading)
                    {
                        var type = RegistryService.ReadValueType(key, name);
                        if (type is { } knownType ? !TypeAllowed(knownType) : !canMatchAbsentValue)
                        {
                            continue;
                        }
                    }

                    if (canStopBeforeReading)
                    {
                        return false;
                    }

                    value = RegistryService.ReadValue(key, name);
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    skipped++;
                    return true;
                }

                token.ThrowIfCancellationRequested();
                if (value is null ? !canMatchAbsentValue : !TypeAllowed(value.Type))
                {
                    continue;
                }

                nameMatch = value is not null && nameMatch;
                var data = searchData && value is not null ? ValueSearchText(value) : string.Empty;
                var dataMatch = searchData && value is not null && Matches(data);
                var note = exactNote ?? (value is null ? null :
                    noteCandidates?.FirstOrDefault(candidate => candidate.MatchesValue(location, name, value)));
                if (!textless && !nameMatch && !dataMatch && note is null)
                {
                    continue;
                }

                if (matches.Count == limit)
                {
                    return false;
                }

                var fields = textless ? "Value" : nameMatch && dataMatch ? "Value name and data" : nameMatch ? "Value name" : "Data";
                if (note is not null)
                {
                    fields = nameMatch && dataMatch ? "Value name, data, and note" :
                        nameMatch || dataMatch ? fields + " and note" : "Value note";
                }

                if (activity != SearchActivity.None)
                {
                    fields = textless ? ActivityLabel(activity) : $"{ActivityLabel(activity)} · {fields}";
                }

                var excerpt = textless ? value!.TypeName : Excerpt(note?.Text ?? (dataMatch ? data : name));
                Add(new RegistrySearchMatch(location, name, value, fields, excerpt)
                {
                    KeyLastModifiedUtc = lastWriteTime
                });
            }

            return true;
        }

        bool TypeAllowed(RegistryValueType type) => options.Types is not { Length: > 0 } types || types.Contains(type);

        if (options.Activity != SearchActivity.None)
        {
            var candidates = GetActivityCandidates(registry, search, inspections, token, out skippedJournalEntries);
            foreach (var (location, values) in candidates)
            {
                token.ThrowIfCancellationRequested();
                using var key = OpenKey(location, ignoreMissing: true);
                if (key is not null && !FindValues(location, key, values))
                {
                    return Finish(truncated: true);
                }
            }

            return Finish();
        }

        var keyNotes = new Dictionary<RegistryLocation, string>(LocationComparer);
        var exactNotes = new Dictionary<RegistryLocation, List<KeyNote>>(LocationComparer);
        var ruleNotes = new Dictionary<RegistryLocation, List<KeyNote>>(LocationComparer);
        var ruleRoots = new HashSet<RegistryLocation>(LocationComparer);
        var notesOnly = options.MatchNoteResults && !options.MatchKeyResults && !options.MatchValueResults;
        if (options.MatchNoteResults)
        {
            foreach (var note in notes ?? throw new InvalidOperationException("Notes are unavailable."))
            {
                token.ThrowIfCancellationRequested();
                if (!Matches(note.Text))
                {
                    continue;
                }

                if (note.MatchRule is not null)
                {
                    foreach (var root in search.Roots)
                    {
                        if (note.Location.IsAtOrBelow(root))
                        {
                            ruleRoots.Add(note.Location);
                        }
                        else if (root.IsAtOrBelow(note.Location))
                        {
                            ruleRoots.Add(root);
                        }
                    }

                    AddValueNote(ruleNotes, note);
                }
                else if (search.Roots.Any(note.Location.IsAtOrBelow))
                {
                    if (note.ValueName is null)
                    {
                        keyNotes.TryAdd(note.Location, note.Text);
                    }
                    else
                    {
                        AddValueNote(exactNotes, note);
                    }
                }
            }
        }

        static void AddValueNote(Dictionary<RegistryLocation, List<KeyNote>> index, KeyNote note)
        {
            if (!index.TryGetValue(note.Location, out var list))
            {
                index.Add(note.Location, list = []);
            }

            list.Add(note);
        }

        List<KeyNote>? RulesFor(RegistryLocation location)
        {
            if (ruleNotes.Count == 0)
            {
                return null;
            }

            List<KeyNote>? applicable = null;
            for (RegistryLocation? ancestor = location; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ruleNotes.TryGetValue(ancestor, out var rules))
                {
                    (applicable ??= []).AddRange(rules);
                }
            }

            return applicable;
        }

        bool CoveredByRuleRoot(RegistryLocation? location)
        {
            if (ruleRoots.Count != 0)
            {
                for (var parent = location; parent is not null; parent = parent.Parent)
                {
                    if (ruleRoots.Contains(parent))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Rule roots traverse whole subtrees; exact notes need only their own keys.
        var roots = notesOnly
            ? keyNotes.Keys.Union(exactNotes.Keys, LocationComparer).Where(location => !CoveredByRuleRoot(location))
                .Concat(ruleRoots.Where(location => !CoveredByRuleRoot(location.Parent)))
            : search.Roots;
        var stack = new Stack<RegistryLocation>(roots.Reverse());
        while (stack.TryPop(out var location))
        {
            token.ThrowIfCancellationRequested();

            var nameMatches = options.MatchKeyResults && (textless || Matches(location.Name));
            var note = keyNotes.GetValueOrDefault(location);
            var keyMatches = nameMatches || note is not null;
            var rules = RulesFor(location);
            var exact = exactNotes.GetValueOrDefault(location);
            var hasValueNotes = exact is not null || rules is not null;
            if (keyMatches && modified is not null)
            {
                DateTimeOffset? ownTimestamp;
                try
                {
                    ownTimestamp = registry.ReadKeyDetails(location, includeLink: false).LastWriteTimeUtc;
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    ownTimestamp = null;
                }

                if (MatchesDate(location, ownTimestamp) && !AddKey(location, ownTimestamp, nameMatches, note))
                {
                    return Finish(truncated: true);
                }

                if (notesOnly && !hasValueNotes)
                {
                    continue;
                }
            }

            token.ThrowIfCancellationRequested();
            using var key = OpenKey(location, ignoreMissing: notesOnly);
            if (key is null)
            {
                continue;
            }

            if (!notesOnly || rules is not null)
            {
                string[] children;
                try
                {
                    children = key.GetSubKeyNames();
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    skipped++;
                    continue;
                }

                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                for (var index = children.Length - 1; index >= 0; index--)
                {
                    stack.Push(location.Child(children[index]));
                }
            }

            if (keyMatches && modified is null && !AddKey(location, null, nameMatches, note)
                || (options.MatchValueResults || hasValueNotes) && !FindValues(location, key, exactAtKey: exact, rulesAtKey: rules))
            {
                return Finish(truncated: true);
            }

            Publish();
        }

        return Finish();
    }

    private static bool IsReadFailure(Exception ex) => ex is UnauthorizedAccessException or SecurityException or IOException or Win32Exception;
}
