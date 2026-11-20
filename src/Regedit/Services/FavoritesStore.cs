// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;


namespace JPSoftworks.Regedit.Services;

internal sealed class FavoritesStore
{
    internal event EventHandler? Changed;

    private readonly AppSettings _settings;
    private readonly Action _save;

    internal IReadOnlyList<Favorite> Favorites => this._settings.Favorites;

    internal IReadOnlyList<FavoriteFolder> Folders => this._settings.FavoriteFolders;

    internal FavoritesStore(AppSettings settings, Action save)
    {
        this._settings = settings;
        this._save = save;
    }

    internal string FolderPath(string? id)
    {
        return this._settings.FavoriteFolderPath(id);
    }

    internal bool Contains(RegistryLocation location)
    {
        return this.Favorites.Any(item => item.Matches(location));
    }

    internal HashSet<string> Subtree(IEnumerable<string> ids)
    {
        return this._settings.FavoriteFolderSubtree(ids);
    }

    internal FavoriteFolder[] FolderChoices(IReadOnlySet<string>? excluded = null)
    {
        return this._settings.FavoriteFolderChoices(excluded);
    }

    internal Favorite Bookmark(string id)
    {
        return this.Favorites.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("This favorite no longer exists.");
    }

    internal void Add(string name, string address, bool is32Bit, string? folderId)
    {
        this.Change(() => this._settings.AddFavorite(name, address, is32Bit, folderId));
    }

    internal string AddFolder(string name, string? parent)
    {
        var id = string.Empty;
        this.Change(() => id = this._settings.AddFavoriteFolder(name, parent).Id);
        return id;
    }

    internal void Rename(FavoriteItemId item, string name)
    {
        this.Change(() =>
        {
            if (item.IsFolder)
            {
                this._settings.RenameFavoriteFolder(item.Id, name);
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Enter a name.");
            }

            var favorite = this.Bookmark(item.Id);
            this._settings.Favorites[this._settings.FavoriteIndex(favorite)] = favorite with { Name = name.Trim() };
        });
    }

    internal void Move(IReadOnlyList<FavoriteItemId> items, string? destination)
    {
        this.Change(() => this._settings.MoveFavoriteItems(this.Bookmarks(items), FolderIds(items), destination));
    }

    internal void Remove(IReadOnlyList<FavoriteItemId> items)
    {
        this.Change(() => this._settings.RemoveFavoriteItems(this.Bookmarks(items), FolderIds(items)));
    }

    internal void RemoveLocation(RegistryLocation location)
    {
        this.Change(() => this._settings.RemoveFavoriteItems(this.Favorites.Where(item => item.Matches(location)).ToArray(), []));
    }

    internal void Reorder(FavoriteItemId item, int offset)
    {
        this.Change(() =>
        {
            if (item.IsFolder)
            {
                this._settings.ReorderFavoriteFolder(item.Id, offset);
            }
            else
            {
                this._settings.ReorderFavorite(this.Bookmark(item.Id), offset);
            }
        });
    }

    internal static string[] FolderIds(IEnumerable<FavoriteItemId> items)
    {
        return items.Where(item => item.IsFolder).Select(item => item.Id).ToArray();
    }

    private Favorite[] Bookmarks(IEnumerable<FavoriteItemId> items)
    {
        return items.Where(item => !item.IsFolder).Select(item => this.Bookmark(item.Id)).ToArray();
    }

    private void Change(Action change)
    {
        var folders = this.Folders.ToArray();
        var favorites = this.Favorites.ToArray();
        try
        {
            change();
            this._save();
        }
        catch
        {
            this._settings.FavoriteFolders.Clear();
            this._settings.Favorites.Clear();
            foreach (var folder in folders)
            {
                this._settings.FavoriteFolders.Add(folder);
            }

            foreach (var favorite in favorites)
            {
                this._settings.Favorites.Add(favorite);
            }

            throw;
        }
        finally
        {
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
