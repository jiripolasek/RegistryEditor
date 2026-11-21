// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class FavoriteFolderTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(FavoritesBarLayout.VisibleCount([], 32, 2, 34) == 0, "An empty bar has no items to fit");
        check(FavoritesBarLayout.VisibleCount([50, 60, 70], 184, 2, 34) == 3,
            "An exact fit does not reserve space for the overflow button");
        check(FavoritesBarLayout.VisibleCount([50, 60, 70], 148, 2, 34) == 2 &&
              FavoritesBarLayout.VisibleCount([50, 60, 70], 147, 2, 34) == 1,
            "A fitting prefix accounts for spacing and the overflow button");
        check(FavoritesBarLayout.VisibleCount([90, 20], 100, 2, 34) == 0,
            "A long first item goes into overflow along with the items after it");

        var directory = Path.Combine(Path.GetTempPath(), "Regedit.Favorites.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(
                file,
                """{"Favorites":[{"Name":"Legacy","Address":"HKCU\\Software","Is32Bit":true}],"Theme":"Dark","ToolPanePinned":true}""");
            var settings = AppSettings.Load(file);
            var legacy = settings.Favorites.Single();
            check(
                settings.LoadError is null
                && settings.FavoriteFolders.Count == 0
                && legacy.FolderId is null
                && legacy.Matches(RegistryLocation.Parse("HKCU\\Software", RegistryView.Registry32)),
                "Legacy favorites load at the root without losing registry view");
            check(settings.Theme == "Dark" && settings.ToolPanePinned, "Favorites migration preserves other settings");

            var work = settings.AddFavoriteFolder(" Work ", null);
            var personal = settings.AddFavoriteFolder("Personal", null);
            var windows = settings.AddFavoriteFolder("Windows", work.Id);
            var otherWindows = settings.AddFavoriteFolder("Windows", personal.Id);
            check(
                work.Name == "Work" && settings.FavoriteFolderPath(windows.Id) == "Favorites / Work / Windows",
                "Nested favorite folders have trimmed names and a full path");
            reject(() => settings.AddFavoriteFolder(" ", null), "Favorite folders reject blank names");
            reject(() => settings.AddFavoriteFolder("wORK", null), "Favorite folders reject duplicate sibling names ignoring case");
            reject(() => settings.AddFavoriteFolder("Orphan", "missing"), "Favorite folders require an existing parent");
            check(otherWindows.ParentId == personal.Id, "Different folders may contain the same subfolder name");

            var child = new Favorite("Classes", "HKCR", false, windows.Id);
            var remote = new Favorite("Server", "\\\\SERVER\\HKLM\\Software", true, work.Id);
            settings.Favorites.Add(child);
            settings.Favorites.Add(remote);
            var renamed = settings.RenameFavoriteFolder(work.Id, "System");
            check(
                renamed.Id == work.Id
                && settings.FavoriteFolderPath(windows.Id) == "Favorites / System / Windows"
                && child.FolderId == windows.Id, "Renaming a folder preserves descendants and favorite references");
            reject(() => settings.RenameFavoriteFolder(work.Id, "Personal"), "Renaming a folder cannot duplicate a sibling name");

            settings.MoveFavoriteItems([legacy], [], windows.Id);
            check(
                settings.Favorites.Single(favorite => favorite.Name == "Legacy") is { FolderId: var legacyFolder, Is32Bit: true }
                && legacyFolder == windows.Id, "Moving a favorite preserves its address and registry view");
            settings.MoveFavoriteItems([], [work.Id], personal.Id);
            check(
                settings.FavoriteFolderPath(windows.Id) == "Favorites / Personal / System / Windows"
                && remote.Matches(RegistryLocation.Parse("\\\\SERVER\\HKLM\\Software", RegistryView.Registry32)),
                "Moving a folder preserves its complete subtree and remote favorite identity");
            var beforeMove = settings.FavoriteFolders.ToArray();
            reject(
                () => settings.MoveFavoriteItems([], [personal.Id], windows.Id), "A favorites folder cannot move into its own descendant");
            reject(() => settings.MoveFavoriteItems([], [work.Id], work.Id), "A favorites folder cannot move into itself");
            check(settings.FavoriteFolders.SequenceEqual(beforeMove), "Rejected folder moves leave the hierarchy unchanged");

            settings.MoveFavoriteItems([child], [work.Id, windows.Id], null);
            check(
                settings.FavoriteFolders.Single(folder => folder.Id == work.Id).ParentId is null
                && settings.FavoriteFolders.Single(folder => folder.Id == windows.Id).ParentId == work.Id
                && settings.Favorites.Contains(child),
                "Moving selected ancestors and descendants preserves nesting without flattening selected favorites");
            settings.AddFavoriteFolder("Windows", null);
            var beforeFavorites = settings.Favorites.ToArray();
            beforeMove = settings.FavoriteFolders.ToArray();
            reject(() => settings.MoveFavoriteItems([child], [windows.Id], null), "A folder move rejects a destination name conflict");
            check(
                settings.Favorites.SequenceEqual(beforeFavorites) && settings.FavoriteFolders.SequenceEqual(beforeMove),
                "A rejected mixed move changes neither favorites nor folders");

            var duplicateOne = new Favorite("Duplicate", "HKCU");
            var duplicateTwo = new Favorite("Duplicate", "HKCU");
            settings.Favorites.Add(duplicateOne);
            settings.Favorites.Add(duplicateTwo);
            settings.MoveFavoriteItems([duplicateTwo], [], windows.Id);
            check(
                settings.Favorites.Any(favorite => ReferenceEquals(favorite, duplicateOne) && favorite.FolderId is null)
                && settings.Favorites.Count(favorite => favorite.Name == "Duplicate" && favorite.FolderId == windows.Id) == 1,
                "Moving equal favorites targets only the selected instance");
            var excluded = settings.FavoriteFolderSubtree([work.Id]);
            var choices = settings.FavoriteFolderChoices(excluded);
            check(
                choices[0].Id == string.Empty
                && choices.All(folder => !excluded.Contains(folder.Id))
                && choices.Any(folder => folder.Name == "Favorites / Personal / Windows"),
                "Folder destinations include the root and exclude the selected subtree");

            settings.Save(file);
            var loaded = AppSettings.Load(file);
            check(
                loaded.LoadError is null
                && loaded.FavoriteFolders.SequenceEqual(settings.FavoriteFolders)
                && loaded.Favorites.SequenceEqual(settings.Favorites),
                "Favorite folders and their ordered contents survive a settings round trip");
            settings.RemoveFavoriteItems([], [work.Id, windows.Id]);
            check(
                !settings.FavoriteFolders.Any(folder => excluded.Contains(folder.Id))
                && !settings.Favorites.Any(favorite => favorite.FolderId is { } id && excluded.Contains(id)),
                "Removing a folder removes its subtree without leaving dangling favorites");
            check(
                settings.FavoriteFolders.Contains(otherWindows)
                && settings.Favorites.Any(favorite => ReferenceEquals(favorite, duplicateOne)),
                "Removing a subtree preserves unrelated folders and equal favorites outside it");

            var order = new AppSettings();
            var firstFolder = order.AddFavoriteFolder("First", null);
            var secondFolder = order.AddFavoriteFolder("Second", null);
            var nested = order.AddFavoriteFolder("Nested", firstFolder.Id);
            var first = new Favorite("First", "HKCU", false, firstFolder.Id);
            var unrelated = new Favorite("Unrelated", "HKLM", false, secondFolder.Id);
            var second = new Favorite("Second", "HKCR", false, firstFolder.Id);
            order.Favorites.Add(first);
            order.Favorites.Add(unrelated);
            order.Favorites.Add(second);
            order.ReorderFavorite(second, -1);
            check(
                order.Favorites.Where(favorite => favorite.FolderId == firstFolder.Id).SequenceEqual([second, first])
                && order.Favorites.Contains(unrelated), "Favorite reordering stays within its folder");
            order.ReorderFavoriteFolder(secondFolder.Id, -1);
            check(
                order.FavoriteFolders.Where(folder => folder.ParentId is null).SequenceEqual([secondFolder, firstFolder])
                && nested.ParentId == firstFolder.Id, "Folder reordering stays within its parent");

            var orderFile = Path.Combine(directory, "order.json");
            var store = new FavoritesStore(order, () => order.Save(orderFile));
            var firstId = new FavoriteItemId(first.Id, false);
            var secondId = new FavoriteItemId(second.Id, false);
            var firstFolderId = new FavoriteItemId(firstFolder.Id, true);
            var secondFolderId = new FavoriteItemId(secondFolder.Id, true);
            check(store.ReorderOffset(secondId, firstId, false) == 0 &&
                  store.ReorderOffset(firstId, secondId, true) == 0, "Adjacent drop positions can leave the order unchanged");
            check(store.ReorderOffset(firstId, secondId, false) == -1 &&
                  store.ReorderOffset(secondId, firstId, true) == 1, "Drop offsets account for removing the source item");
            check(store.ReorderOffset(firstId, new FavoriteItemId(unrelated.Id, false), false) == 0 &&
                  store.ReorderOffset(firstId, firstFolderId, false) == 0 &&
                  store.ReorderOffset(firstId, new FavoriteItemId("missing", false), false) == 0,
                "Reordering only targets existing siblings of the same kind");
            store.Reorder(firstFolderId, store.ReorderOffset(firstFolderId, secondFolderId, false));
            store.Reorder(firstId, store.ReorderOffset(firstId, secondId, false));
            loaded = AppSettings.Load(orderFile);
            check(loaded.FavoriteFolders.Where(folder => folder.ParentId is null).SequenceEqual([firstFolder, secondFolder]) &&
                  loaded.Favorites.Where(favorite => favorite.FolderId == firstFolder.Id).SequenceEqual([first, second]),
                "Bookmark and folder order after dropping survives a reload");

            var invalidCases = new (Favorite[] Favorites, FavoriteFolder[] Folders)[]
            {
                ([], [new FavoriteFolder("a", "A", "b"), new FavoriteFolder("b", "B", "a")]),
                ([], [new FavoriteFolder("a", "A", "missing")]), ([], [new FavoriteFolder("a", "A"), new FavoriteFolder("a", "B")]),
                ([], [new FavoriteFolder("a", "A"), new FavoriteFolder("b", "a")]),
                ([new Favorite("Orphan", "HKCU", false, "missing")], []), ([new Favorite("Bad address", "UNKNOWN")], []),
                ([], [new FavoriteFolder(string.Empty, "Missing identifier")])
            };
            for (var index = 0; index < invalidCases.Length; index++)
            {
                var invalidFile = Path.Combine(directory, $"invalid-{index}.json");
                var data = invalidCases[index];
                var json = JsonSerializer.Serialize(new { data.Favorites, FavoriteFolders = data.Folders });
                File.WriteAllText(invalidFile, json);
                var invalid = AppSettings.Load(invalidFile);
                check(
                    invalid.LoadError is not null
                    && invalid.FavoriteFolders.Count == 0
                    && invalid.Favorites.Count == 0
                    && File.ReadAllText(invalidFile) == json, $"Invalid favorites structure {index} is rejected without changing its file");
                invalid.Save(invalidFile);
                var backup = Directory.GetFiles(directory, Path.GetFileName(invalidFile) + ".unreadable.*").Single();
                check(
                    File.ReadAllText(backup) == json && AppSettings.Load(invalidFile).LoadError is null,
                    $"Invalid favorites structure {index} is backed up before recovery");
            }

            var savedText = File.ReadAllText(file);
            var invalidSettings = new AppSettings();
            invalidSettings.FavoriteFolders.Add(new FavoriteFolder("orphan", "Orphan", "missing"));
            reject(() => invalidSettings.Save(file), "Saving rejects an invalid folder hierarchy");
            check(File.ReadAllText(file) == savedText, "Rejected settings saves preserve the existing file");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try
                {
                    settings.Save(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }

                check(failed, "A locked settings file reports a failed favorites save");
            }

            check(
                File.ReadAllText(file) == savedText && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "Failed favorites saves preserve the file and clean up temporary output");
        }
        finally
        {
            foreach (var temporary in Directory.GetFiles(directory))
            {
                File.Delete(temporary);
            }

            Directory.Delete(directory);
        }
    }
}
