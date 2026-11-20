// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Shell;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ToolPresentationTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var today = new DateTime(2026, 11, 10);
        var time = new DateTimeOffset(today.AddHours(12));
        check(ToolPresentation.DayLabel(time, today) == "Today", "Journal groups entries by local day");
        check(ToolPresentation.DayLabel(time.AddDays(-1), today) == "Yesterday", "Journal names the previous local day");
        check(
            ToolPresentation.DayLabel(time.AddDays(-4), today) == today.AddDays(-4).ToString("D"),
            "Older journal groups use localized dates");
        var location = RegistryLocation.Parse("HKCU\\Software\\Test");

        var noteRow = InvestigationRow.Note(new NoteDraft(location, "Example"));
        var noteTab = new ToolTab(ToolKind.Notes);
        check(
            noteRow.Glyph == "\uF56B"
            && noteTab.Glyph == noteRow.Glyph
            && noteRow.IconFontFamily == ToolPresentation.FluentSystemIconsFontFamily
            && noteTab.IconFontFamily == noteRow.IconFontFamily, "Note rows and tabs pair the supplied note glyph with its bundled font");
        check(
            new ToolTab(ToolKind.Journal).IconFontFamily == "Segoe Fluent Icons, Segoe MDL2 Assets"
            && new InvestigationRow("Workspace", "Example", "", "", "", "", new object()).IconFontFamily
            == "Segoe Fluent Icons, Segoe MDL2 Assets", "Other tool icons retain their Segoe Fluent Icons font");
        var added = ToolPresentation.Compare(new RegistryDifference(location, RegistryChangeKind.KeyAdded));
        var removed = ToolPresentation.Compare(new RegistryDifference(location, RegistryChangeKind.KeyRemoved));
        check(
            added.Before == "Key absent" && added.After == "Key exists" && removed.Before == "Key exists" && removed.After == "Key absent",
            "Key comparisons distinguish existence from absent values");
        var value = new RegistryValue("Long value", RegistryValueType.String, Encoding.Unicode.GetBytes(new string('a', 5000) + "END\0"));
        var comparison = ToolPresentation.Compare(new RegistryDifference(location, RegistryChangeKind.ValueAdded, After: value));
        check(
            comparison.Before == "Value absent"
            && comparison.After.Contains(new string('a', 256))
            && !comparison.After.Contains(new string('a', 257))
            && !comparison.After.Contains(Convert.ToHexString(value.Data))
            && ReferenceEquals(comparison.AfterValue, value),
            "Comparison previews stay bounded while retaining the typed source for explicit inspection");
        var journalLocation = new RegistryLocation(Microsoft.Win32.RegistryHive.CurrentUser, new string('A', 360));
        var entry = new JournalEntry(Guid.NewGuid(), time, "Set value", [], [], ActivityLocation: journalLocation);
        (JournalOperation Operation, string Style)[] journalStyles =
        [
            (JournalOperation.CreateKey, "JournalCreatedIconStyle"),
            (JournalOperation.CopyKey, "JournalCreatedIconStyle"),
            (JournalOperation.CreateValue, "JournalCreatedIconStyle"),
            (JournalOperation.EditValue, "JournalEditedIconStyle"),
            (JournalOperation.RenameKey, "JournalRenamedIconStyle"),
            (JournalOperation.RenameValue, "JournalRenamedIconStyle"),
            (JournalOperation.DeleteKey, "JournalFailedIconStyle"),
            (JournalOperation.DeleteValue, "JournalFailedIconStyle"),
            (JournalOperation.Revert, "JournalRevertedIconStyle"),
            (JournalOperation.Activity, "ToolIconStyle"),
            (JournalOperation.Unknown, "ToolIconStyle"),
            ((JournalOperation)999, "ToolIconStyle")
        ];
        foreach (var (operation, style) in journalStyles)
        {
            var coloredEntry = entry with { Kind = operation };
            check(
                InvestigationRow.Journal(coloredEntry, "Completed").IconStyleKey == style,
                $"Journal {operation} uses its semantic icon style");
            check(
                ToolPresentation.JournalIconStyleKey(coloredEntry with { Error = "Write failed", After = null })
                == "JournalFailedIconStyle", $"Journal {operation} failures override the operation color");
        }

        check(
            ToolPresentation.JournalIconStyleKey(entry with { Kind = JournalOperation.CreateValue, After = null })
            == "JournalEditedIconStyle", "Incomplete Journal captures use the caution icon style");
        check(noteRow.IconStyleKey == "ToolIconStyle", "Other tool rows retain their neutral icon style");
        var longTarget = journalLocation.Address;
        var journalRow = new InvestigationRow("Edit value", "Edit value", longTarget, string.Empty, string.Empty, string.Empty, entry);
        check(
            journalRow.PrimaryText.Contains("HKCU\\", StringComparison.Ordinal)
            && journalRow.PrimaryText.Length <= 150
            && journalRow.SecondaryText == "Set value"
            && journalRow.PrimaryToolTip.Contains(longTarget, StringComparison.Ordinal),
            "Journal rows lead with a bounded abbreviated registry target and retain its full path");
        var watchTime = time.AddMinutes(1);
        var watchChange = new RegistryDifference(
            location, RegistryChangeKind.ValueChanged, new RegistryValue(string.Empty, RegistryValueType.Binary, [1]),
            new RegistryValue(string.Empty, RegistryValueType.Binary, [2]));
        var watchRow = new InvestigationRow(
            "Watch", "Value changed", location.Address, string.Empty, string.Empty, string.Empty,
            new WatchObservation(watchChange, watchTime));
        check(
            watchRow.PrimaryText.StartsWith("(Default) · HKCU\\", StringComparison.Ordinal)
            && watchRow.PrimaryText.Length < 150
            && watchRow.SecondaryText.Contains("Data changed · REG_BINARY 01 → REG_BINARY 02", StringComparison.Ordinal)
            && watchRow.AccessibleName.Contains(location.Address, StringComparison.Ordinal)
            && watchRow.AccessibleName.Contains("64-bit", StringComparison.Ordinal)
            && watchRow.TimeToolTip == watchTime.ToLocalTime().ToString("O"),
            "Watch rows lead with the registry target and bounded before/after preview while exposing the exact accessible target");
        var sharedPath = "Software\\" + new string('A', 350);
        var firstLongLocation = new RegistryLocation(
            Microsoft.Win32.RegistryHive.CurrentUser, sharedPath + "TailOne", View: Microsoft.Win32.RegistryView.Registry32);
        var secondLongLocation = firstLongLocation with { SubKey = sharedPath + "TailTwo" };
        var firstLongEntry = new JournalEntry(Guid.NewGuid(), time, "Edit value", [], [], ActivityLocation: firstLongLocation);
        var secondLongEntry = new JournalEntry(Guid.NewGuid(), time, "Edit value", [], [], ActivityLocation: secondLongLocation);
        var firstLongRow = new InvestigationRow(
            "Edit value", "Edit value", firstLongLocation.Address, string.Empty, string.Empty, string.Empty, firstLongEntry);
        var secondLongRow = new InvestigationRow(
            "Edit value", "Edit value", secondLongLocation.Address, string.Empty, string.Empty, string.Empty, secondLongEntry);
        check(
            firstLongRow.PrimaryText.Contains("HKCU\\", StringComparison.Ordinal)
            && firstLongRow.PrimaryText.EndsWith("TailOne", StringComparison.Ordinal)
            && secondLongRow.PrimaryText.EndsWith("TailTwo", StringComparison.Ordinal)
            && firstLongRow.PrimaryText != secondLongRow.PrimaryText
            && firstLongRow.AccessibleName.Contains(firstLongLocation.Address, StringComparison.Ordinal)
            && firstLongRow.AccessibleName.Contains("32-bit", StringComparison.Ordinal),
            "Long Journal targets abbreviate the hive and shared path prefix while retaining unique tails and full accessible view/path");
        var oldValue = new RegistryValue("Counter", RegistryValueType.Binary, [1]);
        var newValue = new RegistryValue("Counter", RegistryValueType.Binary, [2]);
        var valueScope = new JournalScope(location, ["Counter"]);
        var beforeState = new JournalState(
            valueScope,
            new RegistryBranchSnapshot(1, location, time, time, [new SnapshotKey(string.Empty, [oldValue])], Environment.MachineName), []);
        var afterState = new JournalState(
            valueScope,
            new RegistryBranchSnapshot(1, location, time, time, [new SnapshotKey(string.Empty, [newValue])], Environment.MachineName), []);
        var changedEntry = new JournalEntry(Guid.NewGuid(), time, "Edit value", [beforeState], [afterState]);
        var changedRow = new InvestigationRow(
            "Edit value", "Edit value", location.Address, string.Empty, string.Empty, string.Empty, changedEntry);
        check(
            changedRow.PrimaryText.Contains("Counter", StringComparison.Ordinal)
            && changedRow.SecondaryText.Contains("REG_BINARY 01 → REG_BINARY 02", StringComparison.Ordinal),
            "Simple value-only Journal snapshots expose a bounded captured before/after preview");
        var incomplete = changedEntry with { After = null };
        var failed = changedEntry with { Error = "Incomplete capture" };
        check(
            new InvestigationRow(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, incomplete)
                .SecondaryText
            == changedEntry.Operation
            && new InvestigationRow(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, failed)
                .SecondaryText
            == changedEntry.Operation, "Incomplete and failed Journal captures do not imply a before or after value");
        check(
            ToolPresentation.JournalStatus(entry, false) == "Completed" && ToolPresentation.JournalStatus(entry, true).Contains("Reverted"),
            "Journal exposes successful and reverted state");
        check(
            ToolPresentation.JournalStatus(entry with { Error = "Write failed" }, false).Contains("Write failed")
            && ToolPresentation.JournalStatus(entry with { After = null }, false).Contains("Incomplete"),
            "Journal retains failure and incomplete status");

        var payload = new byte[1024 * 1024];
        var changedPayload = (byte[])payload.Clone();
        changedPayload[^20] = 0xff;
        var oldBytes = new RegistryValue("Counter", RegistryValueType.Binary, payload);
        var newBytes = new RegistryValue("Counter", RegistryValueType.Binary, changedPayload);
        var bytes = new ToolComparison(string.Empty, string.Empty, string.Empty, BeforeValue: oldBytes, AfterValue: newBytes);
        var diff = bytes.DescribeByteDifference();
        check(
            diff.Contains($"0x{payload.Length - 20:X}")
            && diff.Contains("After: FF 00")
            && diff.Split('\n')[2].Length == "After: ".Length + 47,
            "Byte comparison reaches changes beyond the preview and formats at most 16 bytes per side");
        check(
            (bytes with { AfterValue = oldBytes }).DescribeByteDifference().Contains("identical"),
            "Equal payloads have no differing offset");
        var empty = new RegistryValue("Counter", RegistryValueType.Binary, []);
        var one = new RegistryValue("Counter", RegistryValueType.Binary, [1]);
        check(
            (bytes with { BeforeValue = empty, AfterValue = one }).DescribeByteDifference().Contains("Before: End of value\nAfter: 01")
            && (bytes with { BeforeValue = one, AfterValue = empty }).DescribeByteDifference().Contains("Before: 01\nAfter: End of value"),
            "Byte comparisons distinguish appended and truncated data from a zero byte");
        check(
            (bytes with { BeforeValue = null, AfterValue = empty }).DescribeByteDifference().Contains("added")
            && (bytes with { BeforeValue = empty, AfterValue = null }).DescribeByteDifference().Contains("removed"),
            "Adding and removing an empty value retain its existence change");
        var settings = new AppSettings();
        var folder = settings.AddFavoriteFolder("Nested", null);
        var favorite = settings.AddFavorite(" Remote ", "\\\\SERVER\\HKLM\\Software", true, folder.Id);
        check(
            favorite.Name == "Remote"
            && favorite.Address == "\\\\SERVER\\HKEY_LOCAL_MACHINE\\Software"
            && favorite.Is32Bit
            && favorite.FolderId == folder.Id, "Explicit favorite creation preserves entered remote location, view and destination");
        check(
            settings.AddFavorite("Computer", "Computer", false, null).Address == "Computer",
            "Explicit favorite creation supports the computer root");
        var count = settings.Favorites.Count;
        reject(
            () => settings.AddFavorite("Blank", string.Empty, false, null),
            "Explicit favorite rejects an empty location without falling back to browser context");
        reject(() => settings.AddFavorite(" ", "HKCU", false, null), "Explicit favorite rejects blank names");
        reject(() => settings.AddFavorite("Bad", "\\\\SERVER\\HKCU", false, null), "Explicit favorite retains remote hive validation");
        reject(() => settings.AddFavorite("Orphan", "HKCU", false, "missing"), "Explicit favorite rejects a missing destination folder");
        check(settings.Favorites.Count == count, "Invalid favorite input never changes the collection");
    }
}
