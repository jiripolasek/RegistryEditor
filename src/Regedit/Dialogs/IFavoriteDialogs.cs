// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.Dialogs;

internal interface IFavoriteDialogs
{
    Task<Favorite?> EditFavoriteAsync(Favorite draft, bool fixedLocation, FavoriteFolder[] folders, string? error);

    Task<(string Name, string? ParentId)?> NewFolderAsync(FavoriteFolder[] folders, string? parentId);

    Task<string?> RenameAsync(string name, bool isFolder);

    Task<FavoriteFolder?> ChooseFolderAsync(FavoriteFolder[] folders, string? folderId);

    Task<bool> ConfirmRemoveAsync(int favorites, int folders);
}
