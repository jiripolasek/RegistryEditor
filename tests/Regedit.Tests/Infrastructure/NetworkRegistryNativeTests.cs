// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure.Interop;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class NetworkRegistryNativeTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var computer = "CredentialTest-" + Guid.NewGuid().ToString("N");
        using var connection = RemoteRegistryCredentials.Retain(computer);
        var originalLogon = CurrentLogon();
        ulong authenticatedLogon = 0;
        using (var token = NewCredentials())
        {
            await RemoteRegistryCredentials.AuthenticateAsync(
                computer, token, async () =>
                {
                    await Task.Yield();
                    authenticatedLogon = CurrentLogon();
                    check(
                        authenticatedLogon != originalLogon,
                        "Authentication uses a separate outbound logon after an asynchronous continuation");
                    check(
                        await Task.Run(CurrentLogon) == authenticatedLogon, "Authentication flows to a worker thread");
                });
        }

        check(
            await Task.Run(() => RemoteRegistryCredentials.Run(computer.ToUpperInvariant(), CurrentLogon)) ==
            authenticatedLogon,
            "Later workers reuse the exact authenticated logon after the prompt token has been disposed");
        check(
            RemoteRegistryCredentials.Run(computer + "-other", CurrentLogon) == originalLogon,
            "Remote credentials do not affect another computer or the caller's original identity");

        using var replacement = NewCredentials("ReplacementUser");
        var denied = new UnauthorizedAccessException();
        try
        {
            await RemoteRegistryCredentials.AuthenticateAsync(
                computer, replacement, async () =>
                {
                    var replacementLogon = CurrentLogon();
                    check(
                        replacementLogon != authenticatedLogon,
                        "A reconnect probes new credentials before replacing the old token");
                    check(
                        await Task.Run(() => RemoteRegistryCredentials.Run(computer, CurrentLogon)) == replacementLogon,
                        "The old cached token cannot override a reconnect probe on another worker");
                    throw denied;
                });
            check(false, "Rejected replacement credentials propagate their error");
        }
        catch (UnauthorizedAccessException ex) when (ReferenceEquals(ex, denied))
        {
            check(
                RemoteRegistryCredentials.Run(computer, CurrentLogon) == authenticatedLogon,
                "Rejected replacement credentials preserve the previous authenticated session");
        }

        var failedComputer = computer + "-failed";
        try
        {
            await RemoteRegistryCredentials.AuthenticateAsync(failedComputer, replacement, () => throw denied);
            check(false, "Rejected initial credentials propagate their error");
        }
        catch (UnauthorizedAccessException ex) when (ReferenceEquals(ex, denied))
        {
            check(
                RemoteRegistryCredentials.Run(failedComputer, CurrentLogon) == originalLogon,
                "Failed authentication never caches the supplied token");
        }

        check(CurrentLogon() == originalLogon, "All operations restore the caller's original identity");

        using var operation
            = RemoteRegistryCredentials.Retain(new string?[] { null, computer, computer.ToUpperInvariant() });
        connection.Dispose();
        check(
            RemoteRegistryCredentials.ConnectionCount(computer) == 1,
            "An operation retains each remote computer once after the last tab closes");
        check(
            await Task.Run(() => RemoteRegistryCredentials.Run(computer, CurrentLogon)) == authenticatedLogon,
            "New workers in the ongoing operation keep the authenticated logon after its tab closes");
        check(
            RemoteRegistryCredentials.Run(
                computer, () =>
                {
                    operation.Dispose();
                    return CurrentLogon();
                }) == authenticatedLogon,
            "An operation already using a token can finish safely after the last connection closes");
        check(
            RemoteRegistryCredentials.Run(computer, CurrentLogon) == originalLogon,
            "Closing the last connection forgets its authenticated token");
        connection.Dispose();
        operation.Dispose();
        check(RemoteRegistryCredentials.ConnectionCount(computer) == 0, "Repeated connection cleanup is harmless");
        await RemoteRegistryCredentials.AuthenticateAsync(computer, replacement, () => Task.CompletedTask);
        check(
            RemoteRegistryCredentials.Run(computer, CurrentLogon) == originalLogon,
            "A successful probe without a surviving tab does not retain a sign-in");

        using var reopened = RemoteRegistryCredentials.Retain(computer);
        connection.Dispose();
        ulong replacementLogon = 0;
        await RemoteRegistryCredentials.AuthenticateAsync(
            computer, replacement, () =>
            {
                replacementLogon = CurrentLogon();
                return Task.CompletedTask;
            });
        check(
            replacementLogon != authenticatedLogon &&
            RemoteRegistryCredentials.Run(computer, CurrentLogon) == replacementLogon,
            "Reconnecting after closing the last tab uses the replacement account's token");
    }

    private static SafeAccessTokenHandle NewCredentials(string user = "TestUser")
    {
        // NEW_CREDENTIALS does not validate this synthetic account; these tests never connect to a server.
        return NetworkRegistryConnection.Logon("CredentialTest", user, "CredentialTest", ['t', 'e', 's', 't', '\0']);
    }

    private static unsafe ulong CurrentLogon()
    {
        using var identity = WindowsIdentity.GetCurrent();
        TOKEN_STATISTICS statistics = default;
        uint length = 0;
        if (!PInvoke.GetTokenInformation(
                (HANDLE)identity.AccessToken.DangerousGetHandle(), TOKEN_INFORMATION_CLASS.TokenStatistics, &statistics,
                (uint)sizeof(TOKEN_STATISTICS), &length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return statistics.AuthenticationId.LowPart | ((ulong)(uint)statistics.AuthenticationId.HighPart << 32);
    }
}
