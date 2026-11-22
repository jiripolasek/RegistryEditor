// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Registry;


namespace JPSoftworks.Regedit.Models.Tools;

public sealed class InvestigationRow
{
    private string? _before;

    private string? _tableSummary;

    public string Kind { get; }

    public string Title { get; }

    public string Path { get; }

    public string Subtitle { get; }

    public string PrimaryText => this.Source switch
    {
        JournalEntry entry => JournalTarget(entry, true, this.Path),
        WatchObservation observation => WatchTarget(observation, true),
        _ => this.Title
    };

    public string SecondaryText => this.Source switch
    {
        JournalEntry entry => JournalSummary(entry),
        WatchObservation observation => WatchSummary(observation),
        _ => this.Path
    };

    public string PrimaryToolTip => this.Source switch
    {
        JournalEntry entry => JournalTarget(entry, false, this.Path),
        WatchObservation observation => WatchTarget(observation, false),
        _ => this.Title
    };

    public string SecondaryToolTip => this.Source switch
    {
        JournalEntry entry => JournalTarget(entry, false, this.Path) + " · " + JournalSummary(entry),
        WatchObservation observation => WatchTarget(observation, false) + " · " + WatchSummary(observation),
        RegistrySearchMatch { KeyLastModifiedUtc: { } modified } => $"{this.Path} · Key last modified {modified.ToLocalTime():G}",
        _ => this.Path
    };

    public string TimeToolTip => this.Source switch
    {
        JournalEntry entry => entry.Time.ToLocalTime().ToString("O"),
        WatchObservation observation => observation.Time.ToLocalTime().ToString("O"),
        _ => this.Time
    };

    public string Before => this._before ??= this.Source is RegistrySearchMatch match
        ? Detail(match.Value, "Key name: " + match.Location.Name)
        : string.Empty;

    public string After { get; }

    public object Source { get; }

    public string Time { get; }

    public string Status { get; }

    public string TableSubject => this.Source switch
    {
        JournalEntry entry when entry.Before.FirstOrDefault()?.Scope.ValueNames is [var name] => name.Length == 0 ? "(Default)" : name,
        WatchObservation { Change.Before: null, Change.After: null } observation => observation.Change.Location.Name,
        WatchObservation observation => observation.Change.Name.Length == 0 ? "(Default)" : observation.Change.Name,
        _ => this.Title
    };

    public string TableSummary => this._tableSummary ??= Shorten(
        (this.Source switch
        {
            RegistrySearchMatch match => match.Value?.DisplayPreview ?? "Key",
            JournalEntry entry => JournalSummary(entry),
            WatchObservation observation => WatchSummary(observation),
            _ => this.Subtitle
        }).ReplaceLineEndings(" "), 512);

    public RegistryEntry? MatchEntry { get; }

    public bool HasToolIcon => this.MatchEntry is null;

    public bool HasNavigationIcon => this.MatchEntry?.HasNavigationIcon == true;

    public bool HasStringIcon => this.MatchEntry?.HasStringIcon == true;

    public bool HasLinkIcon => this.MatchEntry?.HasLinkIcon == true;

    public bool HasBinaryIcon => this.MatchEntry?.HasBinaryIcon == true;

    public bool HasDataIcon => this.MatchEntry?.HasDataIcon == true;

    public string Glyph => this.MatchEntry is { } match ? match.Glyph :
        this.Source is JournalEntry entry ? ToolPresentation.JournalGlyph(entry.OperationKind) : ToolPresentation.Glyph(this.Kind);

    public string IconFontFamily => this.MatchEntry?.IconFontFamily
                                    ?? (this.Kind is "Note" or "Notes"
                                        ? ToolPresentation.FluentSystemIconsFontFamily
                                        : "Segoe Fluent Icons, Segoe MDL2 Assets");

    public string IconStyleKey => this.Source is JournalEntry entry ? ToolPresentation.JournalIconStyleKey(entry) : "ToolIconStyle";

    public bool HasSubtitle => !string.IsNullOrEmpty(this.Subtitle);

    public string AccessibleName => this.Source switch
    {
        JournalEntry entry => this.Title
                              + ", "
                              + JournalTarget(entry, false, this.Path)
                              + ", "
                              + JournalSummary(entry)
                              + ", "
                              + this.Status,
        WatchObservation observation => this.Title
                                        + ", "
                                        + WatchTarget(observation, false)
                                        + ", "
                                        + WatchSummary(observation)
                                        + ", "
                                        + this.Status,
        _ => $"{this.Title}, {this.Path}, {this.Status}"
    };

    public InvestigationRow(
        string kind,
        string title,
        string path,
        string subtitle,
        string? before,
        string after,
        object source,
        string time = "",
        string? status = null)
    {
        this.Kind = kind;
        this.Title = title;
        this.Path = path;
        this.Subtitle = source is RegistrySearchMatch ? Shorten(subtitle, 512) : subtitle;
        this._before = before;
        this.After = source is RegistrySearchMatch ? Shorten(after, 512) : after;
        this.Source = source;
        this.MatchEntry = source is RegistrySearchMatch match
            ? new RegistryEntry(match.Value is null ? match.Location : null, match.Value) { SourceLocation = match.Location }
            : null;
        this.Time = time;
        this.Status = status ?? subtitle;
    }

    internal InvestigationRow(RegistrySearchMatch match)
        : this(
            match.Value?.TypeName ?? "Key", match.Value?.DisplayName ?? match.Location.Name, match.Location.Address,
            (match.KeyLastModifiedUtc is { } modified ? $"Key last modified {modified.ToLocalTime():G} · " : string.Empty)
            + match.Fields + " · " + match.MatchedText, null, match.MatchedText, match)
    {
    }

    public override string ToString()
    {
        return this.AccessibleName;
    }

    public static InvestigationRow Change(RegistryDifference change, DateTimeOffset? time = null)
    {
        return new InvestigationRow(
            change.Change, change.Name, change.Location.Address,
            (time is null ? string.Empty : $"{time.Value.ToLocalTime():T} · ") + change.Types,
            change.Before is null ? change.BeforeText : Detail(change.Before),
            change.After is null ? change.AfterText : Detail(change.After), change);
    }

    internal static InvestigationRow Journal(JournalEntry entry, string status)
    {
        return new InvestigationRow(
            entry.Operation, entry.Operation, ToolPresentation.JournalTarget(entry), status == "Completed" ? string.Empty : status,
            string.Empty, string.Empty, entry, entry.Time.ToLocalTime().ToString("t"), status);
    }

    internal static InvestigationRow Observation(WatchObservation observation)
    {
        var change = observation.Change;
        return new InvestigationRow(
            change.Change, change.Change + ": " + change.Name, change.Location.Address, change.Types, string.Empty, string.Empty,
            observation, observation.Time.ToLocalTime().ToString("t"));
    }

    internal static InvestigationRow Note(NoteDraft note)
    {
        return new InvestigationRow(
            "Note", note.Location.Name, ToolPresentation.Address(note.Location), Shorten(note.Text.ReplaceLineEndings(" "), 256),
            string.Empty, string.Empty, note, note.SaveError is not null ? "Couldn’t save" : note.IsDirty ? "Unsaved" : string.Empty,
            note.SaveError is not null ? "Couldn’t save" : note.IsDirty ? "Unsaved changes" : "Saved");
    }

    public static string Detail(RegistryValue? value, string fallback = "Absent")
    {
        return value is null
            ? fallback
            : $"{value.TypeName} · {value.Data.Length:N0} bytes\n{value.DisplayPreview}\n\nRaw preview: {Convert.ToHexString(value.Data.AsSpan(0, Math.Min(128, value.Data.Length)))}{(value.Data.Length > 128 ? "…" : string.Empty)}";
    }

    private static string Shorten(string text, int limit)
    {
        return text.Length > limit ? text[..limit] + "…" : text;
    }

    private static string JournalTarget(JournalEntry entry, bool compact, string fallbackPath)
    {
        var state = entry.Before.FirstOrDefault() ?? entry.After?.FirstOrDefault();
        var location = state?.Scope.Location ?? entry.ActivityLocation;
        var names = state?.Scope.ValueNames ?? [];
        var path = location?.Address ?? (entry.Path.Length == 0 ? fallbackPath : entry.Path);
        var name = names.Length switch
        {
            0 => string.Empty,
            1 => names[0].Length == 0 ? "(Default)" : names[0],
            2 => string.Join(" → ", names.Select(value => value.Length == 0 ? "(Default)" : value)),
            _ => string.Empty
        };
        if (compact)
        {
            var subject = name.Length > 0 ? name : location?.Name ?? PathLeaf(path);
            subject = ShortenTail(subject, 72);
            var compactAddress = location is null
                ? CompactAddress(path, 150 - subject.Length - 3)
                : CompactAddress(location, 150 - subject.Length - 3);
            return subject.Length == 0 ? compactAddress : subject + " · " + compactAddress;
        }

        var fullAddress = path;
        var fullSubject = names.Length == 0
            ? string.Empty
            : " · " + string.Join(", ", names.Select(value => value.Length == 0 ? "(Default)" : value));
        var identity = location is null
            ? string.Empty
            : " · "
              + (location.Machine ?? Environment.MachineName)
              + " · "
              + (location.View == Microsoft.Win32.RegistryView.Registry32 ? "32-bit" : "64-bit");
        return fullAddress + fullSubject + identity;
    }

    private static string JournalSummary(JournalEntry entry)
    {
        if (entry.Error is not null
            || entry.After is null
            || entry.OperationKind is not (JournalOperation.CreateValue or JournalOperation.EditValue or JournalOperation.RenameValue
                or JournalOperation.DeleteValue))
        {
            return entry.Activity ?? entry.Operation;
        }

        var before = entry.Before.Length == 1 ? entry.Before[0] : null;
        var after = entry.After.Length == 1 ? entry.After[0] : null;
        if (before is null
            || after is null
            || before.Snapshot.Keys.Length != 1
            || after.Snapshot.Keys.Length != 1
            || before.Snapshot.Keys[0].Path.Length != 0
            || after.Snapshot.Keys[0].Path.Length != 0
            || !before.Scope.Location.IsSameLocation(after.Scope.Location)
            || before.Scope.ValueNames is not { Length: >= 1 and <= 2 } names
            || after.Scope.ValueNames is null
            || !names.SequenceEqual(after.Scope.ValueNames, StringComparer.OrdinalIgnoreCase))
        {
            return entry.Operation;
        }

        var beforeValues = before.Snapshot.Keys[0].Values;
        var afterValues = after.Snapshot.Keys[0].Values;
        if (beforeValues.Length > 1 || afterValues.Length > 1)
        {
            return entry.Operation;
        }

        var oldValue = beforeValues.FirstOrDefault();
        var newValue = afterValues.FirstOrDefault();
        if (oldValue is not null && !names.Contains(oldValue.Name, StringComparer.OrdinalIgnoreCase)
            || newValue is not null && !names.Contains(newValue.Name, StringComparer.OrdinalIgnoreCase))
        {
            return entry.Operation;
        }

        return entry.Operation + " · " + ValueSummary(oldValue, "Absent") + " → " + ValueSummary(newValue, "Absent");
    }

    private static string WatchTarget(WatchObservation observation, bool compact)
    {
        var change = observation.Change;
        var name = string.Empty;
        if (change.Before is not null || change.After is not null)
        {
            name = change.Name.Length == 0 ? "(Default)" : change.Name;
        }

        return compact
            ? CompactWatchTarget(change.Location, name)
            : change.Location.Address
              + (name.Length == 0 ? string.Empty : " · " + name)
              + " · "
              + (change.Location.Machine ?? Environment.MachineName)
              + " · "
              + (change.Location.View == Microsoft.Win32.RegistryView.Registry32 ? "32-bit" : "64-bit");
    }

    private static string CompactWatchTarget(RegistryLocation location, string name)
    {
        var subject = ShortenTail(name.Length == 0 ? location.Name : name, 72);
        var address = CompactAddress(location, 150 - subject.Length - 3);
        return subject + " · " + address;
    }

    private static string WatchSummary(WatchObservation observation)
    {
        var change = observation.Change;
        if (change.Before is null && change.After is null)
        {
            return change.Change;
        }

        var before = ValueSummary(change.Before, "Absent");
        var after = ValueSummary(change.After, "Absent");
        return Shorten(change.Change + " · " + before + " → " + after, 180);
    }

    private static string ValueSummary(RegistryValue? value, string fallback)
    {
        return value is null ? fallback : value.TypeName + " " + Shorten(value.DisplayPreview.Replace('\r', ' ').Replace('\n', ' '), 72);
    }

    private static string CompactAddress(RegistryLocation location, int limit)
    {
        var root = RegistryLocation.Roots.FirstOrDefault(item => item.Hive == location.Hive);
        var hive = root.Alias ?? location.RootName;
        var prefix = location.Machine is { Length: > 0 } machine ? "\\\\" + machine + "\\" : string.Empty;
        var target = prefix + hive;
        return CompactPath(target, location.SubKey, limit);
    }

    private static string CompactPath(string target, string subKey, int limit = 150)
    {
        if (subKey.Length == 0)
        {
            return ShortenTail(target, limit);
        }

        var available = limit - target.Length - 1;
        if (subKey.Length <= available)
        {
            return target + "\\" + subKey;
        }

        var tailLength = limit - target.Length - 2;
        if (tailLength > 0)
        {
            var start = Math.Max(0, subKey.Length - tailLength);
            var separator = subKey.IndexOf('\\', start);
            if (separator >= 0)
            {
                return target + "\\…\\" + subKey[(separator + 1)..];
            }

            return target + "\\…" + subKey[^tailLength..];
        }

        return ShortenTail(target, limit);
    }

    private static string CompactAddress(string address, int limit)
    {
        var prefix = string.Empty;
        if (address.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var machineEnd = address.IndexOf('\\', 2);
            if (machineEnd >= 0)
            {
                prefix = address[..(machineEnd + 1)];
                address = address[(machineEnd + 1)..];
            }
        }

        foreach (var root in RegistryLocation.Roots)
        {
            if (address.Equals(root.Name, StringComparison.OrdinalIgnoreCase))
            {
                return ShortenTail(prefix + root.Alias, limit);
            }

            if (address.StartsWith(root.Name + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return CompactPath(prefix + root.Alias, address[(root.Name.Length + 1)..], limit);
            }
        }

        return ShortenTail(prefix + address, limit);
    }

    private static string ShortenTail(string text, int limit)
    {
        return text.Length <= limit ? text : "…" + text[^Math.Max(1, limit - 1)..];
    }

    private static string PathLeaf(string address)
    {
        var end = address.Length - 1;
        while (end >= 0 && address[end] == '\\')
        {
            end--;
        }

        if (end < 0)
        {
            return string.Empty;
        }

        var start = address.LastIndexOf('\\', end);
        return address[(start + 1)..(end + 1)];
    }
}
