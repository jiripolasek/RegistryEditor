// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace JPSoftworks.Regedit.Services;

internal static class PowerShellDiscovery
{
    internal static string? FindPowerShell7(string configuredPath, bool use32Bit, out string? error, out string? version)
    {
        version = null;
        var width = use32Bit ? "32-bit" : "64-bit";
        var custom = !string.IsNullOrWhiteSpace(configuredPath);
        var programFiles = Environment.GetFolderPath(
            use32Bit && Environment.Is64BitOperatingSystem
                ? Environment.SpecialFolder.ProgramFilesX86
                : Environment.SpecialFolder.ProgramFiles);

        // shortcut: Look in the standard install directory. Use the path setting for other installs until discovery expands.
        var path = custom ? configuredPath.Trim() : Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1];
        }

        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Enter the full path to pwsh.exe in Terminal settings.";
            return null;
        }

        if (!File.Exists(path))
        {
            error = custom
                ? "The configured pwsh.exe could not be found. Update the path in Terminal settings."
                : $"PowerShell 7 ({width}) was not found. Install it or enter its path in Terminal settings.";
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var executable = new PEReader(stream);
            var magic = executable.PEHeaders.PEHeader?.Magic;
            var fileVersion = FileVersionInfo.GetVersionInfo(path);
            if (magic is null || fileVersion.ProductMajorPart != 7)
            {
                error = "The selected executable is not PowerShell 7.";
                return null;
            }

            if ((magic == PEMagic.PE32) != use32Bit)
            {
                error = $"This session needs {width} PowerShell to use the requested registry view.";
                return null;
            }

            version = fileVersion.ProductVersion?.Split(' ', 2)[0];
        }
        catch (Exception errorReadingFile) when (errorReadingFile is IOException or UnauthorizedAccessException
                                                or BadImageFormatException or Win32Exception)
        {
            error = $"Could not read the PowerShell executable. {errorReadingFile.Message}";
            return null;
        }

        error = null;
        return path;
    }

    internal static string? FindWindowsPowerShell(bool use32Bit)
    {
        var systemDirectory = use32Bit && Environment.Is64BitOperatingSystem
            ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
            : Environment.SystemDirectory;
        var path = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }
}
