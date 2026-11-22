// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Registry;

internal static partial class RegistryNative
{
    internal const int ErrorKeyDeleted = 1018;

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryKey(
        SafeRegistryHandle key,
        int informationClass,
        [Out] byte[]? information,
        int length,
        out int required);

    [LibraryImport("advapi32.dll", EntryPoint = "RegNotifyChangeKeyValue")]
    internal static partial int NotifyChange(SafeRegistryHandle key, int subtree, uint filter, SafeWaitHandle signal, int asynchronous);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int OpenKey(SafeRegistryHandle key, string name, uint options, uint access, out SafeRegistryHandle result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    internal static partial int QueryInfoKey(
        SafeRegistryHandle key,
        nint keyClass,
        nint classLength,
        nint reserved,
        out uint subKeys,
        nint maxSubKeyLength,
        nint maxClassLength,
        out uint values,
        nint maxValueNameLength,
        nint maxValueLength,
        nint securityDescriptorSize,
        out long lastWrite);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int QueryValue(
        SafeRegistryHandle key,
        string name,
        nint reserved,
        out uint type,
        [Out] byte[]? data,
        ref uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int EnumKey(
        SafeRegistryHandle key,
        uint index,
        [Out] char[] name,
        ref uint length,
        nint reserved,
        nint keyClass,
        nint classLength,
        nint lastWrite);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int EnumValue(
        SafeRegistryHandle key,
        uint index,
        [Out] char[] name,
        ref uint length,
        nint reserved,
        nint type,
        nint data,
        nint dataLength);

[LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW")]
    internal static partial int EnumValueSize(
        SafeRegistryHandle key,
        uint index,
        [Out] char[] name,
        ref uint length,
        nint reserved,
        nint type,
        nint data,
        out uint dataLength);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetValue(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegRenameKey", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RenameKey(SafeRegistryHandle key, string oldName, string newName);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateKey(
        SafeRegistryHandle key,
        string name,
        uint reserved,
        nint keyClass,
        uint options,
        uint access,
        nint attributes,
        out SafeRegistryHandle result,
        out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCopyTreeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CopyTree(SafeRegistryHandle source, string? subKey, SafeRegistryHandle destination);
}
