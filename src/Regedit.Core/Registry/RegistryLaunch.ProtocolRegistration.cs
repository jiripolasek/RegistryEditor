// ------------------------------------------------------------
// Copyright (c) Jiří Polášek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Registry;

public static partial class RegistryLaunch
{
    public static bool IsProtocolRegisteredForExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var protocol = root.OpenSubKey($@"Software\Classes\{Scheme}");
            if (protocol?.GetValue("URL Protocol") is not string)
            {
                continue;
            }

            using var command = protocol.OpenSubKey(@"shell\open\command");
            if (ProtocolCommandTargetsExecutable(command?.GetValue(string.Empty) as string, executable))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ProtocolCommandTargetsExecutable(string? command, string? executable)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            return SplitArguments(command) is [var target, "%1"] && string.Equals(target, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
