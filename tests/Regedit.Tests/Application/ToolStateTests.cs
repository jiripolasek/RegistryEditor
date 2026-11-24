// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.AccessControl;
using System.Security.Principal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Win32;
using Registry = Microsoft.Win32.Registry;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ToolStateTests
{
    public static async Task RunAsync(Action<bool, string> check, bool integration)
    {
        var settings = new AppSettings();
        check(
            !settings.ShowKeysInList && !settings.ShowValuePreview && !settings.ToolPanePinned,
            "New settings use values-only and minimal chrome");
        var location = RegistryLocation.Parse("HKCU\\Software\\Example");
        var draft = new NoteDraft(location, "Saved text");
        check(!draft.IsDirty, "Loaded note starts saved");
        draft.Text = "Unsaved text";
        check(
            draft.IsDirty && draft.Matches(RegistryLocation.Parse("hkcu\\software\\EXAMPLE")),
            "Draft changes retain canonical key identity");
        check(!draft.Matches(location with { View = RegistryView.Registry32 }), "Draft identity preserves registry view");
        check(!draft.Matches(location.Child("Child")), "Draft identity excludes child keys");
        draft.Text = "Saved text";
        check(!draft.IsDirty, "Restoring original text clears the dirty state");
        CheckNotePersistence(check, location);
        if (!integration)
        {
            return;
        }

        var registry = new RegistryService();
        var root = RegistryLocation.Parse("HKCU\\Software\\Regedit.Tools.Tests." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(root.Parent!, root.Name);
        registry.CreateKey(root, "Unreadable");
        using var unreadable = Registry.CurrentUser.OpenSubKey(
            root.Child("Unreadable").SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!;
        var originalSecurity = unreadable.GetAccessControl();
        WatchSession? session = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = unreadable.GetAccessControl();
            security.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.ReadKey, AccessControlType.Deny));
            unreadable.SetAccessControl(security);
            session = new WatchSession(
                registry, new WatchDefinition(root, "TRACKED"), action =>
                {
                    action();
                    return Task.CompletedTask;
                });
            await Until(() => session.Status == "Watching" || !session.IsRunning);
            check(session.IsRunning, "Value watch starts without a view or reading inaccessible descendants");
            var observation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Observations.CollectionChanged += Observed;

            void Observed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
            {
                observation.TrySetResult();
            }

            registry.Write(root, new RegistryValue("Tracked", RegistryValueType.DWord, [1, 0, 0, 0]), null);
            await observation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Observations.CollectionChanged -= Observed;
            registry.Write(
                root, new RegistryValue("Tracked", RegistryValueType.DWord, [2, 0, 0, 0]),
                new RegistryValue("Tracked", RegistryValueType.DWord, [1, 0, 0, 0]));
            await Until(() => session.Observations.Count == 2 || !session.IsRunning);
            check(session.Observations.Count == 2 && session.IsRunning, "Watch retains observations after its observer detaches");
            session.Stop();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            session.Stop();
            check(!session.IsRunning && session.Status == "Stopped", "Explicit stop releases the watch and can be repeated");

            session = new WatchSession(
                registry, new WatchDefinition(root, string.Empty), action =>
                {
                    action();
                    return Task.CompletedTask;
                });
            await Until(() => session.Status == "Watching" || !session.IsRunning);
            check(
                session.IsRunning && registry.ReadValue(root, string.Empty) is null && session.Title.EndsWith(" · (Default)"),
                "Default value watch starts before the value exists");
            registry.Write(root, new RegistryValue("Other", RegistryValueType.DWord, [3, 0, 0, 0]), null);
            registry.CreateKey(root, "Child");
            registry.Write(root.Child("Child"), new RegistryValue(string.Empty, RegistryValueType.DWord, [4, 0, 0, 0]), null);
            var original = new RegistryValue(string.Empty, RegistryValueType.DWord, [5, 0, 0, 0]);
            var updated = original with { Data = [6, 0, 0, 0] };
            registry.Write(root, original, null);
            await Until(() => session.Status == "Watching · 1 observation" || !session.IsRunning);
            check(
                session.Observations.Count == 1
                && session.Observations[0].Change is { Kind: RegistryChangeKind.ValueAdded, Before: null } added
                && original.SameData(added.After), "Default value watch observes creation and ignores named values and subkeys");
            registry.Write(root, updated, original);
            await Until(() => session.Observations.Count >= 2 || !session.IsRunning);
            check(
                session.Observations.Count == 2
                && session.Observations[1].Change is { Kind: RegistryChangeKind.ValueChanged } changed
                && original.SameData(changed.Before)
                && updated.SameData(changed.After), "Default value watch retains exact before and after bytes");
            registry.DeleteValue(root, updated);
            await Until(() => session.Observations.Count >= 3 || !session.IsRunning);
            check(
                session.Observations.Count == 3
                && session.Observations[2].Change is { Kind: RegistryChangeKind.ValueRemoved, After: null } removed
                && updated.SameData(removed.Before), "Default value watch observes deletion");
            session.Stop();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            var removedKey = root.Child("Removed");
            registry.CreateKey(root, removedKey.Name);
            registry.Write(removedKey, original, null);
            session = new WatchSession(
                registry, new WatchDefinition(removedKey, string.Empty), action =>
                {
                    action();
                    return Task.CompletedTask;
                });
            await Until(() => session.Status == "Watching" || !session.IsRunning);
            check(session.IsRunning, "Value watch starts on a populated key");
            registry.DeleteKey(removedKey);
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                !session.IsRunning
                && session.Status.Contains("deleted")
                && session.Observations.Count == 2
                && session.Observations.Any(item => item.Change.Kind == RegistryChangeKind.KeyRemoved)
                && session.Observations.Any(item =>
                    item.Change.Kind == RegistryChangeKind.ValueRemoved && original.SameData(item.Change.Before)),
                "Deleting the watched key retains its value removal and stops the watch");

            var stopped = new WatchSession(
                registry, new WatchDefinition(root), action =>
                {
                    action();
                    return Task.CompletedTask;
                });
            stopped.Stop();
            await stopped.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            check(!stopped.IsRunning, "A watch can stop while capturing its baseline");

            var queued = System.Threading.Channels.Channel.CreateUnbounded<(Action Action, TaskCompletionSource Done)>();
            session = new WatchSession(
                registry, new WatchDefinition(root, "Queued"), action =>
                {
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    queued.Writer.TryWrite((action, done));
                    return done.Task;
                });

            async Task ApplyQueuedAsync()
            {
                var update = await queued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    update.Action();
                    update.Done.TrySetResult();
                }
                catch (Exception error)
                {
                    update.Done.TrySetException(error);
                }
            }

            await ApplyQueuedAsync();
            registry.Write(root, new RegistryValue("Queued", RegistryValueType.Binary, [1]), null);
            var late = await queued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            session.Stop();
            try
            {
                late.Action();
                late.Done.TrySetResult();
            }
            catch (Exception error)
            {
                late.Done.TrySetException(error);
            }

            await ApplyQueuedAsync();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                session.Observations.Count == 0 && !session.IsRunning && session.Error is null,
                "Stop rejects an already queued observation and keeps deliberate cancellation quiet");

            session = new WatchSession(
                registry, new WatchDefinition(root.Child("Missing")), action =>
                {
                    action();
                    return Task.CompletedTask;
                });
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                session.Error is FileNotFoundException && !session.IsRunning && session.Status.StartsWith("Stopped:"),
                "Unexpected watch failure retains its error with stopped session evidence");
        }
        finally
        {
            if (session is not null)
            {
                session.Stop();
                await session.Completion;
            }

            var restoredSecurity = new RegistrySecurity();
            restoredSecurity.SetSecurityDescriptorBinaryForm(
                originalSecurity.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            unreadable.SetAccessControl(restoredSecurity);
            unreadable.Close();
            registry.DeleteKey(root);
        }
    }

    private static void CheckNotePersistence(Action<bool, string> check, RegistryLocation location)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RegeditNotesTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var settings = new AppSettings();
            settings.Notes.Add(new KeyNote(location, "Old text"));
            settings.Notes.Add(new KeyNote(location with { SubKey = location.SubKey.ToUpperInvariant() }, "Duplicate legacy note"));
            settings.Notes.Add(new KeyNote(location with { View = RegistryView.Registry32 }, "Other view"));
            settings.Notes.Add(new KeyNote(location with { Hive = RegistryHive.LocalMachine, Machine = "SERVER" }, "Other computer"));
            var draft = new NoteDraft(location, "Old text") { Text = "Updated note: žluťoučký 日本語" };
            var dirtyNotifications = 0;
            draft.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NoteDraft.IsDirty))
                {
                    dirtyNotifications++;
                }
            };
            using var notes = NotesStoreTests.CreateStore(settings, move => settings.Save(path, move));
            notes.Save(draft);
            var loaded = AppSettings.Load(path);
            check(
                loaded.LoadError is null
                && loaded.Notes.Count == 3
                && loaded.Notes.Single(note => note.Matches(location)).Text == draft.Text,
                "Saving a note replaces canonical duplicates and preserves Unicode text");
            check(
                loaded.Notes.Any(note => note.Text == "Other view") && loaded.Notes.Any(note => note.Text == "Other computer"),
                "Saving a note preserves other computers and registry views");
            check(!draft.IsDirty && dirtyNotifications == 1, "Successful note save notifies all draft observers of its saved state");
            var saved = File.ReadAllBytes(path);
            var previous = settings.Notes.ToArray();
            foreach (var text in new[] { "Unsaved replacement", string.Empty })
            {
                draft.Text = text;
                var failed = false;
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        notes.Save(draft);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed = true;
                    }
                }

                check(
                    failed
                    && draft.IsDirty
                    && draft.Text == text
                    && settings.Notes.SequenceEqual(previous)
                    && saved.SequenceEqual(File.ReadAllBytes(path)),
                    text.Length == 0
                        ? "Failed note removal preserves the saved note and its unsaved clearing draft"
                        : "Failed note save preserves the shared draft and previous persisted notes");
            }

            notes.Save(draft);
            check(
                !draft.IsDirty && AppSettings.Load(path).Notes.All(note => !note.Matches(location)) && settings.Notes.Count == 2,
                "Retrying a cleared note removes only its own computer and view after the file unlocks");
            check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Note save failures leave no temporary settings files");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.Delete(file);
            }

            Directory.Delete(folder);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
