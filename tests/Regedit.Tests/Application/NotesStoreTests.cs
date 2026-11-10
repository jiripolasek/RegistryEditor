// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class NotesStoreTests
{
    internal static NotesStore CreateStore(AppSettings settings, Action? save = null)
    {
        return new NotesStore(settings, save ?? (() => { }), token => Task.Delay(Timeout.Infinite, token));
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditAutosave.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(path);
            var delays = new List<TaskCompletionSource>();
            var saves = 0;
            var failSave = false;
            using var notes = new NotesStore(
                settings, () =>
                {
                    if (failSave)
                    {
                        throw new IOException("Test autosave failure");
                    }

                    settings.Save(path);
                    saves++;
                }, token =>
                {
                    var due = new TaskCompletionSource();
                    delays.Add(due);
                    return due.Task.WaitAsync(token);
                });
            var key = RegistryLocation.Parse("HKCU\\Software\\Autosave", RegistryView.Registry64);
            var note = notes.Get(key);
            check(
                ReferenceEquals(note, notes.Get(key with { SubKey = key.SubKey.ToUpperInvariant() })) &&
                !ReferenceEquals(note, notes.Get(key with { View = RegistryView.Registry32 })),
                "Notes share canonical drafts while preserving exact registry views");
            var sawSaving = false;
            note.PropertyChanged += (_, _) => sawSaving |= note.IsSaving;
            note.Text = "First edit";
            note.Text = "Latest edit: 日本語";
            check(
                saves == 0 && delays.Count == 2 && note.IsDirty,
                "Typing debounces note persistence instead of saving every keystroke");
            delays[0].SetResult();
            delays[1].SetResult();
            await Until(() => !note.IsDirty);
            check(
                saves == 1 && sawSaving && !note.IsSaving && AppSettings.Load(path).Notes.Single().Text == note.Text,
                "Only the latest pending autosave runs and saved state follows a successful write");

            note.Text = string.Empty;
            notes.Save(note);
            check(
                note.CanUndoDelete && !note.IsDirty && AppSettings.Load(path).Notes.Count == 0,
                "Autosaving a cleared note removes it while retaining the text for Undo");
            notes.UndoDelete(note);
            check(
                note.Text == "Latest edit: 日本語" && !note.IsDirty && !note.CanUndoDelete &&
                AppSettings.Load(path).Notes.Single().Text == note.Text,
                "Undo clearing restores the full note and persists it immediately");

            var savedText = note.Text;
            var failures = 0;
            notes.SaveFailed += (_, _) => failures++;
            failSave = true;
            note.Text = "Retained failed draft";
            delays[^1].SetResult();
            await Until(() => note.SaveError is not null && failures == 1);
            check(
                note.IsDirty && note.Text == "Retained failed draft" && failures == 1 &&
                AppSettings.Load(path).Notes.Single().Text == savedText && settings.Notes.Single().Text == savedText,
                "An autosave failure preserves the draft, rolls back settings and reports the error");
            failSave = false;
            check(
                notes.TrySave(note) && !note.IsDirty && note.SaveError is null &&
                AppSettings.Load(path).Notes.Single().Text == "Retained failed draft",
                "Save now retries a failed autosave without losing text");

            failSave = true;
            try
            {
                notes.Delete(note);
                throw new InvalidOperationException("Expected Delete to fail");
            }
            catch (IOException)
            {
                check(
                    note.Text == "Retained failed draft" && !note.IsDirty &&
                    AppSettings.Load(path).Notes.Single().Text == note.Text,
                    "Failed explicit deletion preserves both the complete draft and stored note");
            }

            failSave = false;
            notes.Delete(note);
            check(
                !note.HasContent && note.CanUndoDelete && AppSettings.Load(path).Notes.Count == 0,
                "Explicit deletion is persisted before replacing the editor text");
            notes.UndoDelete(note);

            note.Text = "Flush before switching or closing";
            var dueBeforeFlush = delays[^1];
            notes.Pause();
            var beforeFlush = saves;
            check(
                notes.Flush() && !note.IsDirty && saves == beforeFlush + 1,
                "Window closure flushes pending changes without waiting for the debounce");
            dueBeforeFlush.SetResult();
            note.Text = "Edits during a pending close";
            var pausedDelays = delays.Count;
            notes.Resume();
            check(
                delays.Count == pausedDelays + 1 && note.IsDirty,
                "Canceling closure resumes autosave for edits made while it was paused");
            var dueBeforeDispose = delays[^1];
            notes.Dispose();
            dueBeforeDispose.SetResult();
            await Task.Delay(20);
            check(
                saves == beforeFlush + 1 && note.IsDirty && AppSettings.Load(path).Notes.Single().Text ==
                "Flush before switching or closing",
                "Disposed window scopes cancel pending writes and cannot save a discarded draft later");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The scheduled note save did not finish.");
            }

            await Task.Delay(5);
        }
    }
}
