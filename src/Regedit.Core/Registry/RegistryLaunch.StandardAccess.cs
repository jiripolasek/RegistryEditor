// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Registry;

public static partial class RegistryLaunch
{
    public static unsafe void StartStandardAccess(string executable, RegistryLocation location)
    {
        var commandLine = StandardAccessCommandLine(executable, location).ToCharArray();
        var shell = GetShellWindow();
        if (shell == 0 || GetWindowThreadProcessId(shell, out var processId) == 0)
        {
            throw new InvalidOperationException("Standard access requires a running Windows desktop shell.");
        }

        using var process = OpenProcess(0x1000, 0, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (OpenProcessToken(process, TokenAccessLevels.Query | TokenAccessLevels.Duplicate, out var token) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new InvalidOperationException(
                    "The Windows desktop shell is elevated. Standard access is unavailable.");
            }

            var startup = new StartupInfo { Size = (uint)sizeof(StartupInfo) };
            // The shell's primary token is already assigned; the new process needs its own copy.
            if (DuplicateTokenEx(token, TokenAccessLevels.AllAccess, 0, 2, 1, out var primaryToken) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            using var launchToken = primaryToken;
            fixed (char* command = commandLine)
            {
                // The desktop user's profile is already loaded; loading it again requires additional token rights.
                if (CreateProcessWithTokenW(
                        launchToken, 0, executable, command, 0, 0, null, ref startup, out var created) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                using var createdProcess = new SafeProcessHandle(created.Process, true);
                using var createdThread = new SafeWaitHandle(created.Thread, true);
            }
        }
    }

    internal static string StandardAccessCommandLine(string executable, RegistryLocation location)
    {
        RegistryWorkspace.ValidateLocation(location, true);
        var command = QuoteArgument(executable) + " " + QuoteArgument(location.Address) +
                      (location.View == RegistryView.Registry32 ? " --32" : string.Empty);
        if (command.Length >= 1024)
        {
            throw new ArgumentException("This registry path is too long to open in a standard-access window.");
        }

        return command + '\0';
    }

    private static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }

        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(
        SafeProcessHandle process,
        TokenAccessLevels access,
        out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int DuplicateTokenEx(
        SafeAccessTokenHandle token,
        TokenAccessLevels access,
        nint attributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle duplicate);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string application,
        char* commandLine,
        uint flags,
        nint environment,
        string? directory,
        ref StartupInfo startup,
        out ProcessInformation process);
}
