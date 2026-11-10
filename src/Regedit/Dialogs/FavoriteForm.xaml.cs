// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

internal enum FavoriteFormKind { Add, NewFolder, Move }

public sealed partial class FavoriteForm : UserControl
{
    public string FavoriteName { get; set; }

    public string Address { get; set; }

    public int ViewIndex { get; set; }

    public bool CanEditName { get; }

    public bool CanEditLocation { get; }

    public bool HasFixedLocation { get; }

    internal FavoriteFolder Folder => (FavoriteFolder)this.FolderInput.SelectedItem;

    internal string? FolderId => this.Folder.Id is { Length: > 0 } id ? id : null;

    internal Control InitialFocus =>
        this.CanEditLocation ? this.AddressInput : this.CanEditName ? this.NameInput : this.FolderInput;

    internal FavoriteForm(
        FavoriteFormKind kind,
        FavoriteFolder[] folders,
        string? folderId,
        Favorite? draft = null,
        bool fixedLocation = false)
    {
        this.FavoriteName = draft?.Name ?? string.Empty;
        this.Address = draft?.Address ?? string.Empty;
        this.ViewIndex = draft?.Is32Bit == true ? 1 : 0;
        this.CanEditName = kind != FavoriteFormKind.Move;
        this.CanEditLocation = kind == FavoriteFormKind.Add && !fixedLocation;
        this.HasFixedLocation = kind == FavoriteFormKind.Add && fixedLocation;
        this.InitializeComponent();
        this.FolderInput.ItemsSource = folders;
        this.FolderInput.SelectedItem = folders.FirstOrDefault(folder => folder.Id == folderId) ?? folders[0];
    }
}
