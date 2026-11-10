// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Persistence;

public sealed partial class AppSettings
{
    internal IReadOnlySet<string> HiddenJumpListFavorites { get; private set; }
        = new HashSet<string>(StringComparer.Ordinal);

    internal void HideJumpListFavorites(IReadOnlySet<string> ids, string? filePath = null)
    {
        if (ids.All(this.HiddenJumpListFavorites.Contains))
        {
            return;
        }

        var previous = this.HiddenJumpListFavorites;
        this.HiddenJumpListFavorites = previous.Concat(ids).ToHashSet(StringComparer.Ordinal);
        try
        {
            this.Save(filePath ?? FilePath);
        }
        catch
        {
            this.HiddenJumpListFavorites = previous;
            throw;
        }
    }
}
