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

        bool FindValues(RegistryLocation location, RegistryKey key, Dictionary<string, SearchActivity>? activities = null)
        {
            DateTimeOffset? lastWriteTime;
            string[] names;
            try
            {
                lastWriteTime = modified is null ? null : RegistryService.ReadLastWriteTime(key);
                if (!MatchesDate(location, lastWriteTime))
                {
                    return true;
                }

                names = key.GetValueNames();
                if (activities is null)
                {
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
                if (!textless && !nameMatch && !searchData)
                {
                    continue;
                }

                RegistryValue? value;
                try
                {
                    if (options.Types is { Length: > 0 } || !searchData && matches.Count == limit)
                    {
                        var type = RegistryService.ReadValueType(key, name);
                        if (type is null || !TypeAllowed(type.Value))
                        {
                            continue;
                        }
                    }

                    if (!searchData && matches.Count == limit)
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
                if (value is null || !TypeAllowed(value.Type))
                {
                    continue;
                }

                var data = searchData ? ValueSearchText(value) : string.Empty;
                var dataMatch = searchData && Matches(data);
                if (!textless && !nameMatch && !dataMatch)
                {
                    continue;
                }

                if (matches.Count == limit)
                {
                    return false;
                }

                var fields = textless ? "Value" : nameMatch && dataMatch ? "Value name and data" : nameMatch ? "Value name" : "Data";
                if (activity != SearchActivity.None)
                {
                    fields = textless ? ActivityLabel(activity) : $"{ActivityLabel(activity)} · {fields}";
                }

                Add(new RegistrySearchMatch(location, name, value, fields, textless ? value.TypeName : Excerpt(dataMatch ? data : name))
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

        Dictionary<RegistryLocation, string>? matchingNotes = null;
        if (options.MatchNoteResults)
        {
            matchingNotes = new(LocationComparer);
            foreach (var note in notes ?? throw new InvalidOperationException("Key notes are unavailable."))
            {
                token.ThrowIfCancellationRequested();
                if (Matches(note.Text) && search.Roots.Any(note.Location.IsAtOrBelow))
                {
                    matchingNotes.TryAdd(note.Location, note.Text);
                }
            }
        }

        var notesOnly = matchingNotes is not null && !options.MatchKeyResults && !options.MatchValueResults;
        var stack = new Stack<RegistryLocation>(notesOnly ? matchingNotes!.Keys.Reverse() : search.Roots.Reverse());
        while (stack.TryPop(out var location))
        {
            token.ThrowIfCancellationRequested();
            var nameMatches = options.MatchKeyResults && (textless || Matches(location.Name));
            var note = matchingNotes?.GetValueOrDefault(location);
            var keyMatches = nameMatches || note is not null;
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

                if (notesOnly)
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

            if (!notesOnly)
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
                || options.MatchValueResults && !FindValues(location, key))
            {
                return Finish(truncated: true);
            }

            Publish();
        }

        return Finish();
    }

    private static bool IsReadFailure(Exception ex) => ex is UnauthorizedAccessException or SecurityException or IOException or Win32Exception;
}
