// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Registry;

public static partial class RegistryLaunch
{
    public static string FormatArguments(IEnumerable<string> arguments)
    {
        return string.Join(
            ' ', arguments.Select(argument =>
            {
                ArgumentNullException.ThrowIfNull(argument);
                if (argument.Contains('\0'))
                {
                    throw new ArgumentException("Launch arguments cannot contain a null character.");
                }

                return QuoteArgument(argument);
            }));
    }

    public static string[] SplitArguments(string arguments)
    {
        if (arguments.Contains('\0'))
        {
            throw new ArgumentException("Launch arguments cannot contain a null character.");
        }

        // The first token has special executable-name parsing rules in CommandLineToArgvW.
        using var buffer = CommandLineToArgvW("regedit2.exe " + arguments, out var count);
        if (buffer.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var result = new string[count - 1];
        for (var index = 1; index < count; index++)
        {
            result[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer.DangerousGetHandle(), index * nint.Size))!;
        }

        return result;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial CommandLineBuffer CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    private sealed class CommandLineBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CommandLineBuffer()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return LocalFree(this.handle) == 0;
        }
    }
}
