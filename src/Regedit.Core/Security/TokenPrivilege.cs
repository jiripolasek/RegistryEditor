// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Security;

internal sealed partial class TokenPrivilege : IDisposable
{
    private WindowsIdentity? _identity;
    private TokenPrivileges _previous;
    private bool _changed;

    public TokenPrivilege(string name)
    {
        try
        {
            this._identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);
            if (LookupPrivilegeValue(null, name, out var luid) == 0)
            {
                return;
            }

            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            this._changed = AdjustTokenPrivileges(
                this._identity.AccessToken, 0, ref requested, (uint)Marshal.SizeOf<TokenPrivileges>(),
                out this._previous, out _) != 0 && Marshal.GetLastPInvokeError() == 0;
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (this._changed && this._identity is not null)
        {
            AdjustTokenPrivileges(
                this._identity.AccessToken, 0, ref this._previous, (uint)Marshal.SizeOf<TokenPrivileges>(), out _,
                out _);
        }

        this._identity?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport(
        "advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial int LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        int disableAll,
        ref TokenPrivileges state,
        uint length,
        out TokenPrivileges previous,
        out uint returnedLength);
}
