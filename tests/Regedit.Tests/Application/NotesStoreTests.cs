// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class NotesStoreTests
{
    internal static NotesStore CreateStore(AppSettings settings, Action<NoteScopeMove?>? save = null)
    {
        return new NotesStore(settings, save ?? (_ => { }), token => Task.Delay(Timeout.Infinite, token));
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
                settings, move =>
                {
                    if (failSave)
                    {
                        throw new IOException("Test autosave failure");
                    }

                    settings.Save(path, move);
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
                ReferenceEquals(note, notes.Get(key with { SubKey = key.SubKey.ToUpperInvariant() }))
                && !ReferenceEquals(note, notes.Get(key with { View = RegistryView.Registry32 })),
                "Notes share canonical drafts while preserving exact registry views");
            var sawSaving = false;
            note.PropertyChanged += (_, _) => sawSaving |= note.IsSaving;
            note.Text = "First edit";
            note.Text = "Latest edit: 日本語";
            check(saves == 0 && delays.Count == 2 && note.IsDirty, "Typing debounces note persistence instead of saving every keystroke");
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
                note.Text == "Latest edit: 日本語"
                && !note.IsDirty
                && !note.CanUndoDelete
                && AppSettings.Load(path).Notes.Single().Text == note.Text,
                "Undo clearing restores the full note and persists it immediately");

            var savedText = note.Text;
            var failures = 0;
            notes.SaveFailed += (_, _) => failures++;
            failSave = true;
            note.Text = "Retained failed draft";
            delays[^1].SetResult();
            await Until(() => note.SaveError is not null && failures == 1);
            check(
                note.IsDirty
                && note.Text == "Retained failed draft"
                && failures == 1
                && AppSettings.Load(path).Notes.Single().Text == savedText
                && settings.Notes.Single().Text == savedText,
                "An autosave failure preserves the draft, rolls back settings and reports the error");
            failSave = false;
            check(
                notes.TrySave(note)
                && !note.IsDirty
                && note.SaveError is null
                && AppSettings.Load(path).Notes.Single().Text == "Retained failed draft",
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
                    note.Text == "Retained failed draft" && !note.IsDirty && AppSettings.Load(path).Notes.Single().Text == note.Text,
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
                delays.Count == pausedDelays + 1 && note.IsDirty, "Canceling closure resumes autosave for edits made while it was paused");
            var dueBeforeDispose = delays[^1];
            notes.Dispose();
            dueBeforeDispose.SetResult();
            await Task.Delay(20);
            check(
                saves == beforeFlush + 1
                && note.IsDirty
                && AppSettings.Load(path).Notes.Single().Text == "Flush before switching or closing",
                "Disposed window scopes cancel pending writes and cannot save a discarded draft later");

            CheckValueNotes(Path.Combine(directory.FullName, "values.json"), check);
            CheckConcurrentScopeChanges(directory.FullName, check);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static void CheckValueNotes(string path, Action<bool, string> check)
    {
        var root = RegistryLocation.Parse(@"HKLM\Software\Notes");
        var child = root.Child("Profile");
        var key = new KeyNote(root, "Legacy key note");
        var valueTarget = new KeyNote(root, string.Empty) { ValueName = "BatchSize" };
        var value = new RegistryValue("batchsize", RegistryValueType.DWord, new byte[4]);
        var settings = new AppSettings { Notes = [key] };
        settings.Save(path);
        var initial = AppSettings.Load(path);
        var second = AppSettings.Load(path);
        using var store = CreateStore(initial, move => initial.Save(path, move));
        using var concurrent = CreateStore(second, move => second.Save(path, move));
        var first = store.Get(valueTarget);
        first.Text = "Use 1024 when testing.";
        store.Save(first);
        var unnamed = concurrent.Get(valueTarget with { ValueName = "" });
        unnamed.Text = "Unnamed value note.";
        concurrent.Save(unnamed);
        check(AppSettings.Load(path).Notes.Count == 3 &&
              ReferenceEquals(first, store.Get(valueTarget with { ValueName = "BATCHSIZE" })) &&
              store.Get(root).Text == key.Text,
            "Concurrent notes for a key, a named value and the unnamed value keep distinct identities");

        var rule = valueTarget with { MatchRule = new ValueNoteRule(RegistryValueType.DWord, 4) };
        store.ChangeScope(first, rule);
        var specific = store.Get(valueTarget with { Location = child });
        specific.Text = "Local profile exception.";
        store.Save(specific);
        var saved = AppSettings.Load(path).Notes;
        check(saved.Count == 4 && saved.Any(note => note.HasSameTarget(rule)) &&
              saved.All(note => !note.HasSameTarget(valueTarget)),
            "Saving a scope change atomically replaces the direct note and preserves unrelated notes");
        check(store.ForValue(child, value.Name, value).SequenceEqual([specific, first]) &&
              store.ForValue(root, value.Name, value).Single() == first &&
              store.SavedAt(child).ForValue(value)?.HasSameTarget(specific.Target) == true &&
              store.SavedAt(root).ForValue(value)?.HasSameTarget(first.Target) == true,
            "Saved notes and drafts rank exact matches ahead of branch rules");
        check(!rule.MatchesValue(root with { SubKey = root.SubKey + "Extra" }, value.Name, value) &&
              !rule.MatchesValue(child with { Machine = "SERVER" }, value.Name, value) &&
              !rule.MatchesValue(child with { View = RegistryView.Registry32 }, value.Name, value) &&
              !rule.MatchesValue(child, value.Name, value with { Type = RegistryValueType.Binary }) &&
              !rule.MatchesValue(child, value.Name, value with { Data = new byte[8] }) &&
              !rule.MatchesValue(child, value.Name, null),
            "Branch rules require the same source and view, a full path segment, type and byte length");

        using var failing = CreateStore(AppSettings.Load(path), _ => throw new IOException("Test scope failure"));
        var original = failing.Get(rule);
        try
        {
            failing.ChangeScope(original, rule with { Location = child });
            throw new InvalidOperationException("Expected scope persistence to fail.");
        }
        catch (IOException)
        {
            check(original.Target.HasSameTarget(rule) && original.HasContent && original.SaveError is null &&
                  AppSettings.Load(path).Notes.Any(note => note.HasSameTarget(rule)),
                "A scope save failure keeps the original rule and text without setting the draft save error");
        }

        foreach (var target in new[]
                 {
                     rule with { Location = root with { Machine = "SERVER" } },
                     rule with { Location = root with { View = RegistryView.Registry32 } },
                     rule with { ValueName = "DifferentValue" }
                 })
        {
            try
            {
                store.ChangeScope(first, target);
                throw new InvalidOperationException("Expected the scope change to be rejected.");
            }
            catch (ArgumentException)
            {
                check(first.Target.HasSameTarget(rule) &&
                      AppSettings.Load(path).Notes.Any(note => note.HasSameTarget(rule)),
                    "Scope changes preserve the original computer, registry view, and value name");
            }
        }

        store.Delete(specific);
        try
        {
            store.ChangeScope(first, valueTarget with { Location = child });
            throw new InvalidOperationException("Expected the pending undo to block the scope change.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("Undo Delete is available", StringComparison.Ordinal))
        {
            check(specific.CanUndoDelete && specific.Current == specific && first.Target.HasSameTarget(rule),
                "A scope move leaves the source and the destination's pending undo intact");
        }

        store.UndoDelete(specific);
        saved = AppSettings.Load(path).Notes;
        check(saved.Single(note => note.HasSameTarget(specific.Target)).Text == "Local profile exception." &&
              saved.Single(note => note.HasSameTarget(rule)).Text == first.Text,
            "Undo restores the destination note after a rejected scope move without touching the source");

        var blank = store.Get(valueTarget);
        var moved = store.ChangeScope(first, valueTarget);
        saved = AppSettings.Load(path).Notes;
        check(moved == first && blank.Current == first && store.Find(valueTarget) == first &&
              !store.Notes.Contains(blank) && first.Text == "Use 1024 when testing." &&
              saved.Single(note => note.HasSameTarget(valueTarget)).Text == first.Text &&
              saved.All(note => !note.HasSameTarget(rule)),
            "Moving into a blank target keeps the original draft and persists only its new scope");
    }

    private static void CheckConcurrentScopeChanges(string directory, Action<bool, string> check)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\NoteMoves");
        var source = new KeyNote(root, "Original text") { ValueName = "Counter" };
        var destination = source with { Location = root.Child("Local"), MatchRule = new ValueNoteRule(null, null) };

        VerifyConflict("move", (store, note) => store.ChangeScope(note, destination with { Location = root.Child("Other") }));
        VerifyConflict("delete", (store, note) => store.Delete(note));
        VerifyConflict("occupied destination", (store, _) =>
        {
            var other = store.Get(destination);
            other.Text = "Local edit";
            store.Save(other);
        });

        void VerifyConflict(string operation, Action<NotesStore, NoteDraft> change)
        {
            var path = Path.Combine(directory, operation + ".json");
            new AppSettings { Notes = [source] }.Save(path);
            var first = AppSettings.Load(path);
            var second = AppSettings.Load(path);
            using var writer = CreateStore(first, move => first.Save(path, move));
            using var stale = CreateStore(second, move => second.Save(path, move));
            var draft = stale.Get(source);
            draft.Text = "Local edit";
            change(writer, writer.Get(source));
            var saved = File.ReadAllText(path);
            try
            {
                stale.ChangeScope(draft, destination);
                throw new InvalidOperationException($"A concurrent {operation} should block the scope change.");
            }
            catch (IOException error) when (error.Message.Contains("Another window changed Notes", StringComparison.Ordinal))
            {
                check(File.ReadAllText(path) == saved && draft.Target.HasSameTarget(source) && draft.Text == "Local edit" && draft.IsDirty,
                    $"A concurrent {operation} keeps the saved notes and the unsaved draft at its original scope");
            }
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
