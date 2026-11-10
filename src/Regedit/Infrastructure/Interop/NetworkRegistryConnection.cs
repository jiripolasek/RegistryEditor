// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Credentials;

namespace JPSoftworks.Regedit.Infrastructure.Interop;

internal static class NetworkRegistryConnection
{
    internal static bool NeedsCredentials(Exception exception)
    {
        if (exception is UnauthorizedAccessException or SecurityException)
        {
            return true;
        }

        var error = exception switch
        {
            Win32Exception native => native.NativeErrorCode,
            IOException io => io.HResult & 0xFFFF,
            _ => 0
        };
        return error is 5 or 1219 or 1311 or 1326 or 1327;
    }

    internal static async Task ConnectAsync(
        nint owner,
        string computer,
        Func<Task> connect,
        Func<nint, string, Func<Task>, Task>? authenticate = null)
    {
        try
        {
            await connect();
        }
        catch (Exception ex) when (NeedsCredentials(ex))
        {
            computer = computer.Trim().Trim('\\');
            await (authenticate ?? AuthenticateAsync)(owner, computer, connect);
        }
    }

    private static async Task AuthenticateAsync(nint owner, string computer, Func<Task> connect)
    {
        using var token = PromptForCredentials(owner, computer);
        if (token is null)
        {
            throw new OperationCanceledException("The connection was canceled.");
        }

        await RemoteRegistryCredentials.AuthenticateAsync(computer, token, connect);
    }

    private static unsafe SafeAccessTokenHandle? PromptForCredentials(nint owner, string computer)
    {
        void* authentication = null;
        uint authenticationSize = 0;
        try
        {
            fixed (char* message = $"Enter an account with registry access on {computer}.")
            fixed (char* caption = "Connect network registry")
            {
                var info = new CREDUI_INFOW
                {
                    cbSize = (uint)sizeof(CREDUI_INFOW),
                    hwndParent = (HWND)owner,
                    pszMessageText = message,
                    pszCaptionText = caption
                };
                uint package = 0;
                var error = PInvoke.CredUIPromptForWindowsCredentials(
                    &info, 5, &package, null, 0, &authentication, &authenticationSize, null,
                    CREDUIWIN_FLAGS.CREDUIWIN_GENERIC);
                if (error == 1223)
                {
                    return null;
                }

                if (error != 0)
                {
                    throw new Win32Exception((int)error);
                }
            }

            return UnpackCredentials(computer, (nint)authentication, authenticationSize);
        }
        finally
        {
            if (authentication is not null)
            {
                CryptographicOperations.ZeroMemory(new Span<byte>(authentication, checked((int)authenticationSize)));
                PInvoke.CoTaskMemFree(authentication);
            }
        }
    }

    private static unsafe SafeAccessTokenHandle UnpackCredentials(
        string computer,
        nint authentication,
        uint authenticationSize)
    {
        uint userSize = 0, domainSize = 0, passwordSize = 0;
        if (!PInvoke.CredUnPackAuthenticationBuffer(
                0, (void*)authentication, authenticationSize, null, &userSize, null, &domainSize, null,
                &passwordSize) && Marshal.GetLastPInvokeError() != 122)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var user = new char[Math.Max(1, checked((int)userSize))];
        var domain = new char[Math.Max(1, checked((int)domainSize))];
        var password = new char[Math.Max(1, checked((int)passwordSize))];
        try
        {
            fixed (char* userPointer = user)
            fixed (char* domainPointer = domain)
            fixed (char* passwordPointer = password)
            {
                if (!PInvoke.CredUnPackAuthenticationBuffer(
                        0, (void*)authentication, authenticationSize, userPointer, &userSize, domainPointer,
                        &domainSize, passwordPointer, &passwordSize))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return Logon(computer, new string(userPointer), new string(domainPointer), password);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(user.AsSpan()));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(domain.AsSpan()));
        }
    }

    internal static unsafe SafeAccessTokenHandle Logon(string computer, string user, string? domain, char[] password)
    {
        var separator = user.IndexOf('\\');
        if (separator >= 0)
        {
            domain = user[..separator];
            user = user[(separator + 1)..];
        }

        domain = user.Contains('@') ? null : string.IsNullOrEmpty(domain) || domain == "." ? computer : domain;
        fixed (char* userPointer = user)
        fixed (char* domainPointer = domain)
        fixed (char* passwordPointer = password)
        {
            // NEW_CREDENTIALS changes outbound authentication without changing local file and journal access.
            HANDLE token = default;
            if (!PInvoke.LogonUser(
                    userPointer, domainPointer, passwordPointer, LOGON32_LOGON.LOGON32_LOGON_NEW_CREDENTIALS,
                    LOGON32_PROVIDER.LOGON32_PROVIDER_WINNT50, &token))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new SafeAccessTokenHandle((nint)token.Value);
        }
    }
}
