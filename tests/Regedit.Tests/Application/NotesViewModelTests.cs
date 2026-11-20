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
            using var notes = NotesStoreTests.CreateStore(settings, () => settings.Save(failSave ? blocked.FullName : file));

            var opened = new List<(RegistryLocation Location, bool NewTab)>();

            Task Navigate(RegistryLocation location, bool newTab)
            {
                opened.Add((location, newTab));
                return Task.CompletedTask;
            }

            var background = new List<RegistryLocation>();
            var copied = new List<string>();
            var confirmDelete = true;
            var panel = new NotesViewModel(notes, Navigate, background.Add, true, copied.Add, _ => Task.FromResult(confirmDelete));
            var tab = new NotesViewModel(notes, Navigate, background.Add, false, copied.Add, _ => Task.FromResult(confirmDelete));
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
            tab.UseCurrentKeyCommand.Execute(null);
            check(ReferenceEquals(panel.Selected, tab.Selected), "Note for current key explicitly changes the standalone selection");
            tab.Selected = notes.Get(key32);
            panel.Selected = tab.Selected;
            check(
                panel.Selected?.Matches(key32) == true && tab.Selected?.Matches(key32) == true,
                "A panel/tab handoff shares the selected draft and preserves its registry view");
            await tab.OpenCommand.ExecuteAsync(null);
            tab.OpenBackgroundCommand.Execute(tab.Selected);
            check(
                opened.SequenceEqual([(key32, true)]) && background.SequenceEqual([key32]),
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
                !panel.ShowOpenKey && !panel.ShowUseCurrentKey && tab.ShowOpenKey && tab.ShowUseCurrentKey,
                "Only the following panel hides redundant note navigation actions");
            panel.Selected = notes.Get(key32);
            check(panel.ShowOpenKey && panel.ShowUseCurrentKey, "A detached panel keeps its route back to the note and current key");
            var empty = notes.Get(key32.Child("Empty"));
            tab.Selected = empty;
            check(
                tab.Status == "No note for this key" && !tab.DeleteCommand.CanExecute(null),
                "An untouched key has an honest empty state and no Delete action");
            tab.Text = "New note " + new string('x', 300) + " searchable tail";
            var row = tab.VisibleRows(notes.Notes, "searchable tail").Single();
            check(
                row.Time == "Unsaved" && row.Status == "Unsaved changes" && row.Subtitle.Length < tab.Text.Length,
                "Notes show a concise preview and unsaved label while searching their entire text");
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
            var guarded = new NotesViewModel(notes, Navigate, background.Add, false, copied.Add, _ => confirmation.Task)
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
