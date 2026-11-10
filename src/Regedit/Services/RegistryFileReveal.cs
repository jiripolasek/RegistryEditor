// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Services;

internal static class RegistryFileReveal
{
    internal static string? Resolve(RegistryLocation location, RegistryValue? value)
    {
        if (Parse(location, value) is not { } candidate)
        {
            return null;
        }

        var (path, quoted) = candidate;
        while (!File.Exists(path) && !Directory.Exists(path))
        {
            if (quoted)
            {
                return null;
            }

            var space = path.LastIndexOf(' ');
            if (space < 3)
            {
                return null;
            }

            path = path[..space];
        }

        return path;
    }

    internal static bool CanResolve(RegistryLocation location, RegistryValue? value)
    {
        return Parse(location, value) is not null;
    }

    private static (string Path, bool Quoted)? Parse(RegistryLocation location, RegistryValue? value)
    {
        if (location.Machine is not null ||
            value is not { Type: RegistryValueType.String or RegistryValueType.ExpandString } ||
            value.Data.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            var path = new UnicodeEncoding(false, false, true).GetString(value.Data).TrimEnd('\0');
            var quoted = path.StartsWith('"');
            if (quoted)
            {
                var endQuote = path.IndexOf('"', 1);
                if (endQuote < 0)
                {
                    return null;
                }

                path = path[1..endQuote];
            }

            if (value.Type == RegistryValueType.ExpandString)
            {
                path = Environment.ExpandEnvironmentVariables(path);
            }

            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
                path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('"'))
            {
                return null;
            }

            return (Path.GetFullPath(path), quoted);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string path)
    {
        var directory = Directory.Exists(path);
        if (!directory && !File.Exists(path))
        {
            throw new FileNotFoundException("The file or folder no longer exists.", path);
        }

        return new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            Arguments = directory ? "\"" + path + "\"" : "/select,\"" + path + "\"", UseShellExecute = true
        };
    }
}
