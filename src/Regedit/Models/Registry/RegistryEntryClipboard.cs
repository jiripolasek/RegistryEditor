// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;

namespace JPSoftworks.Regedit.Models.Registry;

internal sealed class RegistryEntryClipboard : ObservableObject
{
    private RegistryEntry? _entry;

    internal RegistryEntry? Entry { get => this._entry; set => this.SetProperty(ref this._entry, value); }
}
