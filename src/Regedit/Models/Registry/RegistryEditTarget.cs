// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Models.Registry;

internal sealed record RegistryEditTarget(RegistryLocation Location, string? ValueName = null)
{
    internal string Description => this.Location.Address +
                                   (this.ValueName is null
                                       ? string.Empty
                                       : "\nValue: " + (this.ValueName.Length == 0 ? "(Default)" : this.ValueName)) +
                                   (this.Location.View == RegistryView.Registry32
                                       ? "\n32-bit registry view"
                                       : "\n64-bit registry view");
}
