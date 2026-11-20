// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.AccessControl;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Security;

public static partial class SecurityEditor
{
    public static void Show(nint owner, RegistryLocation location, RegistryJournal? journal = null)
    {
        using var auditing = new TokenPrivilege("SeSecurityPrivilege");
        using var ownership = new TokenPrivilege("SeTakeOwnershipPrivilege");
        using var information = new RegistrySecurityInformation(location, journal);
        if (EditSecurity(owner, information) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        GC.KeepAlive(information);
    }

    [LibraryImport("aclui.dll", SetLastError = true)]
    private static partial int EditSecurity(nint owner, ISecurityInformation information);
}

[GeneratedComInterface]
[Guid("965FC360-16FF-11D0-91CB-00AA00BBB723")]
public partial interface ISecurityInformation
{
    [PreserveSig]
    int GetObjectInformation(out SecurityObjectInfo info);

    [PreserveSig]
    int GetSecurity(uint requested, out nint descriptor, int useDefault);

    [PreserveSig]
    int SetSecurity(uint requested, nint descriptor);

    [PreserveSig]
    int GetAccessRights(nint objectType, uint flags, out nint access, out uint count, out uint defaultAccess);

    [PreserveSig]
    int MapGeneric(nint objectType, ref byte aceFlags, ref uint mask);

    [PreserveSig]
    int GetInheritTypes(out nint types, out uint count);

    [PreserveSig]
    int PropertySheetPageCallback(nint window, uint message, uint page);
}

[StructLayout(LayoutKind.Sequential)]
public struct SecurityObjectInfo
{
    public uint Flags;
    public nint Instance, ServerName, ObjectName, PageTitle;
    public Guid ObjectType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAccess
{
    public nint ObjectType;
    public uint Mask;
    public nint Name;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityInherit
{
    public nint ObjectType;
    public uint Flags;
    public nint Name;
}

[GeneratedComClass]
public sealed partial class RegistrySecurityInformation : ISecurityInformation, IDisposable
{
    private readonly RegistryLocation _location;
    private readonly RegistryJournal? _journal;
    private readonly List<nint> _allocations = [];
    private readonly nint _name, _server, _access, _inherit;
    private readonly uint _accessCount;

    public RegistrySecurityInformation(RegistryLocation location, RegistryJournal? journal = null)
    {
        RegistryWorkspace.ValidateLocation(location);
        this._location = location;
        this._journal = journal;
        this._name = this.Text(location.Address);
        this._server = location.Machine is null ? 0 : this.Text(location.Machine);
        (string Name, uint Mask)[] rights =
        [
            ("Full control", 0xF003F), ("Read", 0x20019), ("Query value", 1), ("Set value", 2),
            ("Create subkey", 4), ("Enumerate subkeys", 8), ("Notify", 16), ("Create link", 32),
            ("Delete", 0x10000), ("Read permissions", 0x20000), ("Change permissions", 0x40000),
            ("Take ownership", 0x80000)
        ];
        this._accessCount = (uint)rights.Length;
        this._access = this.Allocate(Marshal.SizeOf<SecurityAccess>() * rights.Length);
        for (var i = 0; i < rights.Length; i++)
        {
            Marshal.StructureToPtr(
                new SecurityAccess { Mask = rights[i].Mask, Name = this.Text(rights[i].Name), Flags = i < 2 ? 0x30000u : 0x10000u },
                this._access + i * Marshal.SizeOf<SecurityAccess>(), false);
        }

        (string Name, uint Flags)[] inheritance = [("This key only", 0), ("This key and subkeys", 2), ("Subkeys only", 10)];
        this._inherit = this.Allocate(Marshal.SizeOf<SecurityInherit>() * inheritance.Length);
        for (var i = 0; i < inheritance.Length; i++)
        {
            Marshal.StructureToPtr(
                new SecurityInherit { Flags = inheritance[i].Flags, Name = this.Text(inheritance[i].Name) },
                this._inherit + i * Marshal.SizeOf<SecurityInherit>(), false);
        }
    }

    public void Dispose()
    {
        foreach (var pointer in this._allocations)
        {
            Marshal.FreeHGlobal(pointer);
        }

        this._allocations.Clear();
    }

    public int GetObjectInformation(out SecurityObjectInfo info)
    {
        info = new SecurityObjectInfo { Flags = 0x17, ObjectName = this._name, ServerName = this._server };
        return 0;
    }

    public int GetSecurity(uint requested, out nint descriptor, int useDefault)
    {
        descriptor = 0;
        if (useDefault != 0)
        {
            return unchecked((int)0x80004001);
        }

        try
        {
            using var key = this.Open((requested & 8) != 0 ? 0x1020000u : 0x20000u);
            var result = SecurityNative.GetSecurityInfo(key.Handle, 4, requested, out _, out _, out _, out _, out descriptor);
            // ACLUI owns this LocalAlloc descriptor and releases it with LocalFree.
            return HResult(result);
        }
        catch (Exception ex)
        {
            return Marshal.GetHRForException(ex);
        }
    }

    public int SetSecurity(uint requested, nint descriptor)
    {
        if (this._journal is null)
        {
            return this.SetSecurityCore(requested, descriptor);
        }

        try
        {
            this._journal.RecordActivity(
                "Change permissions", this._location,
                $"Native Windows permission change (flags 0x{requested:X}). Previous access rules are not retained for automatic revert.",
                () => Marshal.ThrowExceptionForHR(this.SetSecurityCore(requested, descriptor)));
            return 0;
        }
        catch (Exception ex)
        {
            return Marshal.GetHRForException(ex);
        }
    }

    public int GetAccessRights(nint objectType, uint flags, out nint access, out uint count, out uint defaultAccess)
    {
        access = this._access;
        count = this._accessCount;
        defaultAccess = 1;
        return 0;
    }

    public int MapGeneric(nint objectType, ref byte aceFlags, ref uint mask)
    {
        var specific = mask & 0x0FFFFFFF;
        if ((mask & 0x80000000) != 0)
        {
            specific |= 0x20019;
        }

        if ((mask & 0x40000000) != 0)
        {
            specific |= 0x20006;
        }

        if ((mask & 0x20000000) != 0)
        {
            specific |= 0x20019;
        }

        if ((mask & 0x10000000) != 0)
        {
            specific |= 0xF003F;
        }

        mask = specific;
        return 0;
    }

    public int GetInheritTypes(out nint types, out uint count)
    {
        types = this._inherit;
        count = 3;
        return 0;
    }

    public int PropertySheetPageCallback(nint window, uint message, uint page)
    {
        return 0;
    }

    private nint Text(string value)
    {
        var pointer = Marshal.StringToHGlobalUni(value);
        this._allocations.Add(pointer);
        return pointer;
    }

    private nint Allocate(int size)
    {
        var pointer = Marshal.AllocHGlobal(size);
        this._allocations.Add(pointer);
        return pointer;
    }

    private RegistryKey Open(uint rights)
    {
        using var root = this._location.Machine is null
            ? RegistryKey.OpenBaseKey(this._location.Hive!.Value, this._location.View)
            : RemoteRegistryCredentials.OpenBaseKey(this._location.Hive!.Value, this._location.Machine, this._location.View);
        return root.OpenSubKey(this._location.SubKey, RegistryKeyPermissionCheck.Default, (RegistryRights)rights)
               ?? throw new IOException("The registry key no longer exists.");
    }

    private int SetSecurityCore(uint requested, nint descriptor)
    {
        try
        {
            nint owner = 0, group = 0, dacl = 0, sacl = 0;
            uint rights = 0;
            if ((requested & 1) != 0)
            {
                CheckBool(SecurityNative.GetSecurityDescriptorOwner(descriptor, out owner, out _));
                rights |= 0x80000;
            }

            if ((requested & 2) != 0)
            {
                CheckBool(SecurityNative.GetSecurityDescriptorGroup(descriptor, out group, out _));
                rights |= 0x80000;
            }

            if ((requested & 4) != 0)
            {
                CheckBool(SecurityNative.GetSecurityDescriptorDacl(descriptor, out _, out dacl, out _));
                rights |= 0x40000;
            }

            if ((requested & 8) != 0)
            {
                CheckBool(SecurityNative.GetSecurityDescriptorSacl(descriptor, out _, out sacl, out _));
                rights |= 0x1000000;
            }

            using var key = this.Open(rights);
            return HResult(SecurityNative.SetSecurityInfo(key.Handle, 4, requested, owner, group, dacl, sacl));
        }
        catch (Exception ex)
        {
            return Marshal.GetHRForException(ex);
        }
    }

    private static int HResult(uint error)
    {
        return error == 0 ? 0 : unchecked((int)(0x80070000 | error));
    }

    private static void CheckBool(int success)
    {
        if (success == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}

internal static partial class SecurityNative
{
    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityInfo(
        SafeRegistryHandle handle,
        uint type,
        uint information,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint descriptor);

    [LibraryImport("advapi32.dll")]
    internal static partial uint SetSecurityInfo(
        SafeRegistryHandle handle,
        uint type,
        uint information,
        nint owner,
        nint group,
        nint dacl,
        nint sacl);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetSecurityDescriptorOwner(nint descriptor, out nint owner, out int defaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetSecurityDescriptorGroup(nint descriptor, out nint group, out int defaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetSecurityDescriptorDacl(nint descriptor, out int present, out nint dacl, out int defaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetSecurityDescriptorSacl(nint descriptor, out int present, out nint sacl, out int defaulted);
}
