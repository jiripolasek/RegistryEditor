// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class FavoritesViewModelTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditFavorites.");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(file);
            var failSave = false;
            var store = new FavoritesStore(
                settings, () =>
                {
                    if (failSave)
                    {
                        throw new IOException("Simulated save failure");
                    }

                    settings.Save(file);
                });
            var parent = store.AddFolder("Investigations", null);
            var nested = store.AddFolder("Nested", parent);
            store.Add("Bookmark", @"\\SERVER\HKLM\Software", true, nested);
            store.Add("Bookmark", @"HKCU\Software", false, nested);
            var remote = new FavoriteItemId(store.Favorites[0].Id, false);
            var local = new FavoriteItemId(store.Favorites[1].Id, false);
            var dialogs = new TestDialogs();
            var opened = new List<(RegistryLocation Location, bool NewTab)>();
            var background = new List<RegistryLocation>();

            Task Navigate(RegistryLocation location, bool newTab)
            {
                opened.Add((location, newTab));
                return Task.CompletedTask;
            }

            var panel = new FavoritesViewModel(store, true, dialogs, Navigate, background.Add);
            var tab = new FavoritesViewModel(store, false, dialogs, Navigate, background.Add);
            panel.Activate();
            tab.Activate();
            check(
                !panel.AddCommand.CanExecute(null) && tab.AddCommand.CanExecute(null),
                "Only the standalone organizer accepts Add without a visible registry key");
            dialogs.Edit = (_, _, _) =>
                throw new InvalidOperationException("An unavailable Add command opened a dialog");
            await panel.AddCommand.ExecuteAsync(null);
            check(
                !panel.HasError && store.Favorites.Count == 2,
                "Direct invocation of compact Add without a location is harmless");
            var addChanged = 0;
            panel.AddCommand.CanExecuteChanged += (_, _) => addChanged++;
            panel.CurrentLocation = RegistryLocation.Parse("HKCU");
            check(addChanged == 1, "Selecting a registry tab invalidates the panel's Add command");
            check(
                panel.AddCommand.CanExecute(null) && !tab.OpenCommand.CanExecute(null),
                "Compact Add follows the visible browser and Open requires a selection");

            panel.ExpandedFolders.UnionWith([parent, nested]);
            panel.SelectTree(remote);
            tab.OpenFolder(nested);
            tab.SelectRows([local]);
            await tab.OpenCommand.ExecuteAsync(null);
            await tab.OpenNewTabCommand.ExecuteAsync(null);
            await tab.OpenBackgroundCommand.ExecuteAsync(remote);
            check(
                opened.SequenceEqual([(tab.Location(local), false), (tab.Location(local), true)]) &&
                background.Single() == tab.Location(remote) && tab.Selected.SequenceEqual([local]),
                "Open and Open in new tab navigate the selected bookmark while middle-click uses its own target");

            var renameChanged = 0;
            tab.RenameCommand.CanExecuteChanged += (_, _) => renameChanged++;
            tab.SelectRows([]);
            check(
                renameChanged > 0 && !tab.RenameCommand.CanExecute(null) && !tab.RemoveCommand.CanExecute(null),
                "Clearing selection invalidates Rename and Remove for F2 and Delete");
            tab.SelectRows([local]);
            var rename = tab.RenameCommand.ExecuteAsync(null);
            check(
                !tab.NewFolderCommand.CanExecute(null) && !tab.RemoveCommand.CanExecute(null),
                "An open rename prompt disables competing commands");
            tab.SelectRows([remote]);
            dialogs.Rename.SetResult("Local renamed");
            await rename;
            check(
                store.Bookmark(local.Id).Name == "Local renamed" && store.Bookmark(remote.Id).Name == "Bookmark" &&
                tab.NewFolderCommand.CanExecute(null),
                "Rename keeps its original target and releases the other commands");
            store.Rename(local, "Bookmark");
            tab.SelectRows([local]);
            await tab.MoveUpCommand.ExecuteAsync(null);
            check(
                store.Favorites[0].Id == local.Id && !tab.MoveUpCommand.CanExecute(null) &&
                tab.MoveDownCommand.CanExecute(null),
                "Reordering the first bookmark invalidates both move commands after the store refresh");
            await tab.MoveDownCommand.ExecuteAsync(null);
            tab.Search("Bookmark");
            check(
                !tab.MoveUpCommand.CanExecute(null) && !tab.MoveDownCommand.CanExecute(null),
                "Search disables manual reordering");
            tab.Search(string.Empty);

            var panelChanges = 0;
            var tabChanges = 0;
            panel.ItemsChanged += (_, _) => panelChanges++;
            tab.ItemsChanged += (_, _) => tabChanges++;
            store.Rename(remote, "Bookmark renamed");
            check(
                panelChanges == 1 && tabChanges == 1 &&
                panel.Rows.Single(row => row.Key == remote).Title == "Bookmark renamed" &&
                tab.Rows.Single(row => row.Key == remote).Title == "Bookmark renamed" &&
                panel.Selected.SequenceEqual([remote]) && tab.Selected.SequenceEqual([local]),
                "A committed rename updates both surfaces once and preserves independent selections by ID");
            panel.Search("Bookmark");
            tab.CopyFrom(panel);
            check(
                tab.Query == "Bookmark" && tab.FolderId == nested && tab.Selected.SequenceEqual([remote]) &&
                !tab.TreeSelection && tab.ExpandedFolders.SetEquals([string.Empty, parent, nested]),
                "Panel to organizer transfers nested location, search, expansion and the selected bookmark");
            tab.SelectRows([local, remote]);
            check(
                tab.MoveToFolderCommand.CanExecute(null) && !tab.RenameCommand.CanExecute(null),
                "The organizer supports batch actions and restricts single-item commands");
            panel.CopyFrom(tab);
            check(
                panel.Query == tab.Query && panel.FolderId == nested && panel.TreeSelection &&
                panel.Selected.SequenceEqual([local]) && tab.Selected.SequenceEqual([local, remote]) &&
                panel.ExpandedFolders.SetEquals(tab.ExpandedFolders),
                "Organizer to single-select panel copies its first selection without changing the organizer");
            await tab.RemoveCommand.ExecuteAsync(null);
            check(
                dialogs.RemoveCounts == (2, 0) && store.Favorites.Count == 2,
                "Canceling a multi-selection removal preserves every bookmark");
            var target = panel.Location(remote);
            check(
                target.Machine == "SERVER" && target.SubKey == "Software" && target.View == RegistryView.Registry32 &&
                panel.Selected.SequenceEqual([local]),
                "Resolving a background target preserves its machine, view and the active selection");

            var before = File.ReadAllBytes(file);
            var beforeFolders = store.Folders.ToArray();
            var beforeFavorites = store.Favorites.ToArray();
            var panelBefore = panelChanges;
            var tabBefore = tabChanges;
            failSave = true;
            try
            {
                store.Remove([new FavoriteItemId(parent, true)]);
                throw new InvalidOperationException("The failed Favorites save was not surfaced");
            }
            catch (IOException)
            {
                check(
                    store.Folders.SequenceEqual(beforeFolders) && store.Favorites.SequenceEqual(beforeFavorites) &&
                    File.ReadAllBytes(file).SequenceEqual(before) && panelChanges == panelBefore + 1 &&
                    tabChanges == tabBefore + 1 && panel.Selected.SequenceEqual([local]) &&
                    tab.Selected.SequenceEqual([local, remote]) && panel.Rows.Count == 2 && tab.Rows.Count == 2,
                    "A failed subtree removal restores both surfaces, their selections, and the persisted settings");
            }

            failSave = false;
            panelBefore = panelChanges;
            tabBefore = tabChanges;
            var note = new NoteDraft(panel.CurrentLocation, string.Empty) { Text = "Edited note" };
            using var notes = NotesStoreTests.CreateStore(settings, () => settings.Save(file));
            notes.Save(note);
            check(
                panelChanges == panelBefore && tabChanges == tabBefore,
                "Editing and saving a note causes zero Favorites refreshes");
            panel.Deactivate();
            store.RemoveLocation(target);
            check(
                panelChanges == panelBefore && tab.Selected.SequenceEqual([local]) && store.Favorites.Count == 1,
                "Browser removal reaches the active organizer and prunes only the removed ID");
            panel.Activate();
            check(
                panel.Rows.Count == 1 && panel.Selected.SequenceEqual([local]),
                "A returning panel loads changes made while detached");
            tab.OpenFolder(nested);
            tab.ExpandedFolders.Remove(nested);
            panel.CopyFrom(tab);
            check(
                panel.ExpandedFolders.Contains(nested) &&
                panel.Selected.SequenceEqual([new FavoriteItemId(nested, true)]),
                "Showing the current folder in the compact panel reveals its bookmarks");

            dialogs.Edit = (draft, fixedLocation, error) =>
            {
                check(
                    !fixedLocation && draft.FolderId == nested,
                    "Organizer Add uses its selected folder and an explicit registry location");
                if (error is null)
                {
                    return draft with { Name = "Edited draft", Address = "invalid" };
                }

                check(
                    draft.Name == "Edited draft" && draft.Address == "invalid",
                    "Validation returns the user's draft for correction");
                return draft with { Address = @"HKCU\Software", Is32Bit = true };
            };
            await tab.AddCommand.ExecuteAsync(null);
            check(
                store.Favorites.Last() is { Name: "Edited draft", Is32Bit: true } saved && saved.FolderId == nested,
                "The generated Add command saves the corrected registry location once");
            var location = RegistryLocation.Parse(@"HKCU\Software\BrowserFavoriteFixture");
            var firstBrowser = new BrowserViewModel(
                new RegistryService(), location, new RegistryEntryClipboard(), store);
            var secondBrowser = new BrowserViewModel(
                new RegistryService(), location, new RegistryEntryClipboard(), store);
            firstBrowser.AddFavoriteRequested = target => FavoritesViewModel.AddFavoriteAsync(
                store, dialogs, target, null, true);
            dialogs.Edit = (draft, _, error) => error is null ? draft : null;
            var childLocation = location.Child("SelectedChild");
            check(
                firstBrowser.SelectedFavoriteLocation.IsSameLocation(location),
                "An empty selection favorites the current location");
            firstBrowser.SelectedEntry = new RegistryEntry(childLocation, null);
            check(
                firstBrowser.SelectedFavoriteLocation.IsSameLocation(childLocation),
                "A single selected key is the keyboard favorite target");
            await firstBrowser.ToggleFavoriteCommand.ExecuteAsync(firstBrowser.SelectedFavoriteLocation);
            check(
                store.Contains(childLocation) && !store.Contains(location),
                "Favoriting the selected child leaves the current key untouched");
            await firstBrowser.ToggleFavoriteCommand.ExecuteAsync(firstBrowser.SelectedFavoriteLocation);
            check(!store.Contains(childLocation), "The selected child can be removed through the same favorite target");
            firstBrowser.SetSelection(
                [new RegistryEntry(childLocation, null), new RegistryEntry(location.Child("Other"), null)]);
            check(
                firstBrowser.SelectedFavoriteLocation.IsSameLocation(location),
                "Multiple selected keys fall back to the current location");
            firstBrowser.SelectedEntry = new RegistryEntry(
                null, new RegistryValue("Value", RegistryValueType.String, []));
            check(
                firstBrowser.SelectedFavoriteLocation.IsSameLocation(location),
                "A selected value favorites its containing location");
            firstBrowser.SelectedEntry = new RegistryEntry(null, null, ParentLocation: location.GetNavigationParent());
            check(
                firstBrowser.SelectedFavoriteLocation.IsSameLocation(location),
                "The virtual parent row does not redirect the favorite target");
            firstBrowser.SelectedEntry = null;
            check(
                !firstBrowser.IsFavorite && firstBrowser.FavoriteAction == "Add favorite",
                "The address star initially reflects a missing loaded favorite");
            failSave = true;
            var failedAdd = false;
            dialogs.Edit = (draft, _, error) =>
            {
                if (error is not null)
                {
                    failedAdd = true;
                    return null;
                }

                return draft;
            };
            await firstBrowser.ToggleFavoriteCommand.ExecuteAsync(null);
            check(
                failedAdd && !firstBrowser.IsFavorite && !secondBrowser.IsFavorite && !store.Contains(location),
                "A failed browser addition reports the save error and leaves both stars unset");
            failSave = false;
            await firstBrowser.ToggleFavoriteCommand.ExecuteAsync(null);
            check(
                firstBrowser.IsFavorite && secondBrowser.IsFavorite && firstBrowser.FavoriteGlyph == "\uE735",
                "The same typed toggle updates loaded address stars in both tabs after a successful add");
            var savedBytes = File.ReadAllBytes(file);
            failSave = true;
            await secondBrowser.ToggleFavoriteCommand.ExecuteAsync(null);
            check(
                secondBrowser.Error == "Simulated save failure" && firstBrowser.IsFavorite &&
                secondBrowser.IsFavorite && savedBytes.SequenceEqual(File.ReadAllBytes(file)),
                "A failed browser removal reports its error while both stars and persisted data remain set");
            failSave = false;
            await firstBrowser.ToggleFavoriteCommand.ExecuteAsync(null);
            check(
                !firstBrowser.IsFavorite && !secondBrowser.IsFavorite && firstBrowser.FavoriteGlyph == "\uE734",
                "Removing through the shared operation clears every loaded tab's star");
            dialogs.Edit = (draft, _, _) =>
            {
                store.Add(draft.Name, draft.Address, draft.Is32Bit, draft.FolderId);
                return draft;
            };
            await firstBrowser.ToggleFavoriteCommand.ExecuteAsync(null);
            check(
                store.Favorites.Count(item => item.Matches(location)) == 1,
                "Another tab saving while the add dialog is open cannot create a duplicate browser favorite");
            firstBrowser.CancelPending();
            secondBrowser.CancelPending();
            store.RemoveLocation(location);
            check(
                firstBrowser.IsFavorite && !firstBrowser.ToggleFavoriteCommand.CanExecute(null),
                "Closing a browser detaches favorite notifications and disables its operation");

            panel.ToggleDetailsCommand.Execute(null);
            check(panel.ShowDetails && !tab.ShowDetails, "Details state belongs to its own surface");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class TestDialogs : IFavoriteDialogs
    {
        internal TaskCompletionSource<string?> Rename { get; } = new();

        internal (int Favorites, int Folders) RemoveCounts { get; private set; }

        internal Func<Favorite, bool, string?, Favorite?>? Edit { get; set; }

        public Task<Favorite?> EditFavoriteAsync(
            Favorite draft,
            bool fixedLocation,
            FavoriteFolder[] folders,
            string? error)
        {
            return Task.FromResult(this.Edit?.Invoke(draft, fixedLocation, error));
        }

        public Task<string?> RenameAsync(string name, bool isFolder)
        {
            return this.Rename.Task;
        }

        public Task<bool> ConfirmRemoveAsync(int favorites, int folders)
        {
            this.RemoveCounts = (favorites, folders);
            return Task.FromResult(false);
        }

        public Task<(string Name, string? ParentId)?> NewFolderAsync(FavoriteFolder[] folders, string? parentId)
        {
            throw new InvalidOperationException("Unexpected new folder prompt");
        }

        public Task<FavoriteFolder?> ChooseFolderAsync(FavoriteFolder[] folders, string? folderId)
        {
            throw new InvalidOperationException("Unexpected move prompt");
        }
    }
}
