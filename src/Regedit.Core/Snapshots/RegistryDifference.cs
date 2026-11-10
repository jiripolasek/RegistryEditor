// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Snapshots;

public sealed record RegistryDifference(
    RegistryLocation Location,
    RegistryChangeKind Kind,
    RegistryValue? Before = null,
    RegistryValue? After = null)
{
    public string Name => (this.After ?? this.Before)?.DisplayName ?? "(Key)";

    public string Change => this.Kind switch
    {
        RegistryChangeKind.KeyAdded => "Key added",
        RegistryChangeKind.KeyRemoved => "Key removed",
        RegistryChangeKind.ValueAdded => "Value added",
        RegistryChangeKind.ValueRemoved => "Value removed",
        _ => this.Before!.Type != this.After!.Type ? "Type changed" : "Data changed"
    };

    public string Types => this.Before?.TypeName == this.After?.TypeName
        ? this.Before?.TypeName ?? "Key"
        : $"{this.Before?.TypeName ?? "Absent"} → {this.After?.TypeName ?? "Absent"}";

    public string BeforeText => Describe(this.Before, this.Kind == RegistryChangeKind.KeyRemoved);

    public string AfterText => Describe(this.After, this.Kind == RegistryChangeKind.KeyAdded);

    public override string ToString()
    {
        return $"{this.Change}: {this.Name}, {this.Location.Address}, {this.Types}";
    }

    private static string Describe(RegistryValue? value, bool key)
    {
        return value is not null ? value.DisplayData : key ? "Key exists" : "Absent";
    }
}
