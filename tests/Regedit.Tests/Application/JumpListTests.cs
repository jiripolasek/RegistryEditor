// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class JumpListTests
{
    internal static void Run(Action<bool, string> check)
    {
        string[] arguments = ["", "--32", "český 🐈", "a\tb", "a\nb", "Quoted \"key\"", @"C:\Tail\", "\\\" \\"];
        check(
            RegistryLaunch.SplitArguments(RegistryLaunch.FormatArguments(arguments)).SequenceEqual(arguments),
            "Windows launch argument round-trip preserves empty tokens, whitespace, Unicode, quotes, and trailing slashes");
        check(RegistryLaunch.SplitArguments(string.Empty).Length == 0, "Empty packaged activation restores the session");
        try
        {
            RegistryLaunch.SplitArguments("Computer\0 --jump-list-admin");
            check(false, "Packaged launch rejects embedded nulls");
        }
        catch (ArgumentException)
        {
            check(true, "Packaged launch rejects embedded nulls");
        }

        var directory = Directory.CreateTempSubdirectory("RegeditJumpList.");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(file);
            var store = new FavoritesStore(settings, () => settings.Save(file));
            var remote = new Favorite("Remote", "\\\\SERVER\\HKLM\\Quoted \"key\"\\Tail ", true);
            settings.Favorites.Add(remote);
            settings.Favorites.Add(remote with { Id = "duplicate", Name = "Same destination" });
            settings.Favorites.Add(new Favorite("Local", "HKCU\\Software"));
            settings.Save(file);
            var stale = AppSettings.Load(file);
            var entries = JumpListEntries.Create(store.Favorites, settings.HiddenJumpListFavorites);
            check(
                entries.Length == 5 && entries[0].FavoriteId is null && entries[1].FavoriteId is null,
                "Jump List provides two tasks followed by favorites");
            check(
                RegistryLaunch.ParseArguments(RegistryLaunch.SplitArguments(entries[0].Arguments))!.IsComputer,
                "New window opens Computer without restoring the previous session");
            check(
                JumpListEntries.IsAdministratorTask(RegistryLaunch.SplitArguments(entries[1].Arguments))
                && !JumpListEntries.IsAdministratorTask([entries[1].Arguments, "HKLM"]),
                "Administrator task requires an exact standalone command");
            check(
                RegistryLaunch.ParseArguments(RegistryLaunch.SplitArguments(entries[2].Arguments))
                == RegistryLocation.Parse(remote.Address, RegistryView.Registry32),
                "Favorite activation preserves remote paths, quotes, trailing spaces, and 32-bit view");
            var removed = JumpListEntries.RemovedFavoriteIds(
                entries, new HashSet<string>([entries[2].Arguments, entries[0].Arguments], StringComparer.Ordinal));
            check(
                removed.SetEquals([remote.Id, "duplicate"]),
                "Removal hides all duplicate destinations without treating tasks as favorites");
            settings.HideJumpListFavorites(removed, file);
            var reloaded = AppSettings.Load(file);
            check(
                reloaded.Favorites.Count == 3 && JumpListEntries.Create(reloaded.Favorites, reloaded.HiddenJumpListFavorites).Length == 3,
                "Removed Jump List entries stay hidden after restart while favorites remain saved");
            new FavoritesStore(stale, () => stale.Save(file)).Rename(new FavoriteItemId(remote.Id, false), "Renamed");
            reloaded = AppSettings.Load(file);
            check(
                reloaded.Favorites[0].Name == "Renamed"
                && JumpListEntries.Create(reloaded.Favorites, reloaded.HiddenJumpListFavorites).Length == 3,
                "A stale window can rename a favorite without losing the user's Jump List removal");
            var otherWindow = AppSettings.Load(file);
            reloaded.HideJumpListFavorites(new HashSet<string>([reloaded.Favorites[2].Id]), file);
            otherWindow.HideJumpListFavorites(new HashSet<string>(["another-removed-id"]), file);
            check(
                AppSettings.Load(file).HiddenJumpListFavorites.SetEquals(
                    [remote.Id, "duplicate", reloaded.Favorites[2].Id, "another-removed-id"]),
                "Independent Jump List removals merge across stale windows");
            try
            {
                reloaded.HideJumpListFavorites(new HashSet<string>(["failed-removal"]), Path.Combine(file, "blocked.json"));
                check(false, "A failed removal save reports its error");
            }
            catch (IOException)
            {
                check(
                    !reloaded.HiddenJumpListFavorites.Contains("failed-removal"),
                    "A failed removal save rolls back the hidden destination state");
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
