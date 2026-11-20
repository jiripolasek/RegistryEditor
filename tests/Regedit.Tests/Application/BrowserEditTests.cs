// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class BrowserEditTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests = true)
    {
        await EditRecoveryTests.RunAsync(check, registryTests);
        var number = new RegistryValueEditorViewModel(
            new RegistryValue("count", RegistryValueType.DWord, [255, 0, 0, 0]), RegistryValueType.DWord, false, false);
        number.BaseIndex = 1;
        check(number.Data == "255" && number.IsNumeric && number.IsNameReadOnly, "The numeric editor converts hexadecimal to decimal");
        number.Data = "invalid";
        number.BaseIndex = 0;
        check(
            number.BaseIndex == 1 && number.HasError && !number.Validate(),
            "An invalid number keeps its data and base and exposes the validation error");
        number.Data = "16";
        number.BaseIndex = 0;
        check(
            number.BaseIndex == 0 && number.Validate() && number.Value.Data.SequenceEqual(new byte[] { 16, 0, 0, 0 }),
            "Correcting the number clears the error and preserves its bytes across bases");
        var malformed = new RegistryValueEditorViewModel(
            new RegistryValue("raw", RegistryValueType.String, [65, 0, 255]), RegistryValueType.String, false, false);
        check(
            malformed.IsRaw && malformed.Validate() && malformed.Value.Data.SequenceEqual(new byte[] { 65, 0, 255 }),
            "A string that cannot round trip opens in raw mode with its exact original bytes");
        var emptyMulti = new RegistryValueEditorViewModel(
            new RegistryValue("m", RegistryValueType.MultiString, [0, 0]), RegistryValueType.MultiString, false, false);
        check(
            !emptyMulti.IsRaw && emptyMulti.IsMultiString && emptyMulti.Data.Length == 0,
            "An empty multi-string stored with a single terminator opens in the text editor");
        check(
            emptyMulti.Validate() && emptyMulti.Value.Data.SequenceEqual(new byte[] { 0, 0 }),
            "Confirming an unchanged value keeps its original bytes");
        emptyMulti.Data = "x";
        check(
            emptyMulti.Validate() && emptyMulti.Value.Data.SequenceEqual(new byte[] { (byte)'x', 0, 0, 0, 0, 0 }),
            "Editing the empty multi-string writes the canonical double terminator");
        var singleTerminator = new RegistryValueEditorViewModel(
            new RegistryValue("m", RegistryValueType.MultiString, [(byte)'a', 0, 0, 0]), RegistryValueType.MultiString, false, false);
        check(
            !singleTerminator.IsRaw && singleTerminator.Data == "a",
            "A multi-string missing its final terminator opens in the text editor");
        var unterminated = new RegistryValueEditorViewModel(
            new RegistryValue("s", RegistryValueType.String, [(byte)'a', 0]), RegistryValueType.String, false, false);
        check(!unterminated.IsRaw && unterminated.Data == "a", "A string without a terminator opens in the text editor");
        foreach (var type in new RegistryValueType[] { RegistryValueType.String, RegistryValueType.ExpandString })
        {
            var singleLine = new RegistryValue("plain", type, RegistryValue.ParseData(type, "original"));
            var singleLineEditor = new RegistryValueEditorViewModel(singleLine, type, false, false);
            check(
                singleLineEditor.CanToggleMultiline
                && !singleLineEditor.IsMultiline
                && !new RegistryValueEditorViewModel(null, type, false, true).IsMultiline,
                $"Type {type} opens existing single-line and new values with multiline editing off");
            check(
                !singleLineEditor.ValidateLineBreaks("pasted\r\nlines")
                && singleLineEditor.Data == "original"
                && singleLineEditor.ValidateLineBreaks("single line")
                && !singleLineEditor.HasError, $"Type {type} rejects multiline paste before the text box can truncate it");
            singleLineEditor.Data = "pasted\r\nlines";
            check(
                !singleLineEditor.Validate()
                && singleLineEditor.HasError
                && singleLineEditor.Value.SameData(singleLine)
                && singleLineEditor.Data == "pasted\r\nlines",
                $"Type {type} blocks saving pasted line breaks without changing the draft or original value");
            singleLineEditor.IsMultiline = true;
            check(
                !singleLineEditor.HasError
                && singleLineEditor.Validate()
                && singleLineEditor.Value.EditText(false) == "pasted\r\nlines"
                && !new RegistryValueEditorViewModel(singleLine, type, false, false).IsMultiline,
                $"Type {type} permits line breaks only in the editor instance that opted in");
            singleLineEditor.IsMultiline = false;
            check(
                !singleLineEditor.CanToggleMultiline
                && singleLineEditor.IsMultiline
                && singleLineEditor.Validate()
                && singleLineEditor.Data == "pasted\r\nlines",
                $"Type {type} keeps multiline editing on while the draft contains line breaks");
            singleLineEditor.Data = "single line";
            singleLineEditor.IsMultiline = false;
            check(
                singleLineEditor.CanToggleMultiline
                && !singleLineEditor.IsMultiline
                && singleLineEditor.Validate()
                && singleLineEditor.Value.EditText(false) == "single line",
                $"Type {type} can save again after removing line breaks while multiline editing is off");
            const string lines = "one\r\ntwo\rthree\nfour";
            var original = new RegistryValue("lines", type, RegistryValue.ParseData(type, lines));
            var editor = new RegistryValueEditorViewModel(original, type, false, false);
            check(
                editor.IsMultiline && !editor.IsRaw && editor.Data == lines && editor.Validate() && editor.Value.SameData(original),
                $"Type {type} opens and confirms embedded line breaks without changing any bytes");
            editor.Data += "\nlast";
            check(
                editor.Validate() && editor.Value.EditText(false) == lines + "\nlast",
                $"Type {type} preserves existing and newly edited line breaks");
        }

        foreach (var lineBreak in new[] { "\r\n", "\r", "\n", "\f", "\u0085", "\u2028", "\u2029" })
        {
            var text = "one" + lineBreak + "two";
            var original = new RegistryValue("lines", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, text));
            var editor = new RegistryValueEditorViewModel(original, RegistryValueType.String, false, false);
            check(
                editor.IsMultiline && editor.Validate() && editor.Value.SameData(original),
                "Existing strings detect supported line break characters and preserve their bytes");
            editor.IsMultiline = false;
            check(
                editor.IsMultiline && !editor.CanToggleMultiline && editor.Validate() && editor.Value.SameData(original),
                "Multiline mode cannot be disabled while the value still contains a line break");
        }

        var binary = new RegistryValueEditorViewModel(
            new RegistryValue("b", RegistryValueType.Binary, [1, 2]), RegistryValueType.Binary, false, false);
        check(
            !number.CanToggleMultiline
            && !number.IsMultiline
            && !binary.CanToggleMultiline
            && binary.IsMultiline
            && !emptyMulti.CanToggleMultiline
            && emptyMulti.IsMultiline
            && !malformed.CanToggleMultiline
            && malformed.IsMultiline, "Numeric multi-string binary and malformed-string editors keep their existing input modes");
        binary.Data = "0A 0B 0C";
        check(binary.PreviewBytes.SequenceEqual(new byte[] { 10, 11, 12 }), "The hex preview follows valid raw edits");
        binary.Data = "0A 0";
        check(
            binary.PreviewBytes.SequenceEqual(new byte[] { 10, 11, 12 }),
            "The hex preview keeps the last valid bytes while the text is incomplete");
        var software = RegistryLocation.Parse(@"HKCU\Software");
        var preview = new BrowserViewModel(new RegistryService(), software);
        var dialogsShown = 0;
        preview.EditPromptRequested += _ =>
        {
            dialogsShown++;
            return Task.FromResult(false);
        };
        var parent = new RegistryEntry(null, null, ParentLocation: software.Parent);
        var unset = new RegistryEntry(null, null, true);
        var value = new RegistryEntry(null, new RegistryValue("Example", RegistryValueType.Binary, [1]));
        var childRow = new RegistryEntry(software.Child("Example child"), null);
        foreach (var rows in new RegistryEntry[][] { [], [parent], [unset], [new RegistryEntry(software.Parent, null)] })
        {
            preview.SetSelection(rows);
            check(
                !preview.DeleteCommand.CanExecute(null) && !preview.RenameCommand.CanExecute(null),
                "Empty or protected row disables Delete and F2: " + (rows.FirstOrDefault()?.Name ?? "(no selection)"));
            await preview.DeleteCommand.ExecuteAsync(null);
            await preview.RenameCommand.ExecuteAsync(null);
        }

        check(dialogsShown == 0, "Executing disabled Delete and F2 never prompts");
        preview.SetSelection([value, childRow]);
        check(
            preview.DeleteCommand.CanExecute(null) && !preview.RenameCommand.CanExecute(null),
            "A mixed selection permits batch deletion and disables rename");
        preview.SelectedEntry = childRow;
        check(
            ReferenceEquals(preview.ActivateEntryCommand, preview.NavigateEntryCommand) && preview.RenameCommand.CanExecute(null),
            "A child key activates navigation and enables rename");
        preview.SelectedEntry = value;
        check(
            ReferenceEquals(preview.ActivateEntryCommand, preview.EditValueCommand) && preview.CopyCommand.CanExecute(null),
            "A stored value activates editing and enables copy");
        preview.Filter = "filtered";
        check(!preview.HasSelection && !preview.DeleteCommand.CanExecute(null), "Filtering invalidates a captured edit selection");
        var createStates = new List<bool>();
        var entriesChanged = 0;
        preview.CreateKeyCommand.CanExecuteChanged += (_, _) => createStates.Add(preview.CreateKeyCommand.CanExecute(null));
        preview.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(preview.Entries))
            {
                entriesChanged++;
            }
        };
        await preview.NavigateAsync(software.GetComputer());
        check(
            createStates.Contains(false) && !preview.UpCommand.CanExecute(null) && !preview.CreateValueCommand.CanExecute("3"),
            "Leaving a key with no selection invalidates the location-dependent commands");
        check(
            preview.Filter == string.Empty && entriesChanged == 1 && preview.Entries.All(entry => entry.IsKey),
            "Navigating away from a filtered key publishes the new list once with its filter cleared");

        var closing = new BrowserViewModel(new RegistryService(), software) { SelectedEntry = value };
        var answer = new TaskCompletionSource<bool>();
        closing.EditPromptRequested += _ => answer.Task;
        var editing = closing.EditValueCommand.ExecuteAsync(null);
        closing.CancelPending();
        answer.SetResult(false);
        await editing;
        check(
            !closing.CreateKeyCommand.CanExecute(null) && !closing.RenameCommand.CanExecute(null),
            "Completing a pending prompt after tab closure keeps edit commands disabled");

        if (!registryTests)
        {
            return;
        }

        var fixture = software.Child("RegeditBrowserEdits." + Guid.NewGuid().ToString("N"));
        var setup = new RegistryService();
        var folder = Directory.CreateTempSubdirectory("RegeditBrowserJournal.");
        setup.CreateKey(software, fixture.Name);
        try
        {
            var child = fixture.Child("Child");
            setup.CreateKey(fixture, child.Name);
            setup.Write(fixture, new RegistryValue("First", RegistryValueType.Binary, [1]), null);
            setup.Write(fixture, new RegistryValue("Second", RegistryValueType.Binary, [2]), null);
            var journal = new RegistryJournal(folder.FullName);
            var service = new RegistryService { Journal = journal };
            var clipboard = new RegistryEntryClipboard();
            var tab = new BrowserViewModel(service, fixture, clipboard) { ShowKeysInList = true, ShowParentKeyInList = true };
            var dialogs = new Dialogs();
            tab.EditPromptRequested += dialogs.ShowAsync;
            var invalidations = 0;
            tab.RegistryChanged += (_, _) => invalidations++;
            var treeRefreshes = 0;
            tab.TreeRefreshRequested += () => treeRefreshes++;
            var branches = new List<(RegistryLocation Parent, (string OldName, string NewName)? Rename)>();

            Task BranchChanged(RegistryLocation parent, (string, string)? rename)
            {
                branches.Add((parent, rename));
                return Task.CompletedTask;
            }

            tab.TreeBranchChanged += BranchChanged;

            static Task Run(IAsyncRelayCommand command)
            {
                return command.ExecuteAsync(null);
            }

            static bool Enabled(IRelayCommand command)
            {
                return command.CanExecute(null);
            }

            RegistryEntry Row(string name)
            {
                return tab.Entries.Single(item => item.Name == name);
            }

            async Task CheckInertAsync(string situation)
            {
                var prompts = dialogs.Count;
                var writes = invalidations;
                check(!Enabled(tab.DeleteCommand) && !Enabled(tab.RenameCommand), situation + " disables Delete and F2");
                await Run(tab.DeleteCommand);
                await Run(tab.RenameCommand);
                check(
                    dialogs.Count == prompts && invalidations == writes && setup.SubKeys(fixture).Contains("Child"),
                    situation + " shows no dialog and leaves the current key intact");
            }

            await tab.NavigateAsync(fixture, false);
            await CheckInertAsync("An empty list selection");
            var renameNotifications = 0;
            tab.RenameCommand.CanExecuteChanged += (_, _) => renameNotifications++;
            tab.SelectedEntry = Row("First");
            check(
                renameNotifications > 0 && tab.RenameCommand.CanExecute(null), "Selecting a named value notifies the bound Rename command");
            tab.Filter = "no visible value";
            await CheckInertAsync("Filtering away the selected row");
            tab.Filter = string.Empty;
            tab.SelectedEntry = Row("First");
            await tab.RefreshCommand.ExecuteAsync(null);
            await CheckInertAsync("Refreshing a selected row");
            tab.SelectedEntry = Row("..");
            await CheckInertAsync("A parent row");
            tab.SelectedEntry = Row("(Default)");
            await CheckInertAsync("An unset default value");
            check(
                Enabled(tab.ActivateEntryCommand) && Enabled(tab.EditBinaryCommand) && !tab.CopyCommand.CanExecute(null),
                "The default placeholder can be edited but cannot be copied");
            dialogs.Value = new RegistryValue(
                string.Empty, RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "stored"));
            var defaultEntries = tab.Entries;
            var defaultRow = Row("(Default)");
            var defaultHash = defaultRow.GetHashCode();
            var defaultSelection = new HashSet<RegistryEntry> { defaultRow };
            var firstEntry = Row("First");
            await Run(tab.ActivateEntryCommand);
            check(
                tab.SelectedEntry == Row("(Default)") && tab.SelectedEntry.Value?.DisplayData == "stored",
                "Editing the unset default keeps the stored default value selected");
            check(
                ReferenceEquals(tab.Entries, defaultEntries)
                && ReferenceEquals(Row("First"), firstEntry)
                && ReferenceEquals(Row("(Default)"), defaultRow)
                && !defaultRow.IsDefaultPlaceholder
                && defaultRow.GetHashCode() == defaultHash
                && defaultSelection.Contains(defaultRow),
                "Editing the default updates its placeholder in place with a stable hash and selection identity");
            var modified = setup.ReadKeyDetails(fixture, false).LastWriteTimeUtc;
            check(
                tab.Entries.Where(entry => !entry.IsKey && !entry.IsParent).All(entry => entry.LastWriteTimeUtc == modified),
                "An in-place edit updates the shared containing-key timestamp on existing value rows");
            tab.SelectedEntry = Row("(Default)");
            check(
                Enabled(tab.DeleteCommand) && !Enabled(tab.RenameCommand) && tab.CopyCommand.CanExecute(null),
                "A stored unnamed value can be copied and deleted and keeps its empty name");
            dialogs.Accept = false;
            await Run(tab.DeleteCommand);
            check(
                setup.ReadValue(fixture, string.Empty) is not null && dialogs.Message.Contains(fixture.Address),
                "Canceling the confirmed target leaves the stored default intact");
            dialogs.Accept = true;
            await Run(tab.DeleteCommand);
            check(
                Row("(Default)").IsDefaultPlaceholder && setup.ReadValue(fixture, string.Empty) is null,
                "Deleting the default removes only its stored value");

            check(
                treeRefreshes == 0 && branches.Count == 0 && tab.SelectedEntries.Count == 0,
                "Deleting a value clears the list selection without touching the registry tree");

            dialogs.Value = new RegistryValue("NewValue", RegistryValueType.Binary, [0, 255, 7]);
            await tab.CreateValueCommand.ExecuteAsync("3");
            tab.SelectedEntry = Row("NewValue");
            dialogs.Value = new RegistryValue("NewValue", RegistryValueType.Binary, [0, 255, 42]);
            var binaryEntries = tab.Entries;
            var binaryRow = Row("NewValue");
            var binaryHash = binaryRow.GetHashCode();
            var binarySelection = new HashSet<RegistryEntry> { binaryRow };
            var binaryLookup = new Dictionary<RegistryEntry, string> { [binaryRow] = "selected" };
            var oldPreview = binaryRow.DataPreview;
            var oldAccessibleName = binaryRow.AccessibleName;
            tab.CopyCommand.Execute(null);
            await tab.PendingPreview;
            var previousInspector = tab.Inspector;
            var selectionChanges = 0;
            var wrongInspector = false;

            void ObserveEdit(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(tab.SelectedEntries))
                {
                    selectionChanges++;
                }

                if (args.PropertyName == nameof(tab.Inspector) && tab.Inspector?.Name != "NewValue")
                {
                    wrongInspector = true;
                }
            }

            tab.PropertyChanged += ObserveEdit;
            var binarySiblings = tab.Entries.Where(entry => entry.Name != "NewValue").ToArray();
            var editChanges = new List<NotifyCollectionChangedAction>();
            binaryEntries.CollectionChanged += (_, args) => editChanges.Add(args.Action);
            await Run(tab.EditBinaryCommand);
            await tab.PendingPreview;
            tab.PropertyChanged -= ObserveEdit;
            check(
                dialogs.Binary && setup.ReadValue(fixture, "NewValue")!.Data.SequenceEqual(new byte[] { 0, 255, 42 }),
                "Binary editing keeps the bytes and writes through the registry service");
            check(tab.SelectedEntry == Row("NewValue"), "Binary editing keeps the updated value selected");
            check(
                ReferenceEquals(tab.Entries, binaryEntries)
                && ReferenceEquals(Row("NewValue"), binaryRow)
                && editChanges.Count == 0
                && binarySiblings.All(entry => tab.Entries.Any(current => ReferenceEquals(current, entry))),
                "Binary editing updates the existing row without any collection changes");
            check(
                selectionChanges == 0
                && !wrongInspector
                && tab.Inspector?.RawBytes == "00FF2A"
                && binaryRow.DataPreview != oldPreview
                && binaryRow.AccessibleName != oldAccessibleName,
                "Editing keeps selection and inspector on the value while updating its cached presentation");
            check(
                binaryRow.GetHashCode() == binaryHash
                && binarySelection.Contains(binaryRow)
                && binaryLookup.TryGetValue(binaryRow, out var lookup)
                && lookup == "selected"
                && previousInspector?.RawBytes == "00FF07",
                "An in-place edit preserves hash set and dictionary lookups while the old inspector keeps its snapshot");
            check(
                clipboard.Entry?.Value?.Data.SequenceEqual(new byte[] { 0, 255, 7 }) == true,
                "Editing a copied row keeps the clipboard's original value bytes");
            tab.Filter = "NewValue";
            await tab.SortCommand.ExecuteAsync(RegistrySortColumn.Data);
            tab.SelectedEntry = Row("NewValue");
            dialogs.Value = new RegistryValue("NewValue", RegistryValueType.Binary, [0, 255, 43]);
            await Run(tab.EditValueCommand);
            check(
                tab.SelectedEntry == Row("NewValue")
                && tab.SelectedEntry.Value!.Data.SequenceEqual(new byte[] { 0, 255, 43 })
                && tab.Filter == "NewValue"
                && tab.SortColumn == RegistrySortColumn.Data
                && !tab.SortDescending, "Normal value editing keeps the updated row selected with the current filter and sort");
            tab.Filter = string.Empty;
            var sortedEntries = tab.Entries;
            var sortedSiblings = tab.Entries.Where(entry => entry.Name != "NewValue").ToArray();
            tab.SelectedEntry = Row("NewValue");
            var sortedRow = tab.SelectedEntry;
            sortedEntries.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Move)
                {
                    check(tab.IsUpdatingValue, "A sorted row moves inside the guarded value update");
                }
            };
            dialogs.Value = new RegistryValue("NewValue", RegistryValueType.Binary, [255]);
            selectionChanges = 0;
            wrongInspector = false;
            tab.PropertyChanged += ObserveEdit;
            await Run(tab.EditValueCommand);
            await tab.PendingPreview;
            tab.PropertyChanged -= ObserveEdit;
            check(
                ReferenceEquals(tab.Entries, sortedEntries)
                && tab.Entries[^1].Name == "NewValue"
                && ReferenceEquals(tab.SelectedEntry, sortedRow)
                && !tab.IsUpdatingValue
                && selectionChanges == 0
                && !wrongInspector
                && tab.Inspector?.RawBytes == "FF"
                && sortedSiblings.All(entry => tab.Entries.Any(current => ReferenceEquals(current, entry))),
                "Editing data moves only the changed row without changing selection or flashing the key inspector");
            tab.Filter = "FF";
            await tab.PendingFilter;
            var filteredEntries = tab.Entries;
            tab.SelectedEntry = Row("NewValue");
            dialogs.Value = new RegistryValue("NewValue", RegistryValueType.Binary, [3]);
            await Run(tab.EditValueCommand);
            check(
                ReferenceEquals(tab.Entries, filteredEntries)
                && tab.Entries.All(entry => entry.IsParent)
                && !tab.HasSelection
                && tab.Status.EndsWith("0 shown", StringComparison.Ordinal),
                $"An edit that stops matching the filter removes only that row and clears its selection "
                + $"(same list: {ReferenceEquals(tab.Entries, filteredEntries)}, rows: {string.Join(", ", tab.Entries.Select(entry => entry.Name))}, "
                + $"selected: {tab.SelectedEntry?.Name}, status: {tab.Status})");
            tab.Filter = string.Empty;
            tab.SelectedEntry = Row("NewValue");
            dialogs.Value = new RegistryValue("NewValue", RegistryValueType.Binary, [4]);
            dialogs.Before = () =>
            {
                tab.Filter = "04";
                return Task.CompletedTask;
            };
            await Run(tab.EditValueCommand);
            check(
                tab.PendingFilter.IsCompleted
                && tab.Entries.Where(entry => !entry.IsParent).ToArray() is [{ Name: "NewValue" }]
                && !tab.HasSelection,
                "An edit waits for a pending filter and inserts only the newly matching row without restoring an old selection");
            tab.Filter = string.Empty;
            tab.SelectedEntry = Row("NewValue");
            dialogs.Name = "RenamedValue";
            dialogs.Before = async () =>
            {
                check(
                    !tab.DeleteCommand.CanExecute(null) && !tab.EditValueCommand.CanExecute(null),
                    "An open edit dialog disables other edit commands");
                var prompts = dialogs.Count;
                await Run(tab.DeleteCommand);
                check(prompts == dialogs.Count, "Calling a disabled command cannot open a second dialog");
                tab.SelectedEntry = Row("Second");
            };
            await Run(tab.RenameCommand);
            check(
                setup.ReadValue(fixture, "RenamedValue") is not null && setup.ReadValue(fixture, "Second") is not null,
                "Rename keeps its original target when selection changes in the dialog");

            var other = new BrowserViewModel(service, child, clipboard);
            other.EditPromptRequested += dialogs.ShowAsync;
            other.TreeBranchChanged += BranchChanged;
            tab.SelectedEntry = Row("RenamedValue");
            var copied = tab.SelectedEntry;
            var pasteNotifications = 0;
            other.PasteCommand.CanExecuteChanged += (_, _) => pasteNotifications++;
            tab.CopyCommand.Execute(null);
            check(pasteNotifications > 0 && other.PasteCommand.CanExecute(null), "Copy notifies the Paste binding in another tab");
            dialogs.Name = "Pasted";
            await Run(other.PasteCommand);
            check(branches.Count == 0, "Value edits and value paste leave every tree branch alone");
            check(
                copied?.Value is not null && setup.ReadValue(child, "Pasted")!.Data.SequenceEqual(copied.Value.Data),
                "A second tab pastes the exact copied registry bytes into its own key");
            tab.SelectedEntry = Row("Child");
            tab.CopyCommand.Execute(null);
            dialogs.Name = "Cloned";
            await Run(tab.PasteCommand);
            check(branches.SequenceEqual([(fixture, null)]), "Key paste refreshes only the destination parent");
            check(
                setup.ReadValue(fixture.Child("Cloned"), "Pasted")!.Data.SequenceEqual(copied!.Value!.Data),
                "Pasting a copied key preserves its contents");
            tab.SelectedEntry = Row("Cloned");
            tab.CopyCommand.Execute(null);
            dialogs.Name = "NestedCopy";
            await Run(other.PasteCommand);
            check(branches.Count == 2 && branches[^1] == (child, null), "Cross-tab key paste refreshes the destination tab's branch");

            tab.SelectedEntry = Row("RenamedValue");
            var old = tab.SelectedEntry.Value;
            dialogs.Before = () =>
            {
                setup.Write(fixture, new RegistryValue("RenamedValue", RegistryValueType.Binary, [98, 97]), old);
                return Task.CompletedTask;
            };
            dialogs.Value = new RegistryValue("RenamedValue", RegistryValueType.Binary, [8]);
            var conflictEntries = tab.Entries;
            var conflictSibling = Row("First");
            await Run(tab.EditBinaryCommand);
            check(
                Row("RenamedValue").Value!.Data.SequenceEqual(new byte[] { 98, 97 })
                && ReferenceEquals(tab.Entries, conflictEntries)
                && ReferenceEquals(Row("First"), conflictSibling),
                "A conflicting edit reads back only the changed row and preserves its newer bytes without rebuilding the list");

            var removedValue = new RegistryValue("RemovedWhileEditing", RegistryValueType.Binary, [1]);
            setup.Write(fixture, removedValue, null);
            await tab.RefreshCommand.ExecuteAsync(null);
            tab.SelectedEntry = Row(removedValue.Name);
            var removedEntries = tab.Entries;
            dialogs.Value = removedValue with { Data = [2] };
            dialogs.Before = () =>
            {
                setup.DeleteValue(fixture, removedValue);
                return Task.CompletedTask;
            };
            await Run(tab.EditValueCommand);
            check(
                ReferenceEquals(tab.Entries, removedEntries)
                && !tab.Entries.Any(entry => entry.Name == removedValue.Name)
                && !tab.HasSelection
                && setup.ReadValue(fixture, removedValue.Name) is null,
                "A value deleted while its editor is open is removed in place without recreating it");

            var first = Row("First");
            var second = Row("Second");
            tab.SetSelection([Row(".."), first, second, Row("(Default)")]);
            check(
                Enabled(tab.DeleteCommand) && !Enabled(tab.RenameCommand) && !Enabled(tab.ActivateEntryCommand),
                "Multi-selection enables batch delete and disables single-item commands");
            var previous = invalidations;
            dialogs.Before = () =>
            {
                tab.SelectedEntry = Row("Child");
                setup.Write(fixture, new RegistryValue("Second", RegistryValueType.Binary, [99]), second.Value);
                return Task.CompletedTask;
            };
            await Run(tab.DeleteCommand);
            check(
                tab.Error?.Contains("Deleted 1 of 2") == true
                && dialogs.Title == "Delete 2 items?"
                && !dialogs.Message.Contains("..")
                && setup.ReadValue(fixture, "First") is null
                && Row("Second").Value!.Data[0] == 99
                && invalidations == previous + 1
                && !tab.HasSelection
                && journal.ReadEntries().Any(e => e.Operation == "Delete value" && e.Error is not null),
                "Partial delete reports the completed count, skips synthetic rows, refreshes and journals the stale write");

            dialogs.Name = "Sibling";
            tab.SelectedEntry = Row("Second");
            await tab.RenameTreeKeyCommand.ExecuteAsync(child);
            check(
                branches.Count == 3 && branches[^1] == (fixture, ("Child", "Sibling")),
                "Tree rename identifies both names within their parent");
            check(
                tab.Location == fixture && setup.SubKeys(fixture).Contains("Sibling") && Row("Second").Value!.Data[0] == 99,
                "Tree rename targets the invoked node independently of the selected list value");
            tab.SelectedEntry = Row("Cloned");
            await Run(tab.DeleteCommand);
            check(branches.Count == 4 && branches[^1] == (fixture, null), "Deleting a selected key refreshes just its parent");
            check(
                !setup.SubKeys(fixture).Contains("Cloned") && tab.Location == fixture, "Deleting a selected child leaves its parent open");
            dialogs.Name = "Created";
            await Run(tab.CreateKeyCommand);
            check(branches.Count == 5 && branches[^1] == (fixture, null), "Creating a key refreshes its captured parent once");
            check(tab.Location == fixture.Child("Created"), "Creating a key opens the new child");
            dialogs.Name = "Moved";
            await tab.RenameTreeKeyCommand.ExecuteAsync(tab.Location);
            check(tab.Location == fixture.Child("Moved"), "Renaming the displayed tree node opens its new path");
            await tab.DeleteTreeKeyCommand.ExecuteAsync(tab.Location);
            check(
                tab.Location == fixture && !setup.SubKeys(fixture).Contains("Moved"), "Deleting the displayed tree node opens its parent");

            dialogs.Name = "Sibling";
            dialogs.RetryName = "AfterConflict";
            await Run(tab.CreateKeyCommand);
            check(
                dialogs.HadError && tab.Location == fixture.Child("AfterConflict"),
                "Correcting a conflicting create name opens the new key after the failure refresh");
            await tab.NavigateAsync(fixture);
            dialogs.Name = "WhileAway";
            dialogs.Before = () => tab.NavigateAsync(fixture.Child("Sibling"));
            await Run(tab.CreateKeyCommand);
            check(
                tab.Location == fixture.Child("Sibling") && setup.SubKeys(fixture).Contains("WhileAway"),
                "A pending edit honors its original registry target and preserves a newer navigation");
            check(branches[^1] == (fixture, null), "Navigating while creating a key does not redirect its tree refresh");
            var siblingKey = fixture.Child("Sibling");
            setup.Write(fixture, new RegistryValue("NavigationEdit", RegistryValueType.Binary, [1]), null);
            setup.Write(siblingKey, new RegistryValue("NavigationEdit", RegistryValueType.Binary, [2]), null);
            await tab.NavigateAsync(fixture);
            tab.SelectedEntry = Row("NavigationEdit");
            dialogs.Value = new RegistryValue("NavigationEdit", RegistryValueType.Binary, [3]);
            dialogs.Before = () => tab.NavigateAsync(siblingKey);
            await Run(tab.EditValueCommand);
            check(
                tab.Location == siblingKey
                && !tab.HasSelection
                && Row("NavigationEdit").Value!.Data.SequenceEqual(new byte[] { 2 })
                && setup.ReadValue(fixture, "NavigationEdit")!.Data.SequenceEqual(new byte[] { 3 }),
                "A value edit does not select a same-named value after navigating to another key");
            setup.CreateKey(fixture, "RemoveFirst");
            setup.CreateKey(fixture, "RemoveSecond");
            await tab.NavigateAsync(fixture);
            tab.SetSelection([Row("RemoveFirst"), Row("RemoveSecond")]);
            dialogs.Before = () =>
            {
                setup.DeleteKey(fixture.Child("RemoveSecond"));
                return Task.CompletedTask;
            };
            int beforeDelete = branches.Count;
            await Run(tab.DeleteCommand);
            check(
                tab.Error?.Contains("Deleted 1 of 2") == true
                && branches.Count == beforeDelete + 1
                && branches[^1] == (fixture, null)
                && treeRefreshes == 0, "A partial key deletion reconciles its parent once and key edits never rebuild the tree");
            var ui = new ConcurrentExclusiveSchedulerPair();
            await Task.Factory.StartNew(
                async () =>
                {
                    tab.SelectedEntry = Row("Sibling");
                    var firstOpen = tab.ActivateEntryCommand;
                    var pending = firstOpen.ExecuteAsync(null);
                    tab.SelectedEntry = Row("WhileAway");
                    check(
                        firstOpen.IsRunning
                        && ReferenceEquals(firstOpen, tab.NavigateEntryCommand)
                        && tab.ActivateEntryCommand.CanExecute(null),
                        "A second key activation is enabled while the first is still running");
                    await Task.WhenAll(pending, tab.ActivateEntryCommand.ExecuteAsync(null));
                }, CancellationToken.None, TaskCreationOptions.None, ui.ExclusiveScheduler).Unwrap();
            ui.Complete();
            check(tab.Location == fixture.Child("WhileAway"), "The latest activated key wins");
            foreach (var root in new[] { software.Parent!, software.GetComputer() })
            {
                check(
                    !tab.DeleteTreeKeyCommand.CanExecute(root) && !tab.RenameTreeKeyCommand.CanExecute(root),
                    root.Name + " cannot be renamed or deleted by a tree shortcut");
            }
        }
        finally
        {
            setup.DeleteKey(fixture);
            folder.Delete(true);
        }
    }

    private sealed class Dialogs
    {
        internal int Count;
        internal string Title = string.Empty, Message = string.Empty, Name = string.Empty;
        internal bool Accept = true, Binary, HadError;
        internal string? RetryName;
        internal RegistryValue Value = new(string.Empty, RegistryValueType.Binary, []);
        internal Func<Task>? Before;

        internal async Task<bool> ShowAsync(RegistryEditPrompt prompt)
        {
            this.Count++;
            if (prompt.Error is not null)
            {
                if (this.RetryName is null)
                {
                    return false;
                }

                this.HadError = true;
                this.Name = this.RetryName;
                this.RetryName = null;
            }

            if (this.Before is { } before)
            {
                this.Before = null;
                await before();
            }

            switch (prompt)
            {
                case RegistryNamePrompt name:
                    name.Name = this.Name;
                    break;
                case RegistryValueEditorViewModel editor:
                    editor.Name = this.Value.Name;
                    editor.Data = this.Value.EditText(true, editor.IsRaw);
                    this.Binary = editor.IsRaw;
                    break;
                case RegistryDeletePrompt delete:
                    this.Title = delete.Title;
                    this.Message = delete.Message;
                    break;
            }

            return this.Accept;
        }
    }
}
