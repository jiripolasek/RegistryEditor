// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Registry;

public sealed record RegistryLocation(
    RegistryHive? Hive = null,
    string SubKey = "",
    string? Machine = null,
    RegistryView View = RegistryView.Registry64)
{
    public static readonly (RegistryHive Hive, string Name, string Alias)[] Roots =
    [
        (RegistryHive.ClassesRoot, "HKEY_CLASSES_ROOT", "HKCR"),
        (RegistryHive.CurrentUser, "HKEY_CURRENT_USER", "HKCU"),
        (RegistryHive.LocalMachine, "HKEY_LOCAL_MACHINE", "HKLM"),
        (RegistryHive.Users, "HKEY_USERS", "HKU"),
        (RegistryHive.CurrentConfig, "HKEY_CURRENT_CONFIG", "HKCC")
    ];

    public bool IsComputer => this.Hive is null;

    public bool IsSameLocation(RegistryLocation? other)
    {
        return other is not null && this.Hive == other.Hive && this.View == other.View &&
               string.Equals(this.Machine, other.Machine, StringComparison.OrdinalIgnoreCase) && string.Equals(
                   this.SubKey, other.SubKey, StringComparison.OrdinalIgnoreCase);
    }

    public RegistryLocation GetComputer()
    {
        return this with { Hive = null, SubKey = string.Empty };
    }

    public RegistryLocation? GetNavigationParent()
    {
        return this.IsComputer ? null : this.Parent ?? this.GetComputer();
    }

    public RegistryLocation? GetAlternateHiveLocation()
    {
        return this.Machine is not null
            ? null
            : this.Hive switch
            {
                RegistryHive.LocalMachine => this with { Hive = RegistryHive.CurrentUser },
                RegistryHive.CurrentUser => this with { Hive = RegistryHive.LocalMachine },
                _ => null
            };
    }

    internal bool IsAtOrBelow(RegistryLocation root)
    {
        return this.Hive == root.Hive && this.View == root.View &&
               string.Equals(this.Machine, root.Machine, StringComparison.OrdinalIgnoreCase) &&
               (root.SubKey.Length == 0 || this.SubKey.Equals(root.SubKey, StringComparison.OrdinalIgnoreCase) ||
                this.SubKey.StartsWith(root.SubKey + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public string RootName =>
        this.IsComputer ? this.Machine ?? "Computer" : Roots.First(root => root.Hive == this.Hive).Name;

    public string Path => this.RootName + (this.SubKey.Length == 0 ? string.Empty : "\\" + this.SubKey);

    public string Address => this.IsComputer
        ? this.Machine is null ? "Computer" : $"\\\\{this.Machine}"
        : (this.Machine is null ? string.Empty : $"\\\\{this.Machine}\\") + this.Path;

    public override string ToString()
    {
        return this.Address;
    }

    public string? FormatPath(RegistryPathFormat format)
    {
        if (this.IsComputer || this.Machine is not null)
        {
            return null;
        }

        return format switch
        {
            RegistryPathFormat.PowerShellDrive => this.Hive is RegistryHive.CurrentUser or RegistryHive.LocalMachine
                ? Roots.First(root => root.Hive == this.Hive).Alias + ":\\" + this.SubKey
                : null,
            RegistryPathFormat.PowerShellProvider => "Registry::" + this.Path,
            RegistryPathFormat.RegFileHeader => this.SubKey.Contains('\r') || this.SubKey.Contains('\n')
                ? null
                : $"[{this.Path}]",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    public string Name => this.SubKey.Length == 0 ? this.RootName : this.SubKey[(this.SubKey.LastIndexOf('\\') + 1)..];

    public RegistryLocation? Parent => this.SubKey.Length == 0
        ? null
        : this with
        {
            SubKey = this.SubKey.Contains('\\') ? this.SubKey[..this.SubKey.LastIndexOf('\\')] : string.Empty
        };

    public RegistryLocation Child(string name)
    {
        if (this.IsComputer)
        {
            throw new InvalidOperationException("Select a registry hive or key first.");
        }

        ValidateKeyName(name);
        return this with { SubKey = this.SubKey.Length == 0 ? name : this.SubKey + "\\" + name };
    }

    public RegistryLocation[] GetHives()
    {
        return Roots.Where(root => this.Machine is null || root.Hive is RegistryHive.LocalMachine or RegistryHive.Users)
            .Select(root => new RegistryLocation(root.Hive, string.Empty, this.Machine, this.View)).ToArray();
    }

    public RegistryLocation[] GetScopeRoots(bool allHives = false)
    {
        return this.IsComputer || allHives
            ?
            [
                this with { Hive = RegistryHive.LocalMachine, SubKey = string.Empty },
                this with { Hive = RegistryHive.Users, SubKey = string.Empty }
            ]
            : [this];
    }

    public static void ValidateKeyName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Contains('\\') || name.Contains('\0'))
        {
            throw new ArgumentException(
                "A key name must contain 1 to 255 characters and cannot contain a backslash or a null character.");
        }
    }

    public static RegistryLocation Parse(string address, RegistryView view = RegistryView.Registry64)
    {
        address = address.TrimStart();
        // Only trim padding outside explicit wrappers; trailing key whitespace is significant.
        if (address.Length > 0 && address[0] is '[' or '"' or '\'')
        {
            address = address.TrimEnd();
        }

        if (address.Length >= 2 && address[0] == address[^1] && address[0] is '"' or '\'')
        {
            address = address[1..^1];
        }

        if (address.StartsWith('['))
        {
            if (!address.EndsWith(']'))
            {
                throw new ArgumentException("A registry file header must end with ].");
            }

            address = address[1..^1];
        }

        const string qualifiedProvider = @"Microsoft.PowerShell.Core\Registry::";
        if (address.StartsWith(qualifiedProvider, StringComparison.OrdinalIgnoreCase))
        {
            address = address[qualifiedProvider.Length..];
        }
        else if (address.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase))
        {
            address = address[10..];
        }

        address = address.TrimEnd('\\');
        if (address.Equals("Computer", StringComparison.OrdinalIgnoreCase))
        {
            return new RegistryLocation(View: view);
        }

        if (address.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase))
        {
            address = address[9..];
        }

        string? machine = null;
        if (address.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var separator = address.IndexOf('\\', 2);
            machine = separator < 0 ? address[2..] : address[2..separator];
            if (machine.Length == 0 || machine.Any(c => char.IsWhiteSpace(c) || c is '/' or '\0'))
            {
                throw new ArgumentException("The computer name is not valid.");
            }

            if (separator < 0)
            {
                return new RegistryLocation(Machine: machine, View: view);
            }

            address = address[(separator + 1)..];
        }

        address = address.TrimEnd('\\');
        var split = address.IndexOf('\\');
        var root = split < 0 ? address : address[..split];
        if (root.EndsWith(':'))
        {
            root = root[..^1];
        }

        var subKey = split < 0 ? string.Empty : address[(split + 1)..];
        var match = Roots.FirstOrDefault(r => r.Name.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                                              r.Alias.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (match.Name is null)
        {
            throw new ArgumentException("Start the address with a registry hive, for example HKCU\\Software.");
        }

        if (subKey.Contains('\0') || subKey.Contains("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The registry path is not valid.");
        }

        if (machine is not null && match.Hive is not (RegistryHive.LocalMachine or RegistryHive.Users))
        {
            throw new ArgumentException("Remote connections support HKEY_LOCAL_MACHINE and HKEY_USERS.");
        }

        return new RegistryLocation(match.Hive, subKey, machine, view);
    }
}
