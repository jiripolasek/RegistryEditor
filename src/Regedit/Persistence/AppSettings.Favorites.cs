// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.Persistence;

public sealed partial class AppSettings
{
    public ObservableCollection<FavoriteFolder> FavoriteFolders { get; } = [];

    public Favorite AddFavorite(string name, string address, bool is32Bit, string? folderId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Enter a name for the favorite.");
        }

        this.RequireFavoriteFolder(folderId);
        var location = RegistryLocation.Parse(
            address, is32Bit ? Microsoft.Win32.RegistryView.Registry32 : Microsoft.Win32.RegistryView.Registry64);
        var favorite = new Favorite(name.Trim(), location.Address, is32Bit, folderId);
        this.Favorites.Add(favorite);
        return favorite;
    }

    public FavoriteFolder[] FavoriteFolderTrail(string? id)
    {
        var trail = new List<FavoriteFolder>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (id is not null && visited.Add(id) && this.FavoriteFolders.FirstOrDefault(folder => folder.Id == id) is { } folder)
        {
            trail.Add(folder);
            id = folder.ParentId;
        }

        trail.Reverse();
        return trail.ToArray();
    }

    public string FavoriteFolderPath(string? id)
    {
        return string.Join(" / ", new[] { "Favorites" }.Concat(this.FavoriteFolderTrail(id).Select(folder => folder.Name)));
    }

    public FavoriteFolder[] FavoriteFolderChoices(IReadOnlySet<string>? excluded = null)
    {
        return this.FavoriteFolders.Where(folder => excluded?.Contains(folder.Id) != true)
            .Select(folder => folder with { Name = this.FavoriteFolderPath(folder.Id) })
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).Prepend(new FavoriteFolder(string.Empty, "Favorites"))
            .ToArray();
    }

    public FavoriteFolder AddFavoriteFolder(string name, string? parentId)
    {
        this.RequireFavoriteFolder(parentId);
        var folder = new FavoriteFolder(Guid.NewGuid().ToString("N"), this.ValidateFolderName(name, parentId), parentId);
        this.FavoriteFolders.Add(folder);
        return folder;
    }

    public FavoriteFolder RenameFavoriteFolder(string id, string name)
    {
        var folder = this.RequireFavoriteFolder(id)!;
        var renamed = folder with { Name = this.ValidateFolderName(name, folder.ParentId, id) };
        this.FavoriteFolders[this.FavoriteFolders.IndexOf(folder)] = renamed;
        return renamed;
    }

    public HashSet<string> FavoriteFolderSubtree(IEnumerable<string> ids)
    {
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(ids);
        while (pending.TryDequeue(out var id))
        {
            this.RequireFavoriteFolder(id);
            if (!descendants.Add(id))
            {
                continue;
            }

            foreach (var child in this.FavoriteFolders.Where(folder => folder.ParentId == id))
            {
                pending.Enqueue(child.Id);
            }
        }

        return descendants;
    }

    public void MoveFavoriteItems(IReadOnlyCollection<Favorite> favorites, IReadOnlyCollection<string> folderIds, string? destination)
    {
        this.RequireFavoriteFolder(destination);
        foreach (var favorite in favorites)
        {
            _ = this.FavoriteIndex(favorite);
        }

        var descendants = this.FavoriteFolderSubtree(folderIds);
        if (destination is not null && descendants.Contains(destination))
        {
            throw new ArgumentException("A folder cannot be moved into itself or one of its subfolders.");
        }

        var roots = this.FavoriteFolders.Where(folder =>
            folderIds.Contains(folder.Id) && (folder.ParentId is null || !descendants.Contains(folder.ParentId))).ToArray();
        var names = this.FavoriteFolders.Where(folder => folder.ParentId == destination && !roots.Contains(folder))
            .Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in roots)
        {
            if (!names.Add(folder.Name))
            {
                throw new ArgumentException($"A folder named '{folder.Name}' already exists in the destination.");
            }
        }

        foreach (var folder in roots)
        {
            this.FavoriteFolders[this.FavoriteFolders.IndexOf(folder)] = folder with { ParentId = destination };
        }

        foreach (var favorite in favorites.Distinct<Favorite>(ReferenceEqualityComparer.Instance))
        {
            if (favorite.FolderId == destination || favorite.FolderId is not null && descendants.Contains(favorite.FolderId))
            {
                continue;
            }

            this.Favorites[this.FavoriteIndex(favorite)] = favorite with { FolderId = destination };
        }
    }

    public void RemoveFavoriteItems(IReadOnlyCollection<Favorite> favorites, IReadOnlyCollection<string> folderIds)
    {
        foreach (var favorite in favorites)
        {
            _ = this.FavoriteIndex(favorite);
        }

        var descendants = this.FavoriteFolderSubtree(folderIds);
        var selected = favorites.ToHashSet<Favorite>(ReferenceEqualityComparer.Instance);
        var folders = this.FavoriteFolders.Where(folder => descendants.Contains(folder.Id))
            .OrderByDescending(folder => this.FavoriteFolderTrail(folder.Id).Length).ToArray();
        for (var index = this.Favorites.Count - 1; index >= 0; index--)
        {
            if (selected.Contains(this.Favorites[index]) || this.Favorites[index].FolderId is { } id && descendants.Contains(id))
            {
                this.Favorites.RemoveAt(index);
            }
        }

        foreach (var folder in folders)
        {
            this.FavoriteFolders.Remove(folder);
        }
    }

    public void ReorderFavorite(Favorite favorite, int offset)
    {
        var index = this.FavoriteIndex(favorite);
        var siblings = this.Favorites.Where(item => item.FolderId == favorite.FolderId).ToList();
        var next = siblings.FindIndex(item => ReferenceEquals(item, favorite)) + offset;
        if (next >= 0 && next < siblings.Count)
        {
            this.Favorites.Move(index, this.FavoriteIndex(siblings[next]));
        }
    }

    public void ReorderFavoriteFolder(string id, int offset)
    {
        var folder = this.RequireFavoriteFolder(id)!;
        var siblings = this.FavoriteFolders.Where(item => item.ParentId == folder.ParentId).ToArray();
        var next = Array.IndexOf(siblings, folder) + offset;
        if (next >= 0 && next < siblings.Length)
        {
            this.FavoriteFolders.Move(this.FavoriteFolders.IndexOf(folder), this.FavoriteFolders.IndexOf(siblings[next]));
        }
    }

    internal int FavoriteIndex(Favorite favorite)
    {
        for (var index = 0; index < this.Favorites.Count; index++)
        {
            if (ReferenceEquals(this.Favorites[index], favorite))
            {
                return index;
            }
        }

        throw new ArgumentException("This favorite no longer exists.");
    }

    private FavoriteFolder? RequireFavoriteFolder(string? id)
    {
        return id is null
            ? null
            : this.FavoriteFolders.FirstOrDefault(folder => folder.Id == id)
              ?? throw new ArgumentException("This favorites folder no longer exists.");
    }

    private string ValidateFolderName(string name, string? parentId, string? currentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Enter a name for the folder.");
        }

        name = name.Trim();
        if (this.FavoriteFolders.Any(folder =>
                folder.ParentId == parentId && folder.Id != currentId && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A folder with this name already exists here.");
        }

        return name;
    }

    private static void ValidateFavorites(IEnumerable<Favorite> favorites, IEnumerable<FavoriteFolder> folders)
    {
        var byId = new Dictionary<string, FavoriteFolder>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (folder is null || string.IsNullOrWhiteSpace(folder.Id) || string.IsNullOrWhiteSpace(folder.Name))
            {
                throw new ArgumentException("A favorites folder is missing its identifier or name.");
            }

            if (!byId.TryAdd(folder.Id, folder))
            {
                throw new ArgumentException("Favorites folders have duplicate identifiers.");
            }
        }

        foreach (var siblings in byId.Values.GroupBy(folder => folder.ParentId))
        {
            if (siblings.Select(folder => folder.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != siblings.Count())
            {
                throw new ArgumentException("Favorites folders have duplicate names in the same folder.");
            }
        }

        var validated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in byId.Values)
        {
            var trail = new HashSet<string>(StringComparer.Ordinal);
            for (string? id = folder.Id; id is not null && !validated.Contains(id);)
            {
                if (!byId.TryGetValue(id, out var ancestor))
                {
                    throw new ArgumentException("A favorites folder has a missing parent.");
                }

                if (!trail.Add(id))
                {
                    throw new ArgumentException("Favorites folders contain a cycle.");
                }

                id = ancestor.ParentId;
            }

            validated.UnionWith(trail);
        }

        var favoriteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var favorite in favorites)
        {
            if (favorite is null || string.IsNullOrWhiteSpace(favorite.Name) || string.IsNullOrWhiteSpace(favorite.Address))
            {
                throw new ArgumentException("A saved favorite is missing its name or address.");
            }

            if (string.IsNullOrWhiteSpace(favorite.Id) || !favoriteIds.Add(favorite.Id))
            {
                throw new ArgumentException("Saved favorites have missing or duplicate identifiers.");
            }

            _ = RegistryLocation.Parse(favorite.Address);
            if (favorite.FolderId is not null && !byId.ContainsKey(favorite.FolderId))
            {
                throw new ArgumentException("A favorite refers to a missing folder.");
            }
        }
    }
}
