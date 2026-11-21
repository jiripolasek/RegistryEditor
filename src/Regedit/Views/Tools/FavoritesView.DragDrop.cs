// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace JPSoftworks.Regedit.Views.Tools;

public sealed partial class FavoritesView
{
    private readonly FavoriteDrag _itemDrag = new();

    private bool CanDragFavorite => this.ViewModel.Query.Length == 0 && !this.ViewModel.IsBusy;

    private void FavoriteFolder_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is TreeViewNode { Content: FavoriteRow { Key.IsFolder: true } } node)
        {
            node.IsExpanded = !node.IsExpanded;
            args.Handled = true;
        }
    }

    private void FavoriteRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (!this.CanDragFavorite || ((FrameworkElement)sender).DataContext is not TreeViewNode
            { Content: FavoriteRow row })
        {
            args.Cancel = true;
            return;
        }

        this._itemDrag.Start(row.Key, args.Data);
        args.AllowedOperations = DataPackageOperation.Move;
    }

    private void FavoriteRow_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        this._itemDrag.Clear();
    }

    private void Results_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (!this.CanDragFavorite || args.Items is not [FavoriteRow row])
        {
            args.Cancel = true;
            return;
        }

        this._itemDrag.Start(row.Key, args.Data);
    }

    private void FavoriteRow_DragOver(object sender, DragEventArgs args)
    {
        var target = (FrameworkElement)sender;
        this.AcceptFavoriteReorder(args, target, (target.DataContext as TreeViewNode)?.Content as FavoriteRow,
            preview: true);
    }

    private async void FavoriteRow_Drop(object sender, DragEventArgs args)
    {
        await this._itemDrag.CompleteDropAsync(args, async () =>
        {
            var target = (FrameworkElement)sender;
            if (this.AcceptFavoriteReorder(args, target, (target.DataContext as TreeViewNode)?.Content as FavoriteRow)
                is { } move)
            {
                await this.ViewModel.ReorderAsync(move.Item, move.Offset);
            }
        });
    }

    private void Results_DragOver(object sender, DragEventArgs args)
    {
        var container = this.FavoriteResultAt(args);
        this.AcceptFavoriteReorder(args, container, container?.Content as FavoriteRow, preview: true);
    }

    private async void Results_Drop(object sender, DragEventArgs args)
    {
        await this._itemDrag.CompleteDropAsync(args, async () =>
        {
            var container = this.FavoriteResultAt(args);
            if (this.AcceptFavoriteReorder(args, container, container?.Content as FavoriteRow) is { } move)
            {
                await this.ViewModel.ReorderAsync(move.Item, move.Offset);
            }
        });
    }

    private ListViewItem? FavoriteResultAt(DragEventArgs args)
    {
        return VisualTreeHelper.FindElementsInHostCoordinates(args.GetPosition(this.XamlRoot.Content), this.Results)
            .OfType<ListViewItem>().FirstOrDefault();
    }

    private (FavoriteItemId Item, int Offset)? AcceptFavoriteReorder(
        DragEventArgs args, FrameworkElement? target, FavoriteRow? row, bool preview = false)
    {
        if (this._itemDrag.GetSource(args) is not { } item || !this.CanDragFavorite || target is null || row is null)
        {
            return null;
        }

        var after = args.GetPosition(target).Y >= target.ActualHeight / 2;
        var offset = this.ViewModel.Store.ReorderOffset(item, row.Key, after);
        if (offset == 0)
        {
            return null;
        }

        args.AcceptedOperation = DataPackageOperation.Move;
        if (preview)
        {
            args.DragUIOverride.Caption = after ? $"Move after {row.Title}" : $"Move before {row.Title}";
        }

        return (item, offset);
    }
}
