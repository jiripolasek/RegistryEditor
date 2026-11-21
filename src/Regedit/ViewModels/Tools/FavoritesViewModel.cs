// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class FavoritesViewModel(
    FavoritesStore store,
    bool compact,
    IFavoriteDialogs dialogs,
    Func<RegistryLocation, bool, Task> navigate,
    Action<RegistryLocation> openBackground) : ObservableObject
{
    internal event Action<string, Exception>? ActionFailed;

    internal event EventHandler? ItemsChanged;

    internal event EventHandler? SelectionChanged;

    internal FavoritesStore Store { get; } = store;

    internal bool Compact { get; } = compact;

    internal string Query { get; private set; } = string.Empty;

    internal string? FolderId { get; private set; }

    internal HashSet<string> ExpandedFolders { get; private set; } = [string.Empty];

    internal bool TreeSelection { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSingleSelection), nameof(HasSelectedBookmark))]
    [NotifyCanExecuteChangedFor(
        nameof(OpenCommand), nameof(OpenNewTabCommand), nameof(RenameCommand), nameof(MoveToFolderCommand), nameof(RemoveCommand))]
    internal partial IReadOnlyList<FavoriteItemId> Selected { get; private set; } = [];

    [ObservableProperty]
    public partial bool ShowDetails { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(AddHelp))]
    public partial RegistryLocation? CurrentLocation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => this.Error is not null;

    public string AddLabel => this.Compact ? "Add current key" : "Add favorite…";

    public string AddHelp => !this.Compact
        ? this.AddLabel
        :
        this.CurrentLocation is { } location
            ?
            $"Add {location.Address} · {(location.View == RegistryView.Registry32 ? "32-bit" : "64-bit")}"
            : "Select a registry tab to add its current key.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(AddCommand), nameof(NewFolderCommand), nameof(OpenCommand), nameof(OpenNewTabCommand), nameof(OpenBackgroundCommand),
        nameof(RenameCommand), nameof(MoveToFolderCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveCommand),
        nameof(ToggleDetailsCommand))]
    internal partial bool IsBusy { get; private set; }

    internal IReadOnlyList<FavoriteRow> Rows { get; private set; } = [];

    internal FavoriteRow Root { get; private set; } = new(new FavoriteItemId(string.Empty, true), "Favorites", "Favorites", string.Empty);

    internal string FolderTitle =>
        this.Store.Folders.FirstOrDefault(folder => folder.Id == this.FolderId)?.Name ?? "Favorites";

    private bool Available => !this.IsBusy;

    private bool CanAdd => this.Available && (!this.Compact || this.CurrentLocation is not null);

    private bool CanUseSelection => this.Available && this.Selected.Count > 0;

    public bool HasSingleSelection => this.Selected.Count == 1;

    public bool HasSelectedBookmark => this.Selected is [{ IsFolder: false }];

    private bool CanUseSingleItem => this.Available && this.HasSingleSelection;

    private bool CanOpenNewTab => this.Available && this.HasSelectedBookmark;

    private bool CanMoveUp => this.Available && this.CanMove(-1);

    private bool CanMoveDown => this.Available && this.CanMove(1);

    internal void Activate()
    {
        this.Store.Changed -= this.StoreChanged;
        this.Store.Changed += this.StoreChanged;
        this.Refresh();
    }

    internal void Deactivate()
    {
        this.Store.Changed -= this.StoreChanged;
    }

    private void StoreChanged(object? sender, EventArgs args)
    {
        this.Refresh();
    }

    internal void Search(string query)
    {
        if (this.Query == query)
        {
            return;
        }

        this.Query = query;
        this.Refresh(this.Compact);
    }

    internal void SelectRows(IEnumerable<FavoriteItemId> items)
    {
        var selected = items.ToArray();
        if (!this.TreeSelection && this.Selected.SequenceEqual(selected))
        {
            return;
        }

        this.TreeSelection = false;
        this.Selected = selected;
        this.NotifyReorderCommands();
        this.SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SelectTree(FavoriteItemId item)
    {
        var folderId = item.IsFolder ? item.Id.Length == 0 ? null : item.Id : this.Store.Bookmark(item.Id).FolderId;
        var clearQuery = item.IsFolder && !this.Compact && this.Query.Length > 0;
        var refresh = this.FolderId != folderId || clearQuery;
        this.FolderId = folderId;
        if (clearQuery)
        {
            this.Query = string.Empty;
        }

        this.TreeSelection = true;
        this.Selected = item.Id.Length == 0 ? [] : (FavoriteItemId[])[item];
        if (refresh)
        {
            this.Refresh(false);
        }
        else
        {
            this.NotifyReorderCommands();
            this.SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void OpenFolder(string id)
    {
        this.Query = string.Empty;
        this.FolderId = id.Length == 0 ? null : id;
        this.ExpandedFolders.Add(id);
        this.TreeSelection = true;
        this.Selected = id.Length == 0 ? [] : (FavoriteItemId[])[new FavoriteItemId(id, true)];
        this.Refresh();
    }

    internal void CopyFrom(FavoritesViewModel source)
    {
        this.Query = source.Query;
        this.FolderId = source.FolderId;
        this.ExpandedFolders = [.. source.ExpandedFolders];
        if (this.Compact)
        {
            this.ExpandedFolders.Add(this.FolderId ?? string.Empty);
        }

        this.Selected = this.Compact ? source.Selected.Take(1).ToArray() : source.Selected.ToArray();
        this.TreeSelection = this.Compact || source.TreeSelection && this.Selected.All(item => item.IsFolder);
        this.Refresh();
        this.ShowDetails = source.ShowDetails;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync()
    {
        return this.CanAdd
            ? this.RunAsync(
                this.AddLabel, () => AddFavoriteAsync(this.Store, dialogs, this.Compact ? this.CurrentLocation : null, this.FolderId))
            : Task.CompletedTask;
    }

    internal static async Task AddFavoriteAsync(
        FavoritesStore store,
        IFavoriteDialogs dialogs,
        RegistryLocation? current,
        string? folderId,
        bool onlyIfMissing = false)
    {
        var draft = new Favorite(
            current?.Name ?? string.Empty, current?.Address ?? string.Empty, current?.View == RegistryView.Registry32, folderId);
        string? error = null;
        while (await dialogs.EditFavoriteAsync(draft, current is not null, store.FolderChoices(), error) is { } edited)
        {
            draft = edited;
            try
            {
                if (onlyIfMissing && current is not null && store.Contains(current))
                {
                    return;
                }

                store.Add(draft.Name, draft.Address, draft.Is32Bit, draft.FolderId);
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(Available))]
    private Task NewFolderAsync()
    {
        return this.RunAsync(
            "New folder", async () =>
            {
                if (await dialogs.NewFolderAsync(this.Store.FolderChoices(), this.FolderId) is { } folder)
                {
                    this.OpenFolder(this.Store.AddFolder(folder.Name, folder.ParentId));
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanUseSingleItem))]
    private Task OpenAsync()
    {
        return this.RunAsync(
            "Open", () =>
            {
                if (this.Selected is [var item])
                {
                    if (!item.IsFolder)
                    {
                        return navigate(this.Location(item), false);
                    }

                    this.OpenFolder(item.Id);
                }

                return Task.CompletedTask;
            });
    }

    [RelayCommand(CanExecute = nameof(CanOpenNewTab))]
    private Task OpenNewTabAsync()
    {
        return this.RunAsync(
            "Open in new tab",
            () => this.Selected is [{ IsFolder: false } item] ? navigate(this.Location(item), true) : Task.CompletedTask);
    }

    [RelayCommand(CanExecute = nameof(CanUseSingleItem))]
    private Task RenameAsync()
    {
        return this.RunAsync(
            "Rename", async () =>
            {
                if (this.Selected is not [var item])
                {
                    return;
                }

                var name = item.IsFolder
                    ? this.Store.Folders.First(folder => folder.Id == item.Id).Name
                    : this.Store.Bookmark(item.Id).Name;
                if (await dialogs.RenameAsync(name, item.IsFolder) is { } renamed)
                {
                    this.Store.Rename(item, renamed);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private Task MoveToFolderAsync()
    {
        return this.RunAsync(
            "Move to folder", async () =>
            {
                var items = this.Selected;
                var excluded = this.Store.Subtree(FavoritesStore.FolderIds(items));
                if (items.Count > 0 && await dialogs.ChooseFolderAsync(this.Store.FolderChoices(excluded), this.FolderId) is { } folder)
                {
                    this.Store.Move(items, folder.Id.Length == 0 ? null : folder.Id);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync()
    {
        return this.ReorderAsync(-1);
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync()
    {
        return this.ReorderAsync(1);
    }

    private Task ReorderAsync(int offset)
    {
        return this.RunAsync(
            offset < 0 ? "Move up" : "Move down", () =>
            {
                if (this.CanMove(offset))
                {
                    this.Store.Reorder(this.Selected[0], offset);
                }

                return Task.CompletedTask;
            });
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private Task RemoveAsync()
    {
        return this.RunAsync(
            "Remove selected", async () =>
            {
                var items = this.Selected;
                if (items.Count == 0)
                {
                    return;
                }

                var folders = this.Store.Subtree(FavoritesStore.FolderIds(items));
                var bookmarks = items.Where(item => !item.IsFolder).Select(item => item.Id).ToHashSet();
                var count = this.Store.Favorites.Count(item =>
                    bookmarks.Contains(item.Id) || item.FolderId is { } id && folders.Contains(id));
                if (await dialogs.ConfirmRemoveAsync(count, folders.Count))
                {
                    this.Store.Remove(items);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(Available))]
    private void ToggleDetails()
    {
        this.Error = null;
        this.ShowDetails = !this.ShowDetails;
    }

    [RelayCommand(CanExecute = nameof(CanOpenBackground))]
    private Task OpenBackgroundAsync(FavoriteItemId? item)
    {
        return this.RunAsync(
            "Open favorite in background", () =>
            {
                if (item is { IsFolder: false } bookmark)
                {
                    openBackground(this.Location(bookmark));
                }

                return Task.CompletedTask;
            });
    }

    private bool CanOpenBackground(FavoriteItemId? item)
    {
        return this.Available && item is { IsFolder: false };
    }

    private async Task RunAsync(string operation, Func<Task> execute)
    {
        if (this.IsBusy)
        {
            return;
        }

        this.IsBusy = true;
        try
        {
            this.Error = null;
            await execute();
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            this.ActionFailed?.Invoke(operation, ex);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private void NotifyReorderCommands()
    {
        this.MoveUpCommand.NotifyCanExecuteChanged();
        this.MoveDownCommand.NotifyCanExecuteChanged();
    }

    private bool CanMove(int offset)
    {
        if (this.Query.Length > 0 || this.Selected is not [var selected])
        {
            return false;
        }

        var parent = selected.IsFolder
            ? this.Store.Folders.FirstOrDefault(folder => folder.Id == selected.Id)?.ParentId
            : this.Store.Favorites.FirstOrDefault(item => item.Id == selected.Id)?.FolderId;
        var siblings = selected.IsFolder
            ? this.Store.Folders.Where(folder => folder.ParentId == parent).Select(folder => folder.Id)
            : this.Store.Favorites.Where(item => item.FolderId == parent).Select(item => item.Id);
        var ids = siblings.ToArray();
        var index = Array.IndexOf(ids, selected.Id);
        return index >= 0 && index + offset >= 0 && index + offset < ids.Length;
    }

    internal RegistryLocation Location(FavoriteItemId item)
    {
        return this.Store.Bookmark(item.Id).GetLocation();
    }

    private void Refresh(bool rebuildTree = true)
    {
        var folders = this.Store.Folders;
        if (this.FolderId is not null && !folders.Any(folder => folder.Id == this.FolderId))
        {
            this.FolderId = null;
        }

        var byParent = folders.ToLookup(folder => folder.ParentId);
        var byFolder = this.Store.Favorites.ToLookup(item => item.FolderId);
        var searching = this.Query.Length > 0;

        bool Matches(FavoriteRow row)
        {
            return row.Title.Contains(this.Query, StringComparison.OrdinalIgnoreCase)
                   || row.Path.Contains(this.Query, StringComparison.OrdinalIgnoreCase)
                   || row.Subtitle.Contains(this.Query, StringComparison.OrdinalIgnoreCase);
        }

        FavoriteRow Bookmark(Favorite item, bool includeFolder)
        {
            return new FavoriteRow(
                new FavoriteItemId(item.Id, false), item.Name, item.Address + (item.Is32Bit ? " · 32-bit" : " · 64-bit"),
                includeFolder ? this.Store.FolderPath(item.FolderId) : string.Empty)
            {
                RegistryPath = item.Address, RegistryView = item.Is32Bit ? "32-bit" : "64-bit"
            };
        }

        FavoriteRow Folder(FavoriteFolder folder)
        {
            var id = folder.Id.Length == 0 ? null : folder.Id;
            var children = byParent[id].Select(Folder).Where(child => !this.Compact || child.Children.Count > 0 || Matches(child)).ToList();
            if (this.Compact)
            {
                children.AddRange(byFolder[id].Select(item => Bookmark(item, true)).Where(Matches));
            }

            return new FavoriteRow(new FavoriteItemId(folder.Id, true), folder.Name, this.Store.FolderPath(id), string.Empty)
            {
                Children = children
            };
        }

        if (rebuildTree)
        {
            this.Root = Folder(new FavoriteFolder(string.Empty, "Favorites"));
        }

        var rows = new List<FavoriteRow>();
        if (searching)
        {
            rows.AddRange(
                folders.Select(folder => new FavoriteRow(
                    new FavoriteItemId(folder.Id, true), folder.Name,
                    $"{byFolder[folder.Id].Count():N0} favorites · {byParent[folder.Id].Count():N0} folders",
                    this.Store.FolderPath(folder.Id))).Where(Matches));
        }

        var bookmarks = searching ? this.Store.Favorites : byFolder[this.FolderId];
        rows.AddRange(bookmarks.Select(item => Bookmark(item, searching)).Where(Matches));
        this.Rows = rows;
        var visible = this.TreeSelection ? TreeKeys(this.Root).ToHashSet() : rows.Select(row => row.Key).ToHashSet();
        this.Selected = this.Selected.Where(visible.Contains).ToArray();
        this.NotifyReorderCommands();
        this.ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<FavoriteItemId> TreeKeys(FavoriteRow row)
    {
        yield return row.Key;
        foreach (var child in row.Children)
        {
            foreach (var key in TreeKeys(child))
            {
                yield return key;
            }
        }
    }
}
