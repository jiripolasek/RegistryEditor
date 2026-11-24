// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class NotesViewModelTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditNotes.");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.json");
            var key64 = RegistryLocation.Parse(@"HKCU\Software", RegistryView.Registry64);
            var key32 = RegistryLocation.Parse(@"HKCU\Software", RegistryView.Registry32);
            var settings = new AppSettings { Notes = [new KeyNote(key64, "64-bit note"), new KeyNote(key32, "32-bit note")] };
            settings.Save(file);
            var failSave = false;
            var blocked = Directory.CreateDirectory(Path.Combine(directory.FullName, "blocked"));
            using var notes = NotesStoreTests.CreateStore(settings, move => settings.Save(failSave ? blocked.FullName : file, move));

            var opened = new List<(RegistryLocation Location, string? ValueName, bool NewTab)>();

            Task Navigate(RegistryLocation location, string? valueName, bool newTab)
            {
                opened.Add((location, valueName, newTab));
                return Task.CompletedTask;
            }

            var background = new List<RegistryLocation>();
            var copied = new List<string>();
            var confirmDelete = true;
            var panel = new NotesViewModel(
                notes, Navigate, (location, _) => background.Add(location), true, copied.Add, _ => Task.FromResult(confirmDelete));
            var tab = new NotesViewModel(
                notes, Navigate, (location, _) => background.Add(location), false, copied.Add, _ => Task.FromResult(confirmDelete));
            check(
                panel.Title == "Select a registry key" && !panel.Path.Contains("New note"),
                "An empty contextual sidebar guides registry navigation without offering collection-page actions");
            check(
                tab.Title == "Select a note" && tab.Path.Contains("New note"),
                "The standalone Notes collection retains its explicit note creation guidance");
            panel.CurrentLocation = key64;
            tab.CurrentLocation = key32;
            tab.Selected = notes.Get(key32);
            check(
                panel.Text == "64-bit note" && tab.Text == "32-bit note" && !ReferenceEquals(panel.Selected, tab.Selected),
                "Notes surfaces keep independent selection and exact registry views");
            var changes = new List<string?>();
            tab.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            tab.Text = "Changed 32-bit note";
            check(
                tab.SaveCommand.CanExecute(null)
                && tab.Status == "Unsaved changes"
                && changes.Count(name => name == nameof(NotesViewModel.Text)) == 1
                && !changes.Contains(nameof(NotesViewModel.Title))
                && !changes.Contains(nameof(NotesViewModel.Path)),
                "Typing notifies text and dirty state once without re-announcing selection details");
            panel.CurrentLocation = key32;
            check(
                ReferenceEquals(panel.Selected, tab.Selected) && panel.Text == tab.Text && ReferenceEquals(notes.Get(key32), tab.Selected),
                "Surfaces share the window-owned draft without copying editor state");
            var before = settings.Notes.ToArray();
            failSave = true;
            tab.SaveCommand.Execute(null);
            check(
                tab.Error is not null
                && panel.Error is not null
                && panel.Status == "Couldn’t save"
                && tab.Selected?.IsDirty == true
                && panel.SaveCommand.CanExecute(null)
                && settings.Notes.SequenceEqual(before),
                "A failed note save rolls back persisted state and leaves both shared editors retryable");
            failSave = false;
            tab.SaveCommand.Execute(null);
            check(
                tab.Error is null
                && !tab.SaveCommand.CanExecute(null)
                && !panel.SaveCommand.CanExecute(null)
                && tab.Status == "Saved"
                && panel.Status == "Saved"
                && AppSettings.Load(file).Notes.Single(note => note.Matches(key32)).Text == "Changed 32-bit note",
                "A successful retry updates every shared editor and persists the exact view");
            panel.CurrentLocation = key64;
            tab.CurrentLocation = key64;
            check(
                panel.Selected?.Matches(key64) == true && tab.Selected?.Matches(key32) == true,
                "The panel follows browser context while the standalone tab retains its selected note");
            check(!tab.UseCurrentKeyCommand.CanExecute(null) && !tab.ShowContextNavigation,
                "The standalone page offers no current-key navigation");
            tab.UseCurrentKeyCommand.Execute(null);
            check(tab.Selected?.Matches(key32) == true, "Current-key commands cannot change the standalone selection");
            check(tab.VisibleRows(notes.Notes, string.Empty).Count() == 2 &&
                  tab.VisibleRows(notes.Notes, "Changed 32-bit note").Any(row => ReferenceEquals(row.Source, tab.Selected)),
                "The organizer lists and searches notes outside the browser context");
            tab.Selected = notes.Get(key32);
            panel.Selected = tab.Selected;
            check(
                panel.Selected?.Matches(key32) == true && tab.Selected?.Matches(key32) == true,
                "A panel/tab handoff shares the selected draft and preserves its registry view");
            await tab.OpenCommand.ExecuteAsync(null);
            tab.OpenBackgroundCommand.Execute(tab.Selected);
            check(
                opened.SequenceEqual([(key32, null, true)]) && background.SequenceEqual([key32]),
                "Note Open and middle-click retain the selected note address and registry view");
            tab.Text = string.Empty;
            tab.SaveCommand.Execute(null);
            check(
                !tab.SaveCommand.CanExecute(null)
                && settings.Notes.Count == 1
                && settings.Notes[0].Matches(key64)
                && AppSettings.Load(file).Notes.All(note => !note.Matches(key32)),
                "Saving an empty note removes only its exact registry view");
            panel.Deactivate();
            var inactiveNotifications = 0;
            panel.PropertyChanged += (_, _) => inactiveNotifications++;
            tab.Text = "Draft while panel is hidden";
            check(inactiveNotifications == 0, "A hidden Notes surface detaches its draft notifications");
            panel.Activate();
            check(
                panel.Text == tab.Text && panel.Status == "Unsaved changes" && panel.SaveCommand.CanExecute(null),
                "A reactivated Notes surface observes current shared text and dirty state");
            panel.CurrentLocation = RegistryLocation.Parse("Computer");
            check(
                !panel.HasNote && !panel.OpenCommand.CanExecute(null) && !panel.UseCurrentKeyCommand.CanExecute(null),
                "Computer context clears the compact editor and disables key actions");
            panel.CurrentLocation = key64;
            check(
                !panel.ShowContextNavigation && !tab.ShowContextNavigation,
                "Current-key actions are absent from the full page and redundant in a following sidebar");
            panel.Selected = notes.Get(key32);
            check(panel.ShowContextNavigation, "A detached sidebar keeps its route back to the note and current key");
            panel.UseCurrentKeyCommand.Execute(null);
            check(panel.Selected?.Matches(key64) == true && !panel.ShowContextNavigation,
                "The sidebar can return to the current key");
            var empty = notes.Get(key32.Child("Empty"));
            tab.Selected = empty;
            check(
                tab.Status == "No note for this key" && !tab.DeleteCommand.CanExecute(null),
                "An untouched key has an honest empty state and no Delete action");
            tab.Text = "New note " + new string('x', 300) + " searchable tail";
            var row = tab.VisibleRows(notes.Notes, "searchable tail").Single();
            check(
                row.Time == "Unsaved" && row.Status == "Unsaved changes" && row.Subtitle.Length < tab.Text.Length &&
                row.NoteTextMatches?.Select(position => row.Subtitle[position]).SequenceEqual("searchable tail") == true,
                "Notes show an unsaved label and highlight a substring in the full text after shortening the preview");
            tab.CopyPathCommand.Execute(null);
            tab.CopyNoteCommand.Execute(null);
            check(
                copied.SequenceEqual([empty.Location.Address, tab.Text]),
                "Note copy commands produce a usable registry path and complete note text");
            confirmDelete = false;
            await tab.DeleteCommand.ExecuteAsync(null);
            check(empty.HasContent, "Canceling Delete preserves the note draft");
            confirmDelete = true;
            failSave = true;
            var beforeDelete = tab.Text;
            await tab.DeleteCommand.ExecuteAsync(null);
            check(
                tab.Text == beforeDelete && tab.Error is not null && empty.IsDirty,
                "Failed deletion leaves the original text and unsaved draft available");
            failSave = false;
            await tab.DeleteCommand.ExecuteAsync(null);
            check(
                !empty.HasContent && !empty.IsDirty && tab.CanUndoDelete && !settings.Notes.Any(note => note.Matches(empty.Location)),
                "Confirmed Delete persists removal and exposes Undo without losing the text");
            tab.UndoDeleteCommand.Execute(null);
            check(
                tab.Text == beforeDelete
                && !empty.IsDirty
                && !tab.CanUndoDelete
                && AppSettings.Load(file).Notes.Any(note => note.Matches(empty.Location) && note.Text == beforeDelete),
                "Undo restores and saves the complete deleted note");
            var confirmation = new TaskCompletionSource<bool>();
            var guarded = new NotesViewModel(notes, Navigate, (location, _) => background.Add(location), false, copied.Add, _ => confirmation.Task)
            {
                Selected = empty
            };
            var deleting = guarded.DeleteCommand.ExecuteAsync(null);
            tab.Text = "A newer edit while confirming";
            confirmation.SetResult(true);
            await deleting;
            check(
                tab.Text == "A newer edit while confirming" && guarded.Error is not null && empty.IsDirty,
                "A stale Delete confirmation cannot remove a newer shared draft");
            tab.Selected = notes.Get(key64);
            check(
                !empty.IsDirty
                && AppSettings.Load(file).Notes.Single(note => note.Matches(empty.Location)).Text == "A newer edit while confirming",
                "Switching notes flushes the previous shared draft immediately");
            var value = notes.Get(new KeyNote(key64, string.Empty) { ValueName = "BatchSize" });
            value.Text = "Number of events buffered per upload.";
            tab.Selected = value;
            var match = tab.VisibleRows(notes.Notes, "btchsz").Single();
            check(match.Source == value && match.NoteTitleMatches?.SequenceEqual([0, 2, 3, 4, 5, 7]) == true,
                "The organizer finds abbreviations and highlights the matching name characters");
            var bodyOnly = new NoteDraft(new KeyNote(key64, "Review uninstalled drivers for BatchSize.") { ValueName = "Drivers" });
            var view32 = new NoteDraft(key32, "Same text");
            var view64 = new NoteDraft(key64, "Same text");
            foreach (var (label, expected) in new[] { ("32-bit", view32), ("64-bit", view64) })
            {
                var viewRow = tab.VisibleRows([view32, view64], label).Single();
                check(viewRow.Source == expected && viewRow.NotePathMatches!.Select(i => viewRow.Path[i]).SequenceEqual(label),
                    $"The organizer filters and highlights the displayed {label} registry view");
            }

            var longPrefix = new string('a', 16_380);
            var longName = longPrefix + "B-C";
            var matcher = new FuzzyStringMatcher(longPrefix + "bc");
            var longMatch = matcher.Match(longName);
            check(longMatch.Score > 0 && longMatch.Positions.Length == longName.Length - 1 &&
                  longMatch.Positions[^2] == longName.Length - 3 && longMatch.Positions[^1] == longName.Length - 1 &&
                  matcher.Match(longPrefix + "C-B").Score == 0,
                "A long value name retains ordered fuzzy matches and their original highlight positions");

            const string word = "trace";
            var longTitle = "t-r-a-c-e".PadRight(14_000, 'x') + ".Trace";
            var contiguous = new NoteDraft(new KeyNote(key64, "Setting") { ValueName = longTitle });
            var partialRun = new NoteDraft(new KeyNote(key64, "Setting") { ValueName = "trac-e" });
            var ranked = tab.VisibleRows([partialRun, contiguous], word).ToArray();
            check(ranked.Select(item => item.Source).SequenceEqual([contiguous, partialRun]) &&
                  ranked[0].NoteTitleMatches!.SequenceEqual(Enumerable.Range(longTitle.Length - word.Length, word.Length)),
                "A contiguous word in a long value name outranks a shorter partial run and highlights the whole word");
            var wordMatcher = new FuzzyStringMatcher(word);
            check(wordMatcher.Match(longTitle).Score == wordMatcher.Match("x.Trace").Score,
                "The same word boundary and casing earn the same contiguous score on both scoring paths");

            var literal = new NoteDraft(new KeyNote(key64, "The scheduled RUN completed.") { ValueName = "Schedule" });
            var bodyMatches = tab.VisibleRows([bodyOnly, literal], "run").ToArray();
            check(bodyMatches.Length == 1 && bodyMatches[0].Source == literal &&
                  bodyMatches[0].NoteTextMatches?.Select(position => bodyMatches[0].Subtitle[position]).SequenceEqual("RUN") == true,
                "Body search highlights contiguous case-insensitive text and skips scattered letters");
            check(tab.VisibleRows([bodyOnly, value], "BatchSize").Select(row => row.Source).SequenceEqual([value, bodyOnly]),
                "A fuzzy name match ranks ahead of a body substring match");
            var accentedPath = RegistryLocation.Parse(@"HKCU\Software\CaféOptions");
            var pathNote = new NoteDraft(new KeyNote(accentedPath, "Empty preview.") { ValueName = "Telemetry" });
            var pathMatch = tab.VisibleRows([pathNote], "CAFÉopt").Single();
            check(!tab.VisibleRows([pathNote], "cafeopt").Any() &&
                  pathMatch.NotePathMatches?.Select(position => accentedPath.Address[position]).SequenceEqual("CaféOpt") == true,
                "Path matching preserves accents and highlights the original characters regardless of case");
            var accentedName = new NoteDraft(new KeyNote(key64, "Empty preview.") { ValueName = "CaféOptions" });
            check(!tab.VisibleRows([accentedName], "cafeopt").Any() &&
                  tab.VisibleRows([accentedName], "CAFÉopt").Single().Source == accentedName &&
                  new FuzzyStringMatcher("cafeopt", foldDiacritics: true).Match("CaféOptions").Score > 0,
                "Regedit preserves accents in value names and the matcher can still fold them when enabled");
            var crossSegment = new NoteDraft(new KeyNote(
                RegistryLocation.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer"),
                "Registry settings.") { ValueName = "Telemetry" });
            var pathOnly = new NoteDraft(new KeyNote(key64.Child("Runtime"), "Empty preview.") { ValueName = "Telemetry" });
            check(tab.VisibleRows([crossSegment, pathOnly, literal], "run").Select(row => row.Source)
                    .SequenceEqual([literal, pathOnly]),
                "A body hit ranks above a fuzzy path hit, and fuzzy letters cannot cross path segment boundaries");
            var qualified = tab.VisibleRows([pathNote], @"sftwr\caféopt").Single();
            check(qualified.NotePathMatches?.Select(position => accentedPath.Address[position]).SequenceEqual("SftwrCaféOpt") == true,
                "A qualified fuzzy query matches adjacent path segments and preserves their highlight offsets");
            var slashPath = new NoteDraft(new KeyNote(
                RegistryLocation.Parse(@"HKCU\Software\Café/Options"), "Empty preview.") { ValueName = "Telemetry" });
            var nestedPath = new NoteDraft(new KeyNote(
                RegistryLocation.Parse(@"HKCU\Software\Café\Options"), "Empty preview.") { ValueName = "Telemetry" });
            check(tab.VisibleRows([slashPath, nestedPath], "café/opt").Single().Source == slashPath &&
                  tab.VisibleRows([slashPath, nestedPath], @"café\opt").Single().Source == nestedPath,
                "Notes search treats forward slash as part of a key name and backslash as the path delimiter");
            var slashName = new NoteDraft(new KeyNote(key64, "Empty preview.") { ValueName = "Alpha/Beta" });
            check(tab.VisibleRows([slashName], @"Alpha\Beta").Count() == 0 &&
                  tab.VisibleRows([slashName], "Alpha/Beta").Single().Source == slashName &&
                  new FuzzyStringMatcher(@"Alpha\Beta", foldSlashes: true).Match("Alpha/Beta").Score > 0,
                "Regedit keeps slashes distinct in value names and the matcher can still fold them when enabled");
            await tab.OpenCommand.ExecuteAsync(null);
            check(opened[^1] == (key64, "BatchSize", true), "Open value carries the exact registry value name");
            tab.SaveCommand.Execute(null);
            var registryValue = new RegistryValue("BatchSize", RegistryValueType.DWord, [1, 0, 0, 0]);
            var editor = tab.CreateScopeEditor(key64, registryValue)!;
            var occupied = key64.Child("ExistingRule");
            var existingRule = notes.Get(value.Target with
            {
                Location = occupied, MatchRule = new ValueNoteRule(RegistryValueType.String, 10)
            });
            existingRule.Text = "A rule for string values in this branch.";
            notes.Save(existingRule);
            editor.Address = occupied.Address;
            check(!editor.CanSave && editor.Summary.Contains("A matching rule already exists", StringComparison.Ordinal) &&
                  editor.Save() is null,
                "A rule with different conditions for the same value and branch blocks Save in the scope preview");
            editor.Address = key64.Address;
            editor.ByteLength = "-1";
            check(!editor.CanSave && editor.Summary.Contains("nonnegative", StringComparison.Ordinal),
                "The scope editor rejects an invalid byte length before saving");
            var cultureBefore = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
                System.Globalization.CultureInfo.CurrentCulture = culture;
                editor.ByteLength = "1,024";
                check(editor.CanSave && editor.Summary.Contains("1,024 bytes", StringComparison.Ordinal),
                    "The byte-length editor accepts the same localized grouped number shown in its summary");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = cultureBefore;
            }

            editor.ByteLength = "4";
            check(editor.CanSave && editor.Summary.Contains("The selected value matches.", StringComparison.Ordinal),
                "The scope editor previews matching conditions for the inspected value");
            failSave = true;
            check(!tab.SaveScope(editor) && editor.HasError && tab.SaveState == NoteSaveState.Saved && tab.Error is null
                  && value.Target.MatchRule is null,
                "A failed scope save is reported in the scope editor while the original note remains saved");
            failSave = false;
            tab.Deactivate();
            editor.Address = key64.Child("Rules").Address;
            check(tab.SaveScope(editor) && !editor.HasError && value.Target.MatchRule is { ByteLength: 4 },
                "Retrying in the scope editor saves the selected branch and conditions");
            changes.Clear();
            tab.Activate();
            check(changes.Contains(nameof(NotesViewModel.SaveState)) && changes.Contains(nameof(NotesViewModel.Path))
                  && changes.Contains(nameof(NotesViewModel.NoteType)) && changes.Contains(nameof(NotesViewModel.MatchScope))
                  && tab.Path == key64.Child("Rules").Address && tab.NoteType == "Match rule",
                "Reactivating a Notes surface republishes the save state, path, and matching metadata");

            var exactTarget = value.Target with { MatchRule = null };
            var blank = notes.Get(exactTarget);
            panel.Selected = blank;
            var hiddenEditor = new NotesViewModel(notes, Navigate, (location, _) => background.Add(location),
                false, copied.Add, _ => Task.FromResult(true)) { Selected = blank };
            hiddenEditor.Deactivate();
            notes.ChangeScope(value, exactTarget);
            hiddenEditor.Activate();
            check(panel.Selected == value && tab.Selected == value && hiddenEditor.Selected == value &&
                  panel.Text == tab.Text && hiddenEditor.Text == tab.Text && tab.NoteType == "Value note",
                "Active and reopened editors on the blank destination follow the original draft to its new scope");
            hiddenEditor.Deactivate();

            notes.Pause();
            guarded.Text = "Discarded during window closure";
            guarded.Deactivate();
            check(
                empty.IsDirty
                && AppSettings.Load(file).Notes.Single(note => note.Matches(empty.Location)).Text == "A newer edit while confirming",
                "Unloading a Notes surface cannot save a draft discarded during window closure");
            panel.Deactivate();
            tab.Deactivate();
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
