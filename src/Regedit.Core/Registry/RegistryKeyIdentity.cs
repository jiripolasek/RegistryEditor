// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Registry;

internal static class RegistryKeyIdentity
{
    private const string Users = @"\REGISTRY\USER";
    private const string Machine = @"\REGISTRY\MACHINE";

    internal static bool SameComputer(string? first, string? second)
    {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) || IsLocal(first) && IsLocal(second);
    }

    internal static bool IsLocal(string? machine)
    {
        return machine is null or "." or "127.0.0.1" or "::1"
               || string.Equals(machine, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string[] Paths(RegistryLocation location)
    {
        if (!IsLocal(location.Machine))
        {
            return
            [
                location.Hive == RegistryHive.Users ? ExpandUserClasses(Join(Users, location.SubKey)) : Join(Machine, location.SubKey)
            ];
        }

        location = location with { Machine = null };
        if (location.Hive == RegistryHive.ClassesRoot)
        {
            var classes = Join(@"Software\Classes", location.SubKey);
            return
            [
                Resolve(location with { Hive = RegistryHive.CurrentUser, SubKey = classes }),
                Resolve(location with { Hive = RegistryHive.LocalMachine, SubKey = classes })
            ];
        }

        if (location.Hive == RegistryHive.CurrentConfig)
        {
            location = location with
            {
                Hive = RegistryHive.LocalMachine, SubKey = Join(@"SYSTEM\CurrentControlSet\Hardware Profiles\Current", location.SubKey)
            };
        }

        return [Resolve(location)];
    }

    private static string Resolve(RegistryLocation location)
    {
        using var root = RegistryKey.OpenBaseKey(location.Hive!.Value, location.View);
        var path = location.SubKey;
        while (path.Length != 0)
        {
            using var key = root.OpenSubKey(path);
            if (key is not null)
            {
                var native = KeyName(key);
                var suffix = location.SubKey[path.Length..];
                return ExpandUserClasses(native + suffix);
            }

            // A deleted or not-yet-created child still belongs to the backing key of its closest existing parent.
            var separator = path.LastIndexOf('\\');
            path = separator < 0 ? string.Empty : path[..separator];
        }

        if (location.Hive == RegistryHive.LocalMachine)
        {
            return Join(Machine, location.SubKey);
        }

        if (location.Hive == RegistryHive.Users)
        {
            return ExpandUserClasses(Join(Users, location.SubKey));
        }

        using var identity = WindowsIdentity.GetCurrent();
        return Join(Join(Users, identity.User!.Value), location.SubKey);
    }

    private static string KeyName(RegistryKey key)
    {
        const int keyNameInformation = 3;
        const int bufferTooSmall = unchecked((int)0xC0000023);
        const int bufferOverflow = unchecked((int)0x80000005);
        byte[]? buffer = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = RegistryNative.NtQueryKey(key.Handle, keyNameInformation, buffer, buffer?.Length ?? 0, out var length);
            if (status == 0 && buffer is not null && length >= sizeof(int) && length <= buffer.Length)
            {
                var bytes = BitConverter.ToInt32(buffer);
                if (bytes > 0 && bytes % sizeof(char) == 0 && bytes <= length - sizeof(int))
                {
                    return MemoryMarshal.Cast<byte, char>(buffer.AsSpan(sizeof(int), bytes)).ToString();
                }
            }

            if (status is not (bufferTooSmall or bufferOverflow) || length < sizeof(int))
            {
                throw new IOException($"Could not identify the registry key (NTSTATUS 0x{status:X8}).");
            }

            buffer = new byte[length];
        }

        throw new IOException("The registry key changed while its backing path was being read.");
    }

    private static string ExpandUserClasses(string path)
    {
        if (!path.StartsWith(Users + @"\", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var end = path.IndexOf('\\', Users.Length + 1);
        if (end < 0)
        {
            end = path.Length;
        }

        const string suffix = "_Classes";
        return path.AsSpan(0, end).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? path[..(end - suffix.Length)] + @"\Software\Classes" + path[end..]
            : path;
    }

    internal static bool AtOrBelow(string child, string parent)
    {
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
               || child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Join(string root, string path)
    {
        return path.Length == 0 ? root : root + "\\" + path;
    }
}
