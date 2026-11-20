// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

internal sealed class FavoriteDialogs : IFavoriteDialogs
{
    private readonly WindowDialogPresenter _dialogs;

    internal FavoriteDialogs(WindowDialogPresenter dialogs)
    {
        this._dialogs = dialogs;
    }

    public async Task<Favorite?> EditFavoriteAsync(Favorite draft, bool fixedLocation, FavoriteFolder[] folders, string? error)
    {
        var form = new FavoriteForm(FavoriteFormKind.Add, folders, draft.FolderId, draft, fixedLocation);
        var dialog = new AppDialog("Add favorite", form, "Add");
        dialog.FocusOnOpen(form.InitialFocus);
        if (error is not null)
        {
            dialog.ShowError(error);
        }

        if (await this._dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        return draft with { Name = form.FavoriteName, Address = form.Address, Is32Bit = form.ViewIndex == 1, FolderId = form.FolderId };
    }

    public async Task<(string Name, string? ParentId)?> NewFolderAsync(FavoriteFolder[] folders, string? parentId)
    {
        var form = new FavoriteForm(FavoriteFormKind.NewFolder, folders, parentId);
        var dialog = new AppDialog("New favorites folder", form, "Create");
        dialog.FocusOnOpen(form.InitialFocus);
        return await this._dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? (form.FavoriteName, form.FolderId) : null;
    }

    public async Task<string?> RenameAsync(string name, bool isFolder)
    {
        var form = new NameForm("Name", name);
        var dialog = new AppDialog(isFolder ? "Rename folder" : "Rename favorite", form, "Save");
        dialog.FocusOnOpen(form.Input);
        return await this._dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? form.Value : null;
    }

    public async Task<FavoriteFolder?> ChooseFolderAsync(FavoriteFolder[] folders, string? folderId)
    {
        var form = new FavoriteForm(FavoriteFormKind.Move, folders, folderId);
        var dialog = new AppDialog("Move to folder", form, "Move");
        dialog.FocusOnOpen(form.InitialFocus);
        return await this._dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? form.Folder : null;
    }

    public async Task<bool> ConfirmRemoveAsync(int favorites, int folders)
    {
        var message
            = $"Remove {favorites:N0} favorites and {folders:N0} folders, including their contents? Registry keys will remain unchanged.";
        return await this._dialogs.ShowAsync(
                   new AppDialog("Remove favorites?", message, "Remove", defaultButton: ContentDialogButton.Close))
               == ContentDialogResult.Primary;
    }
}
