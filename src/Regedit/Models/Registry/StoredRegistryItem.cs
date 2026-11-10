// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;


namespace JPSoftworks.Regedit.Models.Registry;

internal abstract record StoredRegistryItem
{
    internal sealed record Key(RegistryLocation Location) : StoredRegistryItem
    {
        internal override string Path => this.Location.Address;

        internal override string Name => this.Location.Name;

        internal override void Rename(
            RegistryService registry,
            string name,
            CancellationToken cancellationToken = default)
        {
            registry.RenameKey(this.Location, name, cancellationToken);
        }

        internal override void Delete(RegistryService registry, CancellationToken cancellationToken = default)
        {
            registry.DeleteKey(this.Location, cancellationToken);
        }
    }

    internal sealed record Value(RegistryLocation Parent, RegistryValue Original) : StoredRegistryItem
    {
        internal override string Path => this.Parent.Address + "\\" + this.Original.DisplayName;

        internal override string Name => this.Original.Name;

        internal override void Rename(
            RegistryService registry,
            string name,
            CancellationToken cancellationToken = default)
        {
            registry.RenameValue(this.Parent, this.Original, name, cancellationToken);
        }

        internal override void Delete(RegistryService registry, CancellationToken cancellationToken = default)
        {
            registry.DeleteValue(this.Parent, this.Original, cancellationToken);
        }
    }

    internal static StoredRegistryItem? FromRow(RegistryLocation parent, RegistryEntry row)
    {
        return row switch
        {
            { Key: { } key } => new Key(key),
            { Value: { } value } => new Value(parent, value),
            _ => null
        };
    }

    internal abstract string Path { get; }

    internal abstract string Name { get; }

    internal bool CanDelete => this is not Key { Location.Parent: null };

    internal bool CanRename => this.CanDelete && this.Name.Length > 0;

    internal abstract void Rename(RegistryService registry, string name, CancellationToken cancellationToken = default);

    internal abstract void Delete(RegistryService registry, CancellationToken cancellationToken = default);
}
