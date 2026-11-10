// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Registry;

public static partial class RegistryLaunch
{
    public const string Scheme = "regedit2";

    public static ProcessStartInfo CreateElevationStartInfo(string executable, RegistryLocation location)
    {
        RegistryWorkspace.ValidateLocation(location, true);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(location.Address);
        if (location.View == RegistryView.Registry32)
        {
            start.ArgumentList.Add("--32");
        }

        return start;
    }

    public static RegistryLocation? ParseArguments(IEnumerable<string> arguments)
    {
        string? address = null;
        var view = RegistryView.Registry64;
        foreach (var argument in arguments)
        {
            if (argument == "--32")
            {
                view = RegistryView.Registry32;
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unknown option: {argument}. Use a registry path or regedit2:// link, optionally followed by --32.");
            }
            else if (address is not null)
            {
                throw new ArgumentException(
                    "Specify one registry path or regedit2:// link. Put paths containing spaces in double quotes.");
            }
            else
            {
                address = argument;
            }
        }

        if (address is null)
        {
            if (view == RegistryView.Registry32)
            {
                throw new ArgumentException("Specify a registry path or regedit2:// link with --32.");
            }

            return null;
        }

        return address.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)
            ? ParseUri(address, view)
            : RegistryLocation.Parse(address, view);
    }

    public static RegistryLocation ParseUri(string uri, RegistryView view = RegistryView.Registry64)
    {
        const string prefix = Scheme + "://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Use a registry link such as regedit2://HKCU/Software.");
        }

        var path = uri[prefix.Length..];
        if (path.Contains('#'))
        {
            throw new ArgumentException("Encode # in registry links as %23.");
        }

        var query = path.IndexOf('?');
        if (query >= 0)
        {
            view = path[(query + 1)..] switch
            {
                "view=32" => RegistryView.Registry32,
                "view=64" => RegistryView.Registry64,
                _ => throw new ArgumentException(
                    "The only supported registry link options are ?view=32 and ?view=64. Encode ? in key names as %3F.")
            };
            path = path[..query];
        }

        // Decode each key after splitting so escaped slashes remain part of its name.
        var parts = path.Replace('\\', '/').TrimEnd('/').Split('/');
        var root = DecodeSegment(parts[0]);
        if (!root.Equals("Computer", StringComparison.OrdinalIgnoreCase) && !RegistryLocation.Roots.Any(hive =>
                hive.Name.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                hive.Alias.Equals(root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Registry links must start with a local registry hive or Computer.");
        }

        var location = RegistryLocation.Parse(root, view);
        foreach (var part in parts.Skip(1))
        {
            if (location.IsComputer)
            {
                throw new ArgumentException("Use a registry link such as regedit2://HKCU/Software.");
            }

            location = location.Child(DecodeSegment(part));
        }

        return location;
    }

    private static string DecodeSegment(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '%')
            {
                continue;
            }

            if (index + 2 >= segment.Length || !Uri.IsHexDigit(segment[index + 1]) ||
                !Uri.IsHexDigit(segment[index + 2]))
            {
                throw new ArgumentException(
                    "The registry link contains an invalid percent escape. Encode a literal % as %25.");
            }

            index += 2;
        }

        return Uri.UnescapeDataString(segment);
    }
}
