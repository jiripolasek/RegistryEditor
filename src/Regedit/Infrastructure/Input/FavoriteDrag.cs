// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace JPSoftworks.Regedit.Infrastructure.Input;

internal sealed class FavoriteDrag
{
    private const string Format = "JPSoftworks.Regedit.Favorite";
    private (FavoriteItemId Item, string Token)? _source;

    internal void Start(FavoriteItemId item, DataPackage data)
    {
        var token = Guid.NewGuid().ToString("N");
        this._source = (item, token);
        data.RequestedOperation = DataPackageOperation.Move;
        data.SetData(Format, token);
    }

    internal FavoriteItemId? GetSource(DragEventArgs args)
    {
        if (this._source is not { } source || !args.DataView.Contains(Format))
        {
            return null;
        }

        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.None;
        return source.Item;
    }

    internal async Task CompleteDropAsync(DragEventArgs args, Func<Task> apply)
    {
        if (this._source is not { } source || this.GetSource(args) is null)
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            object data;
            try
            {
                data = await args.DataView.GetDataAsync(Format);
            }
            catch (Exception)
            {
                // An external provider can advertise the format without supplying valid data.
                return;
            }

            if (data is string token && token == source.Token && this._source == source)
            {
                await apply();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    internal void Clear()
    {
        this._source = null;
    }
}
