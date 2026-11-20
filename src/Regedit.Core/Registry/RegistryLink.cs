// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Registry;

public static class RegistryLink
{
    public static RegistryLocation? Resolve(RegistryValue? value, RegistryLocation source)
    {
        if (value is not { Type: RegistryValueType.Link, Data.Length: > 0 and <= ushort.MaxValue } || value.Data.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            var path = new UnicodeEncoding(false, false, true).GetString(value.Data).TrimEnd('\0');
            const string machine = @"\Registry\Machine";
            const string users = @"\Registry\User";
            string address;
            if (path.Equals(machine, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(machine + "\\", StringComparison.OrdinalIgnoreCase))
            {
                address = "HKLM" + path[machine.Length..];
            }
            else if (path.Equals(users, StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith(users + "\\", StringComparison.OrdinalIgnoreCase))
            {
                address = "HKU" + path[users.Length..];
            }
            else
            {
                return null;
            }

            var target = RegistryLocation.Parse(address, source.View) with { Machine = source.Machine };
            if (source.Machine is null && target.Hive == RegistryHive.Users)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User?.Value;
                if (sid is not null
                    && (target.SubKey.Equals(sid, StringComparison.OrdinalIgnoreCase)
                        || target.SubKey.StartsWith(sid + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    target = target with
                    {
                        Hive = RegistryHive.CurrentUser,
                        SubKey = target.SubKey.Length == sid.Length ? string.Empty : target.SubKey[(sid.Length + 1)..]
                    };
                }
            }

            return target;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string DisplayPath(RegistryLocation target)
    {
        var root = RegistryLocation.Roots.First(root => root.Hive == target.Hive).Alias;
        return (target.Machine is null ? string.Empty : $"\\\\{target.Machine}\\")
               + root
               + (target.SubKey.Length == 0 ? string.Empty : "\\" + target.SubKey);
    }
}
