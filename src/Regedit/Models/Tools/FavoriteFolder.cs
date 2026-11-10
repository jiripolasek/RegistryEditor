// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Tools;

public sealed record FavoriteFolder(string Id, string Name, string? ParentId = null)
{
    public override string ToString()
    {
        return this.Name;
    }
}
