// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Import;

namespace JPSoftworks.Regedit.Models.Files;

public sealed partial record RegistryFilePreviewKey(string Name, RegistryFileKeyChange Change)
{
    public bool IsNormal => this.Change == RegistryFileKeyChange.None;

    public bool IsDeleted => this.Change == RegistryFileKeyChange.Delete;

    public bool IsRecreated => this.Change == RegistryFileKeyChange.Recreate;

    public string Label => this.Name + (this.Change switch
    {
        RegistryFileKeyChange.Delete => " (Delete)",
        RegistryFileKeyChange.Recreate => " (Delete, then recreate)",
        _ => ""
    });

    public string Help => this.Change switch
    {
        RegistryFileKeyChange.Delete =>
            "This file deletes the key, directly or through an ancestor, without recreating it.",
        RegistryFileKeyChange.Recreate =>
            "This file deletes the existing subtree, then recreates this key. Only later instructions restore its contents.",
        _ => this.Name
    };

    public override string ToString()
    {
        return this.Label;
    }
}
