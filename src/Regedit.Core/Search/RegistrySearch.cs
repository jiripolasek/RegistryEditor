// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;

namespace JPSoftworks.Regedit.Core.Search;

public static class RegistrySearch
{
    internal static string ValueSearchText(RegistryValue value)
    {
        return value.Type switch
        {
            RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.Link
                or RegistryValueType.MultiString => Encoding.Unicode.GetString(value.Data).TrimEnd('\0'),
            RegistryValueType.DWord or RegistryValueType.DWordBigEndian when value.Data.Length == 4 =>
                value.DisplayData,
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
        if (search is null || string.IsNullOrWhiteSpace(search.Name) || search.Roots is null ||
            search.Roots.Length == 0 || search.Options is null || string.IsNullOrEmpty(search.Options.Text) ||
            !(search.Options.Keys || search.Options.Values || search.Options.Data))
        {
            throw new ArgumentException(
                "A saved search needs a name, search text, scope, and at least one search field.");
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
        Validate(search);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var matches = new List<RegistrySearchMatch>();
        var pending = new List<RegistrySearchMatch>();
        var published = Stopwatch.StartNew();

        void Publish(bool final = false)
        {
            if (progress is null || pending.Count == 0 ||
                !final && pending.Count < 128 && published.ElapsedMilliseconds < 100)
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
        var comparison = search.Options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        bool Matches(string text)
        {
            return search.Options.Whole
                ? text.Equals(search.Options.Text, comparison)
                : text.Contains(search.Options.Text, comparison);
        }

        string Excerpt(string text)
        {
            var start = Math.Max(0, text.IndexOf(search.Options.Text, comparison) - 60);
            var length = Math.Min(240, text.Length - start);
            return (start > 0 ? "…" : string.Empty) + text.Substring(start, length) +
                   (start + length < text.Length ? "…" : string.Empty);
        }

        var stack = new Stack<RegistryLocation>(search.Roots.Reverse());
        while (stack.TryPop(out var location))
        {
            token.ThrowIfCancellationRequested();
            RegistrySnapshot snapshot;
            try
            {
                snapshot = registry.Read(location);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException
                                           or Win32Exception)
            {
                skipped++;
                continue;
            }

            if (search.Options.Keys && Matches(location.Name))
            {
                if (matches.Count == limit)
                {
                    Publish(true);
                    return new RegistrySearchResults(matches.ToArray(), skipped, true);
                }

                Add(new RegistrySearchMatch(location, null, null, "Key name", Excerpt(location.Name)));
            }

            foreach (var value in snapshot.Values)
            {
                token.ThrowIfCancellationRequested();
                if (search.Options.Types is { Length: > 0 } types && !types.Contains(value.Type))
                {
                    continue;
                }

                var nameMatch = search.Options.Values && Matches(value.Name);
                var data = search.Options.Data ? ValueSearchText(value) : string.Empty;
                var dataMatch = search.Options.Data && Matches(data);
                if (!nameMatch && !dataMatch)
                {
                    continue;
                }

                if (matches.Count == limit)
                {
                    Publish(true);
                    return new RegistrySearchResults(matches.ToArray(), skipped, true);
                }

                Add(
                    new RegistrySearchMatch(
                        location, value.Name, value,
                        nameMatch && dataMatch ? "Value name and data" : nameMatch ? "Value name" : "Data",
                        Excerpt(dataMatch ? data : value.Name)));
            }

            snapshot.PushChildren(stack, location);
            Publish();
        }

        Publish(true);
        return new RegistrySearchResults(matches.ToArray(), skipped, false);
    }
}
