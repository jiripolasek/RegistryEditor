// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Models.Tools;

public sealed record Favorite(
    string Name,
    string Address,
    bool Is32Bit = false,
    string? FolderId = null,
    string? Id = null)
{
    public string Id { get; init; } = Id ?? Guid.NewGuid().ToString("N");

    public bool Matches(RegistryLocation location)
    {
        var saved = RegistryLocation.Parse(
            this.Address, this.Is32Bit ? RegistryView.Registry32 : RegistryView.Registry64);
        return saved.Hive == location.Hive && saved.View == location.View &&
               saved.SubKey.Equals(location.SubKey, StringComparison.OrdinalIgnoreCase) && string.Equals(
                   saved.Machine, location.Machine, StringComparison.OrdinalIgnoreCase);
    }
}
