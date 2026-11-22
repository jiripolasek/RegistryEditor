// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Workspaces;


namespace JPSoftworks.Regedit.Models.Tools;

internal static class ToolPresentation
{
    internal const string FluentSystemIconsFontFamily = "/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    internal static readonly ToolChoice[] Tools =
    [
        new(ToolKind.Favorites, "Favorites", "Favorites", "\uE728"),
        new(ToolKind.Watches, "Watches", "Watches", "\uE7B3"),
        new(ToolKind.Journal, "Journal", "Change journal", "\uE81C"),
        new(ToolKind.Notes, "Notes", "Notes", "\uF56B")
    ];

    internal static ToolChoice Choice(this ToolKind kind)
    {
        return Tools.First(tool => tool.Kind == kind);
    }

    internal static string Address(RegistryLocation? location)
    {
        return location is null
            ? "Select a registry tab"
            : location.Address + (location.View == Microsoft.Win32.RegistryView.Registry32 ? " · 32-bit" : " · 64-bit");
    }

    internal static string SearchOptionsText(SearchOptions options)
    {
        var fields = new[]
        {
            options.Keys ? "Key names" : null,
            options.Values ? "Value names" : null,
            options.Data ? "Value data" : null,
            options.Notes ? "Key notes" : null
        };
        var types = options.Types is { Length: > 0 }
            ? string.Join(", ", options.Types.Select(type => new RegistryValue(string.Empty, type, []).TypeName))
            : "All";
        return "Search in: "
               + string.Join(", ", fields.Where(field => field is not null))
               + "\nMatch: "
               + (options.MatchCase ? "Case sensitive" : "Ignore case")
               + " · "
               + (options.Whole ? "Whole text" : "Substring")
               + "\nValue types: "
               + types
               + (options.Activity == SearchActivity.None ? string.Empty : "\nMy activity: " + options.Activity
                   + (options.ActivityWithin is { } within ? " · within " + SearchQueryText.FormatWindow(within) : string.Empty))
               + (options.KeyModified is { } modified ? "\nKey last modified: " + SearchQueryText.FormatKeyModified(modified) : string.Empty);
    }

    internal static IEnumerable<ToolComparison> WorkspaceComparisons(RegistryWorkspace workspace)
    {
        if (workspace.Tabs.Length > 0)
        {
            yield return new ToolComparison(string.Empty, string.Join("\n", workspace.Tabs.Select(Address)), string.Empty, "Tabs");
        }

        if (workspace.Searches.Length > 0)
        {
            yield return new ToolComparison(
                string.Empty, string.Join("\n", workspace.Searches.Select(search => search.Name)), string.Empty, "Saved searches");
        }

        if (workspace.Watches.Length > 0)
        {
            yield return new ToolComparison(
                string.Empty,
                string.Join(
                    "\n",
                    workspace.Watches.Select(watch =>
                        Address(watch.Location)
                        + (watch.ValueName is { } name ? " · Value: " + (name.Length == 0 ? "(Default)" : name) : " · Branch"))),
                string.Empty, "Watches");
        }
    }

    internal static (string Title, string Description) EmptyState(ToolKind tool, bool searching)
    {
        if (searching)
        {
            return ("No matches", "Try another name or registry path.");
        }

        return tool switch
        {
            ToolKind.Watches => ("No observations yet", "Start a watch to see changes to this branch."),
            ToolKind.Notes => ("No notes yet", "Select a registry key to write your first note."),
            _ => ("No recorded edits", "Edits made by this app appear here.")
        };
    }

    internal static string Glyph(string tool)
    {
        return tool switch
        {
            "Folder" => "\uE8B7",
            "Favorite" => "\uE734",
            "Favorites" => "\uE728",
            "Watches" => "\uE7B3",
            "Note" or "Notes" => "\uF56B",
            "Saved search" => "\uE721",
            "Workspace" => "\uE8F1",
            _ => "\uE81C"
        };
    }

    internal static string JournalGlyph(JournalOperation operation)
    {
        return operation switch
        {
            JournalOperation.CreateKey or JournalOperation.CopyKey => "\uE8B7",
            JournalOperation.RenameKey or JournalOperation.RenameValue => "\uE8AC",
            JournalOperation.DeleteKey or JournalOperation.DeleteValue => "\uE74D",
            JournalOperation.CreateValue => "\uE710",
            JournalOperation.EditValue => "\uE70F",
            JournalOperation.Revert => "\uE7A7",
            JournalOperation.Activity => "\uE946",
            _ => "\uE81C"
        };
    }

    internal static string JournalIconStyleKey(JournalEntry entry)
    {
        return entry switch
        {
            { Error: not null } => "JournalFailedIconStyle",
            { After: null } => "JournalEditedIconStyle",
            { OperationKind: JournalOperation.CreateKey or JournalOperation.CopyKey or JournalOperation.CreateValue } =>
                "JournalCreatedIconStyle",
            { OperationKind: JournalOperation.EditValue } => "JournalEditedIconStyle",
            { OperationKind: JournalOperation.RenameKey or JournalOperation.RenameValue } => "JournalRenamedIconStyle",
            { OperationKind: JournalOperation.DeleteKey or JournalOperation.DeleteValue } => "JournalFailedIconStyle",
            { OperationKind: JournalOperation.Revert } => "JournalRevertedIconStyle",
            _ => "ToolIconStyle"
        };
    }

    internal static string JournalTarget(JournalEntry entry)
    {
        var names = entry.Before.SelectMany(state => state.Scope.ValueNames ?? []).Select(name => name.Length == 0 ? "(Default)" : name)
            .ToArray();
        return entry.Path + (names.Length == 0 ? string.Empty : " · " + string.Join(", ", names));
    }

    public static string DayLabel(DateTimeOffset time, DateTime today)
    {
        var day = time.LocalDateTime.Date;
        return day == today.Date ? "Today" : day == today.Date.AddDays(-1) ? "Yesterday" : day.ToString("D");
    }

    public static string JournalStatus(JournalEntry entry, bool reverted)
    {
        return entry.Status + (reverted ? " · Reverted" : entry.Reverts is not null ? " · Revert edit" : string.Empty);
    }

    public static ToolComparison Compare(RegistryDifference change)
    {
        return new ToolComparison(
            change.Location.Address + (change.Before is not null || change.After is not null ? "\n" + change.Name : string.Empty),
            ValuePreview(
                change.Before,
                change.Kind == RegistryChangeKind.KeyRemoved ? "Key exists" :
                change.Kind == RegistryChangeKind.KeyAdded ? "Key absent" : "Value absent"),
            ValuePreview(
                change.After,
                change.Kind == RegistryChangeKind.KeyAdded ? "Key exists" :
                change.Kind == RegistryChangeKind.KeyRemoved ? "Key absent" : "Value absent"), BeforeValue: change.Before,
            AfterValue: change.After);
    }

    public static string ValuePreview(RegistryValue? value, string fallback = "Value absent")
    {
        return value is null ? fallback : $"{value.DisplayName}\n{value.TypeName} · {value.Data.Length:N0} bytes\n{value.DisplayPreview}";
    }
}
