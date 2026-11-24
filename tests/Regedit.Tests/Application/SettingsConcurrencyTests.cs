// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Text.Json;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class SettingsConcurrencyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Regedit.SettingsConcurrency." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        try
        {
            var first = AppSettings.Load(file);
            var second = AppSettings.Load(file);
            first.AddFavorite("First window", "HKCU\\Software", false, null);
            second.AddFavorite("Second window", "HKLM\\Software", false, null);
            first.Save(file);
            second.Save(file);
            check(AppSettings.Load(file).Favorites.Count == 2, "Independent windows retain both favorites when saving stale settings");
            first.Save(file);
            second.Save(file);
            check(AppSettings.Load(file).Favorites.Count == 2, "Later saves and window closure cannot erase another window's favorites");

            first = AppSettings.Load(file);
            second = AppSettings.Load(file);
            first.UpdatePreferences(settings => settings.Theme = "Dark", file);
            second.UpdatePreferences(settings => settings.FontSize = 18, file);
            var saved = AppSettings.Load(file);
            check(
                saved.Theme == "Dark" && saved.FontSize == 18,
                "Different preference changes merge without reverting another window's choice");
            Reject(
                () => second.UpdatePreferences(settings => settings.Theme = "Light", file), check,
                "Conflicting preference changes fail without overwriting the first save");
            check(
                second.Theme == "Default" && AppSettings.Load(file).Theme == "Dark",
                "Preference conflicts retain the previous local state and current saved state");

            foreach (var ownerSavesFirst in new[] { true, false })
            {
                var sessionFile = Path.Combine(folder, "live-session-" + ownerSavesFirst + ".json");
                var owner = AppSettings.Load(sessionFile);
                owner.SaveSession([new SavedTab("HKCU")], true, sessionFile);
                var restored = AppSettings.Load(sessionFile);

                void SaveOwner()
                {
                    owner.SaveSession(
                        [new SavedTab("HKCU\\Software", Layout: new BrowserLayout(333))], true, sessionFile,
                        new WindowLayout(PaneWidth: 400));
                }

                void SaveRestored()
                {
                    restored.SaveSession(
                        [new SavedTab("HKLM", Layout: new BrowserLayout(444))], true, sessionFile, new WindowLayout(PaneWidth: 600));
                }

                if (ownerSavesFirst)
                {
                    SaveOwner();
                    SaveRestored();
                }
                else
                {
                    SaveRestored();
                    SaveOwner();
                }

                SaveOwner();
                SaveRestored();
                owner.Save(sessionFile);
                restored.Save(sessionFile);
                var sessions = AppSettings.Load(sessionFile);
                check(
                    sessions.LoadError is null
                    && sessions.Tabs.Count == 2
                    && sessions.Tabs.Any(tab => tab.Address == "HKCU\\Software" && tab.Layout?.NameWidth == 333)
                    && sessions.Tabs.Any(tab => tab.Address == "HKLM" && tab.Layout?.NameWidth == 444),
                    "Restoring a live window's session lets both windows save their current tabs: " + ownerSavesFirst);
                using var document = JsonDocument.Parse(File.ReadAllText(sessionFile));
                check(
                    document.RootElement.GetProperty("WindowSessions").EnumerateArray()
                        .Select(session => session.GetProperty("Layout").GetProperty("PaneWidth").GetDouble()).Order()
                        .SequenceEqual([400d, 600d]), "Restoring a live window's session retains both window layouts: " + ownerSavesFirst);
            }

            var noteLocation = RegistryLocation.Parse("HKCU\\Software\\SettingsConcurrency");
            saved.Notes.Add(new KeyNote(noteLocation, "Original"));
            saved.Save(file);
            first = AppSettings.Load(file);
            second = AppSettings.Load(file);
            using var firstNotes = NotesStoreTests.CreateStore(first, move => first.Save(file, move));
            using var secondNotes = NotesStoreTests.CreateStore(second, move => second.Save(file, move));
            firstNotes.Save(new NoteDraft(noteLocation, "Original") { Text = "First edit" });
            var draft = new NoteDraft(noteLocation, "Original") { Text = "Second edit" };
            Reject(() => secondNotes.Save(draft), check, "Two edits to the same note produce an explicit conflict");
            check(
                draft.IsDirty
                && draft.Text == "Second edit"
                && second.Notes.Single().Text == "Original"
                && AppSettings.Load(file).Notes.Single().Text == "First edit",
                "A note conflict retains the unsaved draft and the other window's saved text");

            var legacyFile = Path.Combine(folder, "legacy.json");
            File.WriteAllText(
                legacyFile, """{"Favorites":[{"Name":"Duplicate","Address":"HKCU"},{"Name":"Duplicate","Address":"HKCU"}]}""");
            first = AppSettings.Load(legacyFile);
            second = AppSettings.Load(legacyFile);
            check(
                first.Favorites.Select(item => item.Id).SequenceEqual(second.Favorites.Select(item => item.Id))
                && first.Favorites.Select(item => item.Id).Distinct().Count() == 2,
                "Legacy duplicate favorites get stable and distinct identities across processes");
            first.Favorites[0] = first.Favorites[0] with { Name = "Renamed" };
            second.Favorites.RemoveAt(1);
            first.Save(legacyFile);
            second.Save(legacyFile);
            check(
                AppSettings.Load(legacyFile).Favorites.Single().Name == "Renamed",
                "Renaming one duplicate and removing the other merge by bookmark identity");
            first = AppSettings.Load(legacyFile);
            second = AppSettings.Load(legacyFile);
            first.Favorites[0] = first.Favorites[0] with { Name = "Edited again" };
            second.Favorites.Clear();
            first.Save(legacyFile);
            Reject(() => second.Save(legacyFile), check, "Deleting a concurrently edited favorite is rejected");
            check(
                AppSettings.Load(legacyFile).Favorites.Single().Name == "Edited again", "A rejected deletion preserves the newer favorite");

            var graphFile = Path.Combine(folder, "folders.json");
            saved = AppSettings.Load(graphFile);
            var parent = saved.AddFavoriteFolder("Parent", null);
            saved.Save(graphFile);
            first = AppSettings.Load(graphFile);
            second = AppSettings.Load(graphFile);
            first.RemoveFavoriteItems([], [parent.Id]);
            second.AddFavorite("New child", "HKCU", false, parent.Id);
            second.Save(graphFile);
            Reject(() => first.Save(graphFile), check, "Removing a folder cannot orphan a concurrently added favorite");
            check(
                AppSettings.Load(graphFile).FavoriteFolders.Count == 1 && AppSettings.Load(graphFile).Favorites.Count == 1,
                "A rejected folder removal retains the complete saved hierarchy");

            var orderFile = Path.Combine(folder, "order.json");
            saved = AppSettings.Load(orderFile);
            foreach (var name in new[] { "A", "B", "C" })
            {
                saved.AddFavorite(name, "HKCU", false, null);
            }

            saved.Save(orderFile);
            first = AppSettings.Load(orderFile);
            second = AppSettings.Load(orderFile);
            first.ReorderFavorite(first.Favorites[1], -1);
            second.AddFavorite("D", "HKCU", false, null);
            second.Save(orderFile);
            first.Save(orderFile);
            check(
                AppSettings.Load(orderFile).Favorites.Select(item => item.Name).SequenceEqual(["B", "A", "C", "D"]),
                "A local reorder preserves a concurrently added favorite");
            first = AppSettings.Load(orderFile);
            second = AppSettings.Load(orderFile);
            first.ReorderFavorite(first.Favorites[1], -1);
            second.ReorderFavorite(second.Favorites[2], -1);
            first.Save(orderFile);
            Reject(() => second.Save(orderFile), check, "Conflicting reorder operations are reported instead of silently winning");

            var changedFile = Path.Combine(folder, "changed.json");
            saved = AppSettings.Load(changedFile);
            saved.Save(changedFile);
            File.WriteAllText(changedFile, "unreadable external update");
            Reject(() => saved.Save(changedFile), check, "A newly unreadable settings file is never replaced by stale settings");
            check(File.ReadAllText(changedFile) == "unreadable external update", "Unreadable external data survives a rejected save");
            saved = AppSettings.Load(changedFile);
            File.WriteAllText(changedFile, "a different unreadable update");
            Reject(() => saved.Save(changedFile), check, "Recovery cannot replace a file changed since its load error");
            File.Delete(changedFile);
            Reject(() => saved.Save(changedFile), check, "A removed settings file requires an explicit reload before saving");

            var lockFile = Path.Combine(folder, "locked.json");
            saved = AppSettings.Load(lockFile);
            saved.AddFavorite("After waiting", "HKCU", false, null);
            Task writer;
            using (var lease = File.Open(lockFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                writer = Task.Run(() => saved.Save(lockFile));
                await Task.Delay(100);
            }

            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                AppSettings.Load(lockFile).Favorites.Single().Name == "After waiting",
                "Settings saves wait for another writer's lease to be released");
            using (var lease = File.Open(lockFile + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Reject(() => saved.Save(lockFile), check, "Writer contention has a bounded timeout and leaves the saved file intact");
            }

            saved.Theme = "Dark";
            using (var reader = File.Open(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                writer = Task.Run(() => saved.Save(lockFile));
                await Task.Delay(100);
            }

            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                AppSettings.Load(lockFile).Theme == "Dark",
                "Settings replacement waits for a transient reader without discarding the save");

            await CheckProcesses(folder, true, check);
            await CheckProcesses(folder, false, check);
            check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Concurrent and rejected settings saves leave no temporary files");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                File.Delete(path);
            }

            Directory.Delete(folder);
        }
    }

    internal static async Task RunWriterAsync(string[] arguments)
    {
        var file = arguments[1];
        var name = arguments[2];
        var gate = arguments[3];
        var settings = AppSettings.Load(file);
        if (settings.LoadError is not null)
        {
            throw new IOException(settings.LoadError);
        }

        File.WriteAllText(gate + "." + name + ".ready", "ready");
        await Until(() => File.Exists(gate + ".save"));
        var location = RegistryLocation.Parse(
            name == "main" ? "HKLM\\Software" : "HKCU\\Software\\Link", Microsoft.Win32.RegistryView.Registry32);
        var destination = settings.AddFavoriteFolder(name, null);
        settings.AddFavorite(name, location.Address, true, destination.Id);
        settings.Notes.Add(new KeyNote(location, name));
        settings.Searches.Add(new SavedSearch(name, [location], new SearchOptions(name)));
        settings.Workspaces.Add(new RegistryWorkspace(name, [location], [], []));
        if (name == "main")
        {
            settings.Theme = "Dark";
            settings.ToolPanePinned = true;
        }
        else
        {
            settings.FontSize = 18;
            settings.SelectedTool = "Notes";
        }

        settings.Save(file);
        File.WriteAllText(gate + "." + name + ".saved", "saved");
        await Until(() => File.Exists(gate + "." + name + ".close"));
        settings.SaveSession(
            name == "main"
                ? [new SavedTab("HKCU"), new SavedTab(location.Address, true, new BrowserLayout(333))]
                : [new SavedTab(location.Address, true, new BrowserLayout(444))], name == "main", file,
            new WindowLayout(
                new WindowPlacement(new WindowBounds(name == "main" ? 100 : 200, 100, 1000, 700), new WindowBounds(0, 0, 1920, 1040)),
                name == "main", name == "main", name == "main" ? 400 : 600, name == "main" ? "Journal" : "Watches"));
    }

    internal static async Task CheckElevatedProcessesAsync(string folder, Process elevatedWriter, Action<bool, string> check)
    {
        await CheckProcesses(folder, true, check, elevatedWriter);
        await CheckProcesses(folder, false, check, elevatedWriter);
    }

    private static async Task CheckProcesses(
        string folder,
        bool mainClosesFirst,
        Action<bool, string> check,
        Process? elevatedWriter = null)
    {
        var file = Path.Combine(folder, "processes-" + mainClosesFirst + ".json");
        var gate = file + ".gate";
        File.WriteAllText(file, """{"Tabs":[{"Address":"HKCU"}]}""");
        if (elevatedWriter is not null)
        {
            File.WriteAllText(file + ".fixture-ready", "ready");
        }

        var processes = new List<Process>();
        try
        {
            foreach (var name in new[] { "main", "link" })
            {
                if (name == "link" && elevatedWriter is not null)
                {
                    processes.Add(elevatedWriter);
                    continue;
                }

                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    start.ArgumentList.Add(typeof(SettingsConcurrencyTests).Assembly.Location);
                }

                foreach (var argument in new[] { "--settings-writer", file, name, gate })
                {
                    start.ArgumentList.Add(argument);
                }

                processes.Add(Process.Start(start)!);
            }

            void CheckRunning()
            {
                foreach (var process in processes)
                {
                    if (process.HasExited)
                    {
                        throw new IOException(
                            "Settings worker exited early: "
                            + (process == elevatedWriter
                                ? File.ReadAllText(Path.Combine(folder, "worker-error.log"))
                                : process.StandardError.ReadToEnd()));
                    }
                }
            }

            await Until(() =>
            {
                CheckRunning();
                return File.Exists(gate + ".main.ready") && File.Exists(gate + ".link.ready");
            });
            File.WriteAllText(gate + ".save", "save");
            await Until(() =>
            {
                CheckRunning();
                return File.Exists(gate + ".main.saved") && File.Exists(gate + ".link.saved");
            });
            foreach (var name in mainClosesFirst ? new[] { "main", "link" } : ["link", "main"])
            {
                File.WriteAllText(gate + "." + name + ".close", "close");
                var process = processes[name == "main" ? 0 : 1];
                if (process == elevatedWriter)
                {
                    await Until(() =>
                    {
                        if (File.Exists(gate + ".link.closed"))
                        {
                            return true;
                        }

                        if (process.HasExited)
                        {
                            throw new IOException(File.ReadAllText(Path.Combine(folder, "worker-error.log")));
                        }

                        return false;
                    });
                    continue;
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (process.ExitCode != 0)
                {
                    throw new IOException("Settings worker failed: " + await process.StandardError.ReadToEndAsync());
                }
            }

            var saved = AppSettings.Load(file);
            check(
                saved.LoadError is null
                && saved.Favorites.Count == 2
                && saved.FavoriteFolders.Count == 2
                && saved.Notes.Count == 2
                && saved.Searches.Count == 2
                && saved.Workspaces.Count == 2,
                "Two processes merge favorites, folders, notes, searches, and workspaces in closing order: " + mainClosesFirst);
            check(
                saved.Theme == "Dark" && saved.FontSize == 18 && saved.ToolPanePinned && saved.SelectedTool == "Notes",
                "Two processes preserve independent appearance and tool preferences: " + mainClosesFirst);
            check(
                saved.Tabs.Count == 3 && saved.Tabs.Count(tab => tab.Is32Bit) == 2 && saved.Tabs.Any(tab => tab.Address == "HKCU"),
                "A link window cannot replace the main window's restored tab session: " + mainClosesFirst);
            using (var layouts = JsonDocument.Parse(File.ReadAllText(file)))
            {
                var sessions = layouts.RootElement.GetProperty("WindowSessions").EnumerateArray().ToArray();
                check(
                    sessions.Length == 2
                    && sessions.Any(session => session.GetProperty("Layout").GetProperty("PaneWidth").GetDouble() == 400
                                               && session.GetProperty("Tabs").EnumerateArray().Any(tab =>
                                                   tab.TryGetProperty("Layout", out var layout)
                                                   && layout.ValueKind == JsonValueKind.Object
                                                   && layout.GetProperty("NameWidth").GetDouble() == 333))
                    && sessions.Any(session =>
                        session.GetProperty("Layout").GetProperty("PaneWidth").GetDouble() == 600
                        && session.GetProperty("Tabs")[0].GetProperty("Layout").GetProperty("NameWidth").GetDouble() == 444),
                    "Two processes retain separate window and browser layout in closing order: " + mainClosesFirst);
            }

            saved.SaveSession(saved.Tabs.ToList(), true, file);
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            check(
                document.RootElement.GetProperty("WindowSessions").GetArrayLength() == 1 && AppSettings.Load(file).Tabs.Count == 3,
                "A restored session replaces consumed session records without duplicating its tabs: " + mainClosesFirst);
        }
        finally
        {
            foreach (var process in processes)
            {
                if (process == elevatedWriter)
                {
                    continue;
                }

                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }

                process.Dispose();
            }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void Reject(Action action, Action<bool, string> check, string message)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            check(true, message);
            return;
        }

        check(false, message);
    }
}
