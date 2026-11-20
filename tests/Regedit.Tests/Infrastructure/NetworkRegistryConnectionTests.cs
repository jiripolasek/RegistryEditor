// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using JPSoftworks.Regedit.Infrastructure.Interop;
using Windows.Win32.Security.Credentials;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class NetworkRegistryConnectionTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(Marshal.SizeOf<CREDUI_INFOW>() == (nint.Size == 8 ? 40 : 20), "CsWin32 credential dialog layout matches the Windows ABI");

        var attempts = 0;
        await NetworkRegistryConnection.ConnectAsync(
            42, "SERVER01", () =>
            {
                attempts++;
                return Task.CompletedTask;
            }, (_, _, _) => throw new InvalidOperationException("Unexpected authentication"));
        check(attempts == 1, "A successful connection never prompts for credentials");

        var credentialFailures = new List<Exception> { new UnauthorizedAccessException(), new SecurityException() };
        foreach (var error in new[] { 5, 1219, 1311, 1326, 1327 })
        {
            credentialFailures.Add(new Win32Exception(error));
            credentialFailures.Add(new IOException("Native registry error", error));
            credentialFailures.Add(new IOException("Registry HRESULT", unchecked((int)(0x80070000u | (uint)error))));
        }

        foreach (var denied in credentialFailures)
        {
            attempts = 0;
            var prompts = 0;
            var authenticating = false;
            await NetworkRegistryConnection.ConnectAsync(
                42, @" \\SÉRVER01\ ", async () =>
                {
                    await Task.Yield();
                    if (++attempts == 1)
                    {
                        throw denied;
                    }

                    check(authenticating, "The retry stays inside the authentication operation after an asynchronous continuation");
                }, async (owner, computer, retry) =>
                {
                    check(owner == 42 && computer == "SÉRVER01", "Authentication receives the owner and normalized computer name");
                    prompts++;
                    authenticating = true;
                    try
                    {
                        await retry();
                    }
                    finally
                    {
                        authenticating = false;
                    }
                });
            check(
                attempts == 2 && prompts == 1 && !authenticating,
                $"{denied.GetType().Name} (0x{denied.HResult:X8}) authenticates and retries once");
        }

        foreach (var failure in new Exception[]
                 {
                     new IOException("Offline"), new IOException("Network path unavailable", unchecked((int)0x80070035)),
                     new Win32Exception(53), new ArgumentException("Invalid name"),
                     new COMException("Unrelated COM failure", unchecked((int)0x8007052E))
                 })
        {
            try
            {
                await NetworkRegistryConnection.ConnectAsync(
                    42, "SERVER01", () => throw failure, (_, _, _) => throw new InvalidOperationException("Unexpected authentication"));
                check(false, "Non-permission failures propagate");
            }
            catch (Exception ex) when (ReferenceEquals(ex, failure))
            {
                check(true, "Non-permission failures keep the original error without authentication");
            }
        }

        foreach (var failure in new Exception[] { new OperationCanceledException(), new Win32Exception(1326) })
        {
            attempts = 0;
            try
            {
                await NetworkRegistryConnection.ConnectAsync(
                    42, "SERVER01", () =>
                    {
                        attempts++;
                        throw new UnauthorizedAccessException();
                    }, (_, _, _) => throw failure);
                check(false, "Unsuccessful authentication stops connecting");
            }
            catch (Exception ex) when (ReferenceEquals(ex, failure))
            {
                check(attempts == 1, "Canceled or failed authentication never retries the connection");
            }
        }

        attempts = 0;
        var retryPrompts = 0;
        var stillDenied = new UnauthorizedAccessException("Still denied");
        try
        {
            await NetworkRegistryConnection.ConnectAsync(
                42, "SERVER01", () =>
                {
                    attempts++;
                    throw stillDenied;
                }, (_, _, retry) =>
                {
                    retryPrompts++;
                    return retry();
                });
            check(false, "Failed retries propagate");
        }
        catch (UnauthorizedAccessException ex) when (ReferenceEquals(ex, stillDenied))
        {
            check(attempts == 2 && retryPrompts == 1, "A failed retry keeps its error without looping credential prompts");
        }
    }
}
