// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class DuplicateValueTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var software = RegistryLocation.Parse(@"HKCU\Software");
        var preview = new BrowserViewModel(new RegistryService(), software);
        var source = new RegistryEntry(null, new RegistryValue("Payload", RegistryValueType.Binary, [255, 254]));
        var prompts = 0;
        preview.EditPromptRequested += prompt =>
        {
            prompts++;
            check(
                prompt is RegistryNamePrompt { Operation: RegistryNameOperation.DuplicateValue }
                && !preview.DuplicateValueCommand.CanExecute(null),
                "Duplicate uses the existing name prompt and disables edits while it is open");
            return Task.FromResult(false);
        };
        foreach (var selection in new RegistryEntry[][]
                 {
                     [], [new RegistryEntry(software.Child("Child"), null)], [new RegistryEntry(null, null, true)],
                     [new RegistryEntry(null, null, ParentLocation: software.Parent)],
                     [source, source with { Value = source.Value! with { Name = "Other" } }]
                 })
        {
            preview.SetSelection(selection);
            check(!preview.DuplicateValueCommand.CanExecute(null), "Duplicate requires exactly one stored value");
            await preview.DuplicateValueCommand.ExecuteAsync(null);
        }

        check(prompts == 0, "Disabled Duplicate never opens a prompt");
        var notifications = 0;
        preview.DuplicateValueCommand.CanExecuteChanged += (_, _) => notifications++;
        preview.SelectedEntry = source;
        check(
            preview.DuplicateValueCommand.CanExecute(null) && notifications > 0, "Selecting a stored value updates the Duplicate command");
        await preview.DuplicateValueCommand.ExecuteAsync(null);
        check(prompts == 1 && preview.CanPrompt, "Canceling Duplicate restores the edit commands without writing");
        preview.CancelPending();
        check(!preview.DuplicateValueCommand.CanExecute(null), "Closing the tab disables Duplicate");

        if (!registryTests)
        {
            return;
        }

        var root = software.Child("RegeditDuplicateValues." + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateTempSubdirectory("RegeditDuplicateValues.");
        var raw = new RegistryService();
        var journal = new RegistryJournal(folder.FullName);
        var clipboard = new RegistryEntryClipboard { Entry = source };
        var tab = new BrowserViewModel(new RegistryService { Journal = journal }, root, clipboard);
        raw.CreateKey(software, root.Name);
        try
        {
            RegistryValue[] values =
            [
                source.Value!, new("Malformed", RegistryValueType.String, [65, 0, 255]),
                new(
                    "Expandable", RegistryValueType.ExpandString,
                    RegistryValue.ParseData(RegistryValueType.ExpandString, @"%SystemRoot%\System32")),
                new("Multi", RegistryValueType.MultiString, [65, 0, 0, 0, 0, 0, 66, 0, 0, 0, 0, 0]),
                new("DWord", RegistryValueType.DWord, [255, 255, 255, 255]),
                new("QWord", RegistryValueType.QWord, [255, 255, 255, 255, 255, 255, 255, 255]),
                new("Uncommon", (RegistryValueType)99, [0, 255, 42]), new(string.Empty, RegistryValueType.Binary, [])
            ];
            foreach (var value in values)
            {
                raw.Write(root, value, null);
            }

            var taken = new RegistryValue(
                "pAyLoAd - cOpY", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "existing"));
            raw.Write(root, taken, null);
            raw.Write(root, taken with { Name = "Payload - Copy (2)" }, null);
            await tab.NavigateAsync(root, false);
            tab.Filter = "REG_BINARY";
            await tab.PendingFilter;

            Task<bool> Confirm(RegistryEditPrompt prompt)
            {
                if (prompt.Error is not null)
                {
                    check(false, "Duplicating a value succeeds: " + prompt.Error);
                }

                return Task.FromResult(true);
            }

            tab.EditPromptRequested += Confirm;

            string? lastCopy = null;
            foreach (var value in values)
            {
                tab.SelectedEntry = tab.Entries.Single(entry => entry.Value?.Name == value.Name);
                await tab.DuplicateValueCommand.ExecuteAsync(null);
                lastCopy = value.Name == "Payload" ? "Payload - Copy (3)" : (value.Name.Length == 0 ? "Default" : value.Name) + " - Copy";
                var copy = raw.ReadValue(root, lastCopy);
                check(
                    copy?.Type == value.Type && copy.Data.SequenceEqual(value.Data) && value.SameData(raw.ReadValue(root, value.Name)),
                    "Duplicate preserves the original type and every byte: " + value.DisplayName);
                check(
                    tab.SelectedEntry?.Value?.Name == lastCopy && ReferenceEquals(clipboard.Entry, source),
                    "Duplicate selects the copy and preserves the clipboard: " + value.DisplayName);
                tab.Filter = string.Empty;
                await tab.PendingFilter;
            }

            check(
                taken.SameData(raw.ReadValue(root, taken.Name))
                && journal.ReadEntries().Count(entry => entry.OperationKind == JournalOperation.CreateValue) == values.Length,
                "Duplicate skips filtered names without overwriting and journals each copy as a creation");
            await tab.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, lastCopy!) is null && values[^1].SameData(raw.ReadValue(root, string.Empty)),
                "Undo removes only the duplicate and leaves the stored default intact");
            await tab.RedoCommand.ExecuteAsync(null);
            check(
                values[^1].Data.SequenceEqual(raw.ReadValue(root, lastCopy!)!.Data),
                "Redo restores the duplicate through the existing history");

            var longValue = new RegistryValue(new string('x', 16383), RegistryValueType.Binary, [1]);
            raw.Write(root, longValue, null);
            await tab.RefreshCommand.ExecuteAsync(null);
            tab.SelectedEntry = tab.Entries.Single(entry => entry.Value?.Name == longValue.Name);
            await tab.DuplicateValueCommand.ExecuteAsync(null);
            check(
                tab.SelectedEntry?.Value is { Name.Length: 16383 } longCopy
                && longCopy.Name != longValue.Name
                && longCopy.Data.SequenceEqual(longValue.Data),
                "The suggested duplicate name fits the Windows limit even for a maximum-length source");

            tab.EditPromptRequested -= Confirm;
            var collisionShown = false;
            tab.EditPromptRequested += prompt =>
            {
                if (prompt.Error is null)
                {
                    ((RegistryNamePrompt)prompt).Name = taken.Name;
                    return Task.FromResult(true);
                }

                collisionShown = true;
                return Task.FromResult(false);
            };
            tab.SelectedEntry = tab.Entries.Single(entry => entry.Value?.Name == "Payload");
            var undoBefore = tab.UndoLabel;
            await tab.DuplicateValueCommand.ExecuteAsync(null);
            check(
                collisionShown
                && taken.SameData(raw.ReadValue(root, taken.Name))
                && source.Value!.SameData(raw.ReadValue(root, "Payload"))
                && tab.UndoLabel == undoBefore,
                "An existing name reports a retryable conflict without overwriting data or adding an Undo step");
        }
        finally
        {
            tab.CancelPending();
            raw.DeleteKey(root);
            folder.Delete(true);
        }
    }
}
