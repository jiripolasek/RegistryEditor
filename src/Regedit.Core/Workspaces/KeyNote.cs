// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Workspaces;

public sealed record KeyNote(RegistryLocation Location, string Text)
{
    public bool Matches(RegistryLocation location)
    {
        return this.Location.Hive == location.Hive && this.Location.View == location.View &&
               this.Location.SubKey.Equals(location.SubKey, StringComparison.OrdinalIgnoreCase) && string.Equals(
                   this.Location.Machine, location.Machine, StringComparison.OrdinalIgnoreCase);
    }
}
