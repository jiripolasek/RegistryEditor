// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed class RegistryListMetadata
{
    private readonly RegistryService _registry;
    private CancellationTokenSource? _active;

    internal RegistryListMetadata(RegistryService registry)
    {
        this._registry = registry;
    }

    internal void Cancel()
    {
        this._active?.Cancel();
    }

    internal async Task<bool> LoadAsync(IEnumerable<RegistryEntry> entries, bool includeLink)
    {
        this.Cancel();
        var rows = entries
            .Where(row => row.Key is { Machine: null } && (!row.DetailsRead || includeLink && !row.LinkRead)).ToArray();
        if (rows.Length == 0)
        {
            return false;
        }

        using var request = new CancellationTokenSource();
        this._active = request;
        try
        {
            var details = await Task.Run(
                () =>
                {
                    var result = new RegistryKeyDetails?[rows.Length];
                    for (var index = 0; index < rows.Length; index++)
                    {
                        request.Token.ThrowIfCancellationRequested();
                        try
                        {
                            result[index] = this._registry.ReadKeyDetails(rows[index].Key!, includeLink);
                        }
                        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
                                                       or SecurityException)
                        {
                            // A single denied or disappearing child must leave the rest of the list usable.
                        }
                    }

                    return result;
                }, request.Token);
            if (request.IsCancellationRequested)
            {
                return false;
            }

            for (var index = 0; index < rows.Length; index++)
            {
                rows[index].SetDetails(details[index], includeLink);
            }

            return true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(this._active, request))
            {
                this._active = null;
            }
        }
    }
}
