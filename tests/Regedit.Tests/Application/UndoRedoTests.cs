// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.AccessControl;
using System.Security.Principal;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class UndoRedoTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registry)
    {
        var empty = new BrowserViewModel(new RegistryService(), RegistryLocation.Parse("HKCU"));
        check(!empty.UndoCommand.CanExecute(null) && !empty.RedoCommand.CanExecute(null), "A fresh tab has no Undo or Redo");
        empty.CancelPending();
        if (!registry)
        {
            return;
        }

        var root = RegistryLocation.Parse(@"HKCU\Software\RegeditModern.Tests\UndoRedo_" + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateTempSubdirectory("RegeditUndoRedo-");
        var raw = new RegistryService();
        var journal = new RegistryJournal(folder.FullName);
        var service = new RegistryService { Journal = journal };
        var history = new RegistryHistoryBroadcaster();
        var tabs = new List<BrowserViewModel>();
        var prompts = new Dictionary<BrowserViewModel, Func<RegistryEditPrompt, Task<bool>>>();
        try
        {
            Registry.CurrentUser.CreateSubKey(root.SubKey).Dispose();
            raw.Write(root, new RegistryValue("Overlapping", RegistryValueType.Binary, [0]), null);
            var overlapping = new List<JournalEntry>();
            journal.TrackEdits(
                () =>
                {
                    service.Write(
                        root, new RegistryValue("Overlapping", RegistryValueType.Binary, [1]), raw.ReadValue(root, "Overlapping"));
                    service.Write(
                        root, new RegistryValue("Overlapping", RegistryValueType.Binary, [2]), raw.ReadValue(root, "Overlapping"));
                }, overlapping);
            journal.Revert(overlapping.Select(entry => entry.Id).ToArray());
            check(
                raw.ReadValue(root, "Overlapping")?.Data.SequenceEqual(new byte[] { 0 }) == true,
                "Batch Undo restores the original value across overlapping entries");

            raw.CreateKey(root, "Elsewhere");
            var a = NewTab();
            var b = NewTab();
            var notices = new List<(string Title, bool Review)>();
            a.HistoryNotice += (title, _, review) => notices.Add((title, review));
            await a.NavigateAsync(root, false);
            await b.NavigateAsync(root, false);
            await CreateValue(a, "First", "01");
            check(
                a.UndoLabel.Contains("First")
                && a.UndoHelp.Contains(root.Address)
                && a.UndoHelp.Contains("64-bit")
                && !b.UndoCommand.CanExecute(null), "Only the originating tab owns an edit and its target is explicit");
            await a.NavigateAsync(root.Child("Elsewhere"));
            await a.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "First") is null
                && a.Location.IsSameLocation(root.Child("Elsewhere"))
                && a.RedoCommand.CanExecute(null), "Undo survives navigation and keeps the current location");
            await a.RedoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "First")?.Data.SequenceEqual(new byte[] { 1 }) == true,
                "Redo writes the recorded bytes at the original target");
            await a.GoToHistoryChangeCommand.ExecuteAsync(null);
            check(a.Location.IsSameLocation(root) && a.SelectedEntry?.Name == "First", "Go to change reveals the restored value");

            await CreateValue(b, "Independent", "02");
            await a.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "Independent") is not null
                && raw.ReadValue(root, "First") is null
                && b.UndoLabel.Contains("Independent"), "Unrelated edits in another tab remain independent");
            await b.UndoCommand.ExecuteAsync(null);
            await CreateValue(a, "New", "03");
            check(!a.RedoCommand.CanExecute(null) && b.RedoCommand.CanExecute(null), "A new edit clears only its own tab's Redo");
            await b.RedoCommand.ExecuteAsync(null);

            a.SelectedEntry = a.Entries.Single(row => row.Name == "New");
            prompts[a] = prompt =>
            {
                ((RegistryValueEditorViewModel)prompt).Data = "04";
                return Task.FromResult(true);
            };
            await a.EditValueCommand.ExecuteAsync(null);
            await a.UndoCommand.ExecuteAsync(null);
            check(
                a.SelectedEntry?.Name == "New" && raw.ReadValue(root, "New")?.Data[0] == 3, "Undo editing restores the data and selection");
            await a.RedoCommand.ExecuteAsync(null);
            check(raw.ReadValue(root, "New")?.Data[0] == 4, "Redo editing restores the committed replacement");
            SetPrompt(a);
            await a.RenameEntryAsync(a.Entries.Single(row => row.Name == "New"), "Renamed");
            await a.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "New")?.Data[0] == 4 && raw.ReadValue(root, "Renamed") is null,
                "Undo rename restores the old value name");
            await a.RedoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "New") is null && raw.ReadValue(root, "Renamed")?.Data[0] == 4,
                "Redo rename restores the new value name");

            var undoLabel = a.UndoLabel;
            raw.Write(root, new RegistryValue("Renamed", RegistryValueType.Binary, [9]), raw.ReadValue(root, "Renamed"));
            await a.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "Renamed")?.Data[0] == 9
                && a.UndoLabel == undoLabel
                && !a.RedoCommand.CanExecute(null)
                && notices.Last().Review, "An external conflict preserves foreign data and never skips to an older edit");
            raw.Write(root, new RegistryValue("Renamed", RegistryValueType.Binary, [4]), raw.ReadValue(root, "Renamed"));
            await a.UndoCommand.ExecuteAsync(null);
            check(a.RedoCommand.CanExecute(null), "The same blocked action can be retried after resolving its conflict");
            var beforeFailedEdit = a.UndoLabel;
            var attempts = 0;
            prompts[a] = prompt =>
            {
                if (attempts++ > 0)
                {
                    return Task.FromResult(false);
                }

                var editor = (RegistryValueEditorViewModel)prompt;
                editor.Name = "New";
                editor.Data = "04";
                return Task.FromResult(true);
            };
            await a.CreateValueCommand.ExecuteAsync("3");
            SetPrompt(a);
            check(
                a.UndoLabel == beforeFailedEdit && a.RedoCommand.CanExecute(null),
                "A failed edit with identical recorded states preserves Undo and Redo");

            var group = NewTab();
            await group.NavigateAsync(root, false);
            await CreateValue(group, "GroupA", "05");
            await CreateValue(group, "GroupB", "06");
            group.SetSelection(group.Entries.Where(row => row.Name is "GroupA" or "GroupB"));
            await group.DeleteCommand.ExecuteAsync(null);
            check(group.UndoLabel == "Undo delete 2 items", "A multiselection delete occupies one history step");
            raw.Write(root, new RegistryValue("GroupA", RegistryValueType.Binary, [8]), null);
            var beforeConflict = journal.ReadEntries().Length;
            await group.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "GroupB") is null
                && raw.ReadValue(root, "GroupA")?.Data[0] == 8
                && journal.ReadEntries().Length == beforeConflict
                && group.UndoLabel == "Undo delete 2 items", "Grouped Undo preflights every item before restoring any item");
            raw.DeleteValue(root, raw.ReadValue(root, "GroupA")!);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await group.UndoCommand.ExecuteAsync(null);
                check(
                    raw.ReadValue(root, "GroupA")?.Data[0] == 5 && raw.ReadValue(root, "GroupB")?.Data[0] == 6,
                    $"Grouped Undo restores both values in cycle {cycle}");
                await group.RedoCommand.ExecuteAsync(null);
                check(
                    raw.ReadValue(root, "GroupA") is null && raw.ReadValue(root, "GroupB") is null,
                    $"Grouped Redo deletes both values in cycle {cycle}");
            }

            raw.CreateKey(root, "OriginalKey");
            raw.Write(root.Child("OriginalKey"), new RegistryValue("Kept", RegistryValueType.Binary, [7]), null);
            using (var kept = raw.Open(root.Child("OriginalKey")))
            {
                var keys = NewTab();
                await keys.NavigateAsync(root, false);
                await keys.RenameEntryAsync(keys.Entries.Single(row => row.Name == "OriginalKey"), "RenamedKey");
                keys.ConfirmHistoryChange = (_, _) => Task.FromResult(false);
                await keys.UndoCommand.ExecuteAsync(null);
                check(
                    raw.SubKeys(root).Contains("RenamedKey") && !keys.RedoCommand.CanExecute(null),
                    "Canceling a subtree Undo preserves the registry and history");
                keys.ConfirmHistoryChange = (_, _) => Task.FromResult(true);
                await keys.NavigateAsync(root.Child("RenamedKey"));
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    await keys.UndoCommand.ExecuteAsync(null);
                    check(
                        keys.Location.IsSameLocation(root.Child("OriginalKey"))
                        && ((byte[])kept.GetValue("Kept")!).SequenceEqual(new byte[] { 7 }),
                        $"Key rename Undo preserves the open key and follows its location in cycle {cycle}");
                    await keys.RedoCommand.ExecuteAsync(null);
                    check(
                        keys.Location.IsSameLocation(root.Child("RenamedKey"))
                        && ((byte[])kept.GetValue("Kept")!).SequenceEqual(new byte[] { 7 }),
                        $"Key rename Redo preserves the open key and follows its location in cycle {cycle}");
                }
            }

            var structural = NewTab();
            await structural.NavigateAsync(root, false);
            prompts[structural] = prompt =>
            {
                ((RegistryNamePrompt)prompt).Name = "CreatedKey";
                return Task.FromResult(true);
            };
            await structural.CreateKeyCommand.ExecuteAsync(null);
            SetPrompt(structural);
            await structural.NavigateAsync(root.Child("CreatedKey"));
            await structural.UndoCommand.ExecuteAsync(null);
            check(
                structural.Location.IsSameLocation(root) && !raw.SubKeys(root).Contains("CreatedKey"),
                "Undo creating the displayed key returns to its parent");
            await structural.RedoCommand.ExecuteAsync(null);
            check(raw.SubKeys(root).Contains("CreatedKey"), "Redo recreates a newly created key");
            raw.CreateKey(root.Child("CreatedKey"), "Nested");
            raw.Write(root.Child("CreatedKey").Child("Nested"), new RegistryValue("Raw", RegistryValueType.Binary, [0, 255, 1]), null);
            await structural.DeleteTreeKeyCommand.ExecuteAsync(root.Child("CreatedKey"));
            await structural.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root.Child("CreatedKey").Child("Nested"), "Raw")?.Data.SequenceEqual(new byte[] { 0, 255, 1 }) == true,
                "Undo a subtree deletion restores its descendants and exact value bytes");
            await structural.RedoCommand.ExecuteAsync(null);
            check(!raw.SubKeys(root).Contains("CreatedKey"), "Redo a subtree deletion removes it again");

            structural.CopyTreeKeyCommand.Execute(root.Child("RenamedKey"));
            await structural.NavigateAsync(root.Child("Elsewhere"));
            prompts[structural] = prompt =>
            {
                ((RegistryNamePrompt)prompt).Name = "PastedKey";
                return Task.FromResult(true);
            };
            await structural.PasteCommand.ExecuteAsync(null);
            SetPrompt(structural);
            await structural.UndoCommand.ExecuteAsync(null);
            raw.Write(
                root.Child("RenamedKey"), new RegistryValue("Kept", RegistryValueType.Binary, [9]),
                raw.ReadValue(root.Child("RenamedKey"), "Kept"));
            await structural.RedoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root.Child("Elsewhere").Child("PastedKey"), "Kept")?.Data[0] == 7
                && raw.ReadValue(root.Child("RenamedKey"), "Kept")?.Data[0] == 9,
                "Redo paste restores the recorded destination without recopying a changed source");

            var partial = NewTab();
            await partial.NavigateAsync(root, false);
            await CreateValue(partial, "PartialA", "01");
            await CreateValue(partial, "PartialB", "02");
            partial.SetSelection(new[] { "PartialA", "PartialB" }.Select(name => partial.Entries.Single(row => row.Name == name)));
            raw.Write(root, new RegistryValue("PartialB", RegistryValueType.Binary, [3]), raw.ReadValue(root, "PartialB"));
            await partial.DeleteCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "PartialA") is null
                && raw.ReadValue(root, "PartialB")?.Data[0] == 3
                && !partial.UndoCommand.CanExecute(null)
                && !partial.RedoCommand.CanExecute(null)
                && partial.UndoHelp.Contains("Inspect"), "A partially failed edit blocks the history chain and exposes recovery");
            await CreateValue(partial, "AfterPartial", "01");
            await partial.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "AfterPartial") is null && !partial.UndoCommand.CanExecute(null),
                "New edits can be undone without crossing a partial-failure boundary");

            var refresh = NewTab();
            var refreshNotice = string.Empty;
            var refreshMessage = string.Empty;
            refresh.HistoryNotice += (title, message, _) => (refreshNotice, refreshMessage) = (title, message);
            await refresh.NavigateAsync(root, false);
            await CreateValue(refresh, "RefreshFailure", "01");
            Func<BrowserViewModel, IReadOnlyList<JournalEntry>, Task> failRefresh = (source, _) => source == refresh
                ? Task.FromException(new IOException("Refresh fixture"))
                : Task.CompletedTask;
            history.Changed += failRefresh;
            await refresh.UndoCommand.ExecuteAsync(null);
            history.Changed -= failRefresh;
            check(
                raw.ReadValue(root, "RefreshFailure") is null
                && refresh.RedoCommand.CanExecute(null)
                && refreshNotice == "Couldn't refresh the view"
                && refreshMessage.StartsWith("The registry has changed.", StringComparison.Ordinal),
                "A refresh failure reports the successful mutation and preserves Redo");

            var capturedA = new List<JournalEntry>();
            var capturedB = new List<JournalEntry>();
            await Task.WhenAll(
                Task.Run(() => journal.TrackEdits(
                    () => service.Write(root, new RegistryValue("TrackedA", RegistryValueType.Binary, [1]), null), capturedA)),
                Task.Run(() => journal.TrackEdits(
                    () => service.Write(root, new RegistryValue("TrackedB", RegistryValueType.Binary, [2]), null), capturedB)));
            service.Write(root, new RegistryValue("Untracked", RegistryValueType.Binary, [3]), null);
            check(
                capturedA.Count == 1
                && capturedB.Count == 1
                && capturedA[0].Before[0].Scope.ValueNames![0] == "TrackedA"
                && capturedB[0].Before[0].Scope.ValueNames![0] == "TrackedB"
                && capturedA[0].After is not null,
                "Concurrent commands capture their own completed entries and tracking ends with the command");
            var pending = new List<JournalEntry>();
            var bounded = new RegistryJournal(Path.Combine(folder.FullName, "bounded"), false, 8192);
            try
            {
                bounded.TrackEdits(
                    () => bounded.Execute(
                        "Edit value", [new JournalScope(root, ["Untracked"])],
                        () => raw.Write(
                            root, new RegistryValue("Untracked", RegistryValueType.Binary, new byte[8192]),
                            raw.ReadValue(root, "Untracked"))), pending);
            }
            catch (IOException)
            {
            }

            check(
                pending.Count == 1 && pending[0].After is null && raw.ReadValue(root, "Untracked")?.Data.Length == 8192,
                "Tracking retains the pending entry when saving the final state fails after mutation");

            raw.CreateKey(root, "DeniedUndo");
            var deniedTab = NewTab();
            var deniedLocation = root.Child("DeniedUndo");
            await deniedTab.NavigateAsync(deniedLocation, false);
            await CreateValue(deniedTab, "DeniedValue", "01");
            using (var key = Registry.CurrentUser.OpenSubKey(
                       deniedLocation.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
                       RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var saved = key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                var denied = key.GetAccessControl(AccessControlSections.Access);
                denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.SetValue, AccessControlType.Deny));
                try
                {
                    key.SetAccessControl(denied);
                    await deniedTab.UndoCommand.ExecuteAsync(null);
                    check(
                        raw.ReadValue(deniedLocation, "DeniedValue") is not null
                        && deniedTab.UndoCommand.CanExecute(null)
                        && !deniedTab.RedoCommand.CanExecute(null),
                        "An Undo that fails without changing data preserves the action for retry");
                }
                finally
                {
                    var restored = new RegistrySecurity();
                    restored.SetSecurityDescriptorSddlForm(saved, AccessControlSections.Access);
                    key.SetAccessControl(restored);
                }
            }

            await deniedTab.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(deniedLocation, "DeniedValue") is null && deniedTab.RedoCommand.CanExecute(null),
                "Undo succeeds after its write permissions are restored");

            var activity = NewTab();
            await activity.NavigateAsync(root, false);
            await CreateValue(activity, "BeforeActivity", "01");
            journal.RecordActivity("Test external operation", root, "Undo boundary", () => { });
            await activity.UndoCommand.ExecuteAsync(null);
            check(
                raw.ReadValue(root, "BeforeActivity") is not null && !activity.RedoCommand.CanExecute(null),
                "Unsupported external activity blocks an earlier Undo");
            var duplicate = NewTab();
            check(!duplicate.UndoCommand.CanExecute(null), "A duplicated location starts with an empty history");
            activity.CancelPending();
            check(
                !activity.UndoCommand.CanExecute(null) && !activity.RedoCommand.CanExecute(null),
                "Closing a tab disables its edit history");

            var originalRename = journal.ReadEntries().First(entry => entry.OperationKind == JournalOperation.RenameKey);
            var mislinked = new List<JournalEntry>();
            journal.TrackEdits(
                () => journal.Execute(
                    "Mislinked revert", [new JournalScope(root, ["Mislinked"])],
                    () => raw.Write(root, new RegistryValue("Mislinked", RegistryValueType.Binary, [1]), null), originalRename.Id),
                mislinked);
            var historyCount = journal.ReadEntries().Length;
            var refused = false;
            try
            {
                journal.Revert(mislinked.Single().Id);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            check(
                refused && journal.ReadEntries().Length == historyCount && raw.ReadValue(root, "Mislinked")?.Data[0] == 1,
                "A forged key-rename lineage is rejected before any inverse write");
        }
        finally
        {
            foreach (var tab in tabs)
            {
                tab.CancelPending();
            }

            Registry.CurrentUser.DeleteSubKeyTree(root.SubKey, false);
            folder.Delete(true);
        }

        BrowserViewModel NewTab()
        {
            var tab = new BrowserViewModel(service, root, new RegistryEntryClipboard(), historyBroadcaster: history)
            {
                ShowKeysInList = true
            };
            SetPrompt(tab);
            tab.EditPromptRequested += prompt => prompts[tab](prompt);
            tabs.Add(tab);
            return tab;
        }

        void SetPrompt(BrowserViewModel tab)
        {
            prompts[tab] = _ => Task.FromResult(true);
        }

        async Task CreateValue(BrowserViewModel tab, string name, string data)
        {
            prompts[tab] = prompt =>
            {
                var editor = (RegistryValueEditorViewModel)prompt;
                editor.Name = name;
                editor.Data = data;
                return Task.FromResult(true);
            };
            await tab.CreateValueCommand.ExecuteAsync("3");
            SetPrompt(tab);
        }
    }
}
