// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;

namespace JPSoftworks.Regedit.Core.Search;

public static class SearchQueryText
{
    [Flags]
    private enum MatchFields
    {
        None = 0,
        KeyNames = 1,
        ValueNames = 2,
        ValueData = 4,
        KeyNotes = 8,
        ValueFields = ValueNames | ValueData,
        Default = KeyNames | ValueFields
    }

    public static SearchOptions Parse(string query) => Parse(query, allowDraft: false, out _, out _);

    public static SearchOptions ParseDraft(string query, out string? error) => ParseDraft(query, out error, out _);

    public static SearchOptions ParseDraft(string query, out string? error, out bool hasMatchSelection)
        => Parse(query, allowDraft: true, out error, out hasMatchSelection);

    private static SearchOptions Parse(string query, bool allowDraft, out string? error, out bool hasMatchSelection)
    {
        error = null;
        var activity = SearchActivity.None;
        TimeSpan? within = null;
        KeyModifiedFilter? modified = null;
        MatchFields? matchFields = null;
        var text = new List<string>();
        var start = 0;
        var end = 0;
        while (end < query.Length)
        {
            if (char.IsWhiteSpace(query[end]))
            {
                end++;
                continue;
            }

            var wordStart = end;
            var quoted = false;
            do
            {
                quoted ^= query[end] == '"';
                end++;
            } while (end < query.Length && (quoted || !char.IsWhiteSpace(query[end])));

            var word = query.AsSpan(wordStart, end - wordStart);
            try
            {
                if (word.StartsWith("activity:", StringComparison.OrdinalIgnoreCase))
                {
                    if (activity != SearchActivity.None)
                    {
                        throw new ArgumentException("Use one activity filter per search.");
                    }

                    activity = word[9..].ToString().ToUpperInvariant() switch
                    {
                        "EDITED" => SearchActivity.Edited,
                        "INSPECTED" => SearchActivity.Inspected,
                        "ANY" or "EITHER" => SearchActivity.Either,
                        _ => throw new ArgumentException("Use activity:edited, activity:inspected, or activity:either.")
                    };
                }
                else if (word.StartsWith("within:", StringComparison.OrdinalIgnoreCase))
                {
                    if (within is not null)
                    {
                        throw new ArgumentException("Use one activity time window per search.");
                    }

                    within = ParseWindow(word[7..]);
                }
                else if (word.StartsWith("modified:", StringComparison.OrdinalIgnoreCase))
                {
                    if (modified is not null)
                    {
                        throw new ArgumentException("Use one Key last modified filter per search.");
                    }

                    modified = ParseKeyModified(word[9..]);
                }
                else if (word.StartsWith("match:", StringComparison.OrdinalIgnoreCase))
                {
                    if (matchFields is not null)
                    {
                        throw new ArgumentException("Use one match: field list per search.");
                    }

                    matchFields = ParseMatchFields(word[6..]);
                }
                else
                {
                    continue;
                }
            }
            catch (ArgumentException ex) when (allowDraft)
            {
                error ??= ex.Message;
            }

            AddText(query.AsSpan(start, wordStart - start));
            start = end;
        }

        string? validationError = null;
        if (activity == SearchActivity.None && within is not null)
        {
            validationError = "Use within: with an activity filter, for example activity:edited within:7d.";
        }
        else if (activity != SearchActivity.None && (matchFields.GetValueOrDefault() & (MatchFields.KeyNames | MatchFields.KeyNotes)) != 0)
        {
            validationError = "With an activity filter, choose match:names, match:data, or match:names,data.";
        }

        if (validationError is not null)
        {
            if (!allowDraft)
            {
                throw new ArgumentException(validationError);
            }

            error ??= validationError;
        }

        hasMatchSelection = matchFields is not null;
        if (start == 0)
        {
            var unquoted = Unquote(query);
            return new SearchOptions(ContainsFilterSyntax(unquoted) || unquoted.Length > 0 && string.IsNullOrWhiteSpace(unquoted)
                ? unquoted : query);
        }

        AddText(query.AsSpan(start));
        var fields = matchFields ?? DefaultMatchFields(activity);
        return new SearchOptions(
            string.Join(' ', text), Keys: fields.HasFlag(MatchFields.KeyNames), Values: fields.HasFlag(MatchFields.ValueNames),
            Data: fields.HasFlag(MatchFields.ValueData), Activity: activity,
            ActivityWithin: within, KeyModified: modified, Notes: fields.HasFlag(MatchFields.KeyNotes));

        void AddText(ReadOnlySpan<char> part)
        {
            if (!part.Trim().IsEmpty)
            {
                text.Add(Unquote(part.Trim().ToString()));
            }
        }
    }

    private static MatchFields DefaultMatchFields(SearchActivity activity)
        => activity == SearchActivity.None ? MatchFields.Default : MatchFields.ValueFields;

    private static MatchFields ParseMatchFields(ReadOnlySpan<char> text)
    {
        var fields = MatchFields.None;
        foreach (var field in text.ToString().Split(','))
        {
            fields |= field.ToUpperInvariant() switch
            {
                "KEYS" => MatchFields.KeyNames,
                "NAMES" => MatchFields.ValueNames,
                "DATA" => MatchFields.ValueData,
                "NOTES" => MatchFields.KeyNotes,
                _ => throw new ArgumentException("Choose fields from match:keys,names,data,notes. Use commas without spaces.")
            };
        }

        return fields;
    }

    private static KeyModifiedFilter ParseKeyModified(ReadOnlySpan<char> text)
    {
        var separator = text.IndexOf("..", StringComparison.Ordinal);
        var filter = separator < 0
            ? new KeyModifiedFilter(Within: ParseWindow(text))
            : new KeyModifiedFilter(From: ParseDate(text[..separator]), Through: ParseDate(text[(separator + 2)..]));
        filter.Validate();
        return filter;

        static DateOnly? ParseDate(ReadOnlySpan<char> date)
        {
            if (date.IsEmpty)
            {
                return null;
            }

            return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : throw new ArgumentException("Use YYYY-MM-DD dates, for example modified:2026-11-01..2026-11-22.");
        }
    }

    public static TimeSpan ParseWindow(ReadOnlySpan<char> text)
    {
        var hoursPerUnit = text.IsEmpty ? 0 : char.ToLowerInvariant(text[^1]) switch { 'h' => 1, 'd' => 24, _ => 0 };
        if (hoursPerUnit == 0 || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                              || count <= 0 || (long)count * hoursPerUnit > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerHour)
        {
            throw new ArgumentException("Enter a positive time window in hours or days, such as 12h or 7d.");
        }

        return TimeSpan.FromTicks((long)count * hoursPerUnit * TimeSpan.TicksPerHour);
    }

    public static string Format(SearchOptions options) => Format(options, quotePlainText: true);

    public static string Format(SearchOptions options, bool quotePlainText)
    {
        var text = options.Text;
        var matchFields = (options.Activity == SearchActivity.None && options.Keys ? MatchFields.KeyNames : MatchFields.None)
                          | (options.Values ? MatchFields.ValueNames : MatchFields.None)
                          | (options.Data ? MatchFields.ValueData : MatchFields.None)
                          | (options.Activity == SearchActivity.None && options.Notes ? MatchFields.KeyNotes : MatchFields.None);
        var hasMatchSelection = matchFields != MatchFields.None && matchFields != DefaultMatchFields(options.Activity);
        var hasFilters = options.Activity != SearchActivity.None || options.KeyModified is not null || hasMatchSelection;
        if (text.Length > 0 && (hasFilters || quotePlainText) && (ContainsFilterSyntax(text)
                               || hasFilters && (text.Contains('"') || text.AsSpan().Trim().Length != text.Length)
                               || string.IsNullOrWhiteSpace(text)))
        {
            text = '"' + text.Replace("\"", "\"\"") + '"';
        }

        if (options.Activity != SearchActivity.None)
        {
            text = Append(text, "activity:" + options.Activity.ToString().ToLowerInvariant());
            if (options.ActivityWithin is { } within)
            {
                text = Append(text, "within:" + FormatWindow(within));
            }
        }

        if (options.KeyModified is { } modified)
        {
            text = Append(text, "modified:" + FormatKeyModified(modified));
        }

        return hasMatchSelection ? Append(text, FormatMatchFields(matchFields)) : text;

        static string Append(string query, string token) => query.Length == 0 ? token : query + " " + token;
    }

    private static string FormatMatchFields(MatchFields fields)
    {
        var names = new List<string>(4);
        if (fields.HasFlag(MatchFields.KeyNames))
        {
            names.Add("keys");
        }

        if (fields.HasFlag(MatchFields.ValueNames))
        {
            names.Add("names");
        }

        if (fields.HasFlag(MatchFields.ValueData))
        {
            names.Add("data");
        }

        if (fields.HasFlag(MatchFields.KeyNotes))
        {
            names.Add("notes");
        }

        return "match:" + string.Join(',', names);
    }

    public static string FormatKeyModified(KeyModifiedFilter filter) => filter.Within is { } within
        ? within == TimeSpan.FromHours(24) ? "24h" : FormatWindow(within)
        : FormattableString.Invariant($"{filter.From:yyyy-MM-dd}..{filter.Through:yyyy-MM-dd}");

    public static string FormatWindow(TimeSpan window) => window.Ticks % TimeSpan.TicksPerDay == 0
        ? FormattableString.Invariant($"{window.Ticks / TimeSpan.TicksPerDay}d")
        : FormattableString.Invariant($"{window.Ticks / TimeSpan.TicksPerHour}h");

    private static bool ContainsFilterSyntax(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (index > 0 && !char.IsWhiteSpace(text[index - 1]) && text[index - 1] != '"')
            {
                continue;
            }

            var tail = text.AsSpan(index);
            if (tail.StartsWith("activity:", StringComparison.OrdinalIgnoreCase) || tail.StartsWith("within:", StringComparison.OrdinalIgnoreCase)
                || tail.StartsWith("modified:", StringComparison.OrdinalIgnoreCase) || tail.StartsWith("match:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        for (var index = 1; index < text.Length - 1; index++)
        {
            if (text[index] == '"' && (++index == text.Length - 1 || text[index] != '"'))
            {
                return text;
            }
        }

        return text[1..^1].Replace("\"\"", "\"");
    }
}
