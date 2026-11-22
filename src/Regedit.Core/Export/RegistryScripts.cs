// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.Text;
using JPSoftworks.Regedit.Core.Inspection;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Export;

public static class RegistryScripts
{
    public static string PowerShellLocation(RegistryLocation location)
    {
        var path = location.FormatPath(RegistryPathFormat.PowerShellProvider) ??
                   throw new ArgumentException("Select a local registry key.", nameof(location));
        return "Set-Location -LiteralPath " + Quote(path);
    }

    public static string PowerShell(IEnumerable<RegistryDifference> selection)
    {
        var changes = Validate(selection);
        var script = new StringBuilder(
            "# Applies the selected registry after-state. Review deletions before running.\r\n$ErrorActionPreference = 'Stop'\r\n");
        script.AppendLine("if (-not ('RegeditScript.Native' -as [type])) { Add-Type -TypeDefinition @'");
        script.AppendLine("using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;");
        script.AppendLine(
            "namespace RegeditScript { public static class Native { [DllImport(\"advapi32.dll\", EntryPoint=\"RegSetValueExW\", CharSet=CharSet.Unicode)] public static extern int Set(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint size); } }");
        script.AppendLine("'@ }");
        foreach (var change in changes)
        {
            var location = change.Location;
            var hive = "[Microsoft.Win32.RegistryHive]::" + location.Hive;
            var view = "[Microsoft.Win32.RegistryView]::" + location.View;
            script.AppendLine(
                location.Machine is null
                    ? $"$root = [Microsoft.Win32.RegistryKey]::OpenBaseKey({hive}, {view})"
                    : $"$root = [Microsoft.Win32.RegistryKey]::OpenRemoteBaseKey({hive}, {Quote(location.Machine)}, {view})");
            script.AppendLine("try {");
            if (change.Kind == RegistryChangeKind.KeyRemoved)
            {
                script.AppendLine($"    $root.DeleteSubKeyTree({Quote(location.SubKey)}, $false)");
            }
            else
            {
                var value = change.After ?? change.Before;
                script.AppendLine(
                    change.Kind == RegistryChangeKind.ValueRemoved
                        ? $"    $key = $root.OpenSubKey({Quote(location.SubKey)}, $true)"
                        : $"    $key = $root.CreateSubKey({Quote(location.SubKey)})");
                script.AppendLine("    try {");
                if (change.Kind == RegistryChangeKind.ValueRemoved)
                {
                    script.AppendLine($"        if ($null -ne $key) {{ $key.DeleteValue({Quote(value!.Name)}, $false) }}");
                }
                else if (change.After is { } after)
                {
                    script.AppendLine($"        $bytes = [Convert]::FromBase64String('{Convert.ToBase64String(after.Data)}')");
                    script.AppendLine(
                        $"        $errorCode = [RegeditScript.Native]::Set($key.Handle, {Quote(after.Name)}, 0, [uint32]{(uint)after.Type}, $bytes, [uint32]$bytes.Length)");
                    script.AppendLine("        if ($errorCode -ne 0) { throw [System.ComponentModel.Win32Exception]::new($errorCode) }");
                }

                script.AppendLine("    } finally { if ($null -ne $key) { $key.Dispose() } }");
            }

            script.AppendLine("} finally { $root.Dispose() }");
        }

        return script.ToString();
    }

    public static string RegExe(IEnumerable<RegistryDifference> selection)
    {
        var changes = Validate(selection);
        var script = new StringBuilder(
            "@echo off\r\nsetlocal DisableDelayedExpansion\r\nrem Save as a UTF-8 .cmd file. Applies selected after-state; review deletions.\r\nfor /f \"tokens=2 delims=:\" %%c in ('chcp') do set \"RegeditCodePage=%%c\"\r\nchcp 65001 >nul\r\n");
        foreach (var change in changes)
        {
            var location = change.Location;
            var command = new StringBuilder("\"%SystemRoot%\\System32\\reg.exe\" ");
            command.Append(change.Kind is RegistryChangeKind.KeyRemoved or RegistryChangeKind.ValueRemoved ? "delete " : "add ");
            command.Append(BatchQuote(location.Address));
            var value = change.After ?? change.Before;
            if (value is not null)
            {
                command.Append(value.Name.Length == 0 ? " /ve" : " /v " + BatchQuote(value.Name));
                if (change.After is { } after)
                {
                    string data;
                    if (after.Type == RegistryValueType.Binary)
                    {
                        data = Convert.ToHexString(after.Data);
                    }
                    else if (after.Type == RegistryValueType.DWordBigEndian && after.Data.Length == 4)
                    {
                        // reg.exe writes this numeric payload in host byte order despite the big-endian type.
                        data = "0x" + BinaryPrimitives.ReadUInt32LittleEndian(after.Data).ToString("X8");
                    }
                    else if (after.Type is RegistryValueType.DWord or RegistryValueType.QWord
                             && RegistryValueInspector.Number(after) is { } number)
                    {
                        data = number.Hexadecimal;
                    }
                    else if (after.Type is RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.MultiString
                             && after.Data.AsSpan().SequenceEqual(RegistryValue.ParseData(after.Type, after.EditText(false))))
                    {
                        if (after.Type == RegistryValueType.MultiString)
                        {
                            var parts = Encoding.Unicode.GetString(after.Data).TrimEnd('\0').Split('\0');
                            if (parts.Any(part => part.Length == 0))
                            {
                                throw Unsupported();
                            }

                            var separator = "|;~#".FirstOrDefault(candidate => parts.All(part => !part.Contains(candidate)));
                            if (separator == default)
                            {
                                throw Unsupported();
                            }

                            command.Append(" /s " + BatchQuote(separator.ToString()));
                            data = string.Join(separator, parts);
                        }
                        else
                        {
                            data = after.EditText(false);
                        }
                    }
                    else
                    {
                        throw Unsupported();
                    }

                    command.Append(" /t " + after.TypeName + " /d " + BatchQuote(data));
                }
            }

            command.Append(location.View == RegistryView.Registry32 ? " /reg:32 /f" : " /reg:64 /f");
            if (command.Length > 8000)
            {
                throw Unsupported();
            }

            script.AppendLine(command.ToString());
            script.AppendLine("if errorlevel 1 goto RegeditFailed");
        }

        script.AppendLine(
            "chcp %RegeditCodePage% >nul\r\nendlocal\r\nexit /b 0\r\n:RegeditFailed\r\nchcp %RegeditCodePage% >nul\r\nendlocal\r\nexit /b 1");
        return script.ToString();
    }

    private static RegistryDifference[] Validate(IEnumerable<RegistryDifference> selection)
    {
        var changes = selection.ToArray();
        if (changes.Length == 0)
        {
            throw new InvalidOperationException("Select changes to copy.");
        }

        foreach (var change in changes)
        {
            RegistryWorkspace.ValidateLocation(change.Location);
            if (change.Kind == RegistryChangeKind.KeyRemoved && change.Location.Parent is null)
            {
                throw new InvalidOperationException("Root hives cannot be deleted.");
            }

            if (change.After is { } after)
            {
                RegistryService.ValidateValueName(after.Name);
            }

            if (change.Before is { } before)
            {
                RegistryService.ValidateValueName(before.Name);
            }
        }

        // A branch deletion already removes its selected descendant deletions.
        return changes.Where((change, index) => change.Kind is not (RegistryChangeKind.KeyRemoved or RegistryChangeKind.ValueRemoved)
                                                || !changes.Take(index).Any(parent =>
                                                    parent.Kind == RegistryChangeKind.KeyRemoved
                                                    && SameBranch(change.Location, parent.Location)
                                                    && !changes.Take(index).SkipWhile(item => item != parent).Skip(1).Any(item =>
                                                        item.Kind is RegistryChangeKind.KeyAdded or RegistryChangeKind.ValueAdded
                                                            or RegistryChangeKind.ValueChanged
                                                        && SameBranch(item.Location, parent.Location)))).ToArray();
    }

    private static bool SameBranch(RegistryLocation child, RegistryLocation parent)
    {
        return child.Hive == parent.Hive
               && child.View == parent.View
               && string.Equals(child.Machine, parent.Machine, StringComparison.OrdinalIgnoreCase)
               && (child.SubKey.Equals(parent.SubKey, StringComparison.OrdinalIgnoreCase)
                   || child.SubKey.StartsWith(parent.SubKey + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string text)
    {
        return "'"
               + text.Replace("'", "''").Replace("\u2018", "\u2018\u2018").Replace("\u2019", "\u2019\u2019")
                   .Replace("\u201a", "\u201a\u201a").Replace("\u201b", "\u201b\u201b")
               + "'";
    }

    private static string BatchQuote(string text)
    {
        if (text.Any(c => char.IsControl(c) || c == '"') || text.EndsWith('\\'))
        {
            throw Unsupported();
        }

        return "\"" + text.Replace("%", "%%") + "\"";
    }

    private static Exception Unsupported()
    {
        return new InvalidOperationException(
            "These bytes, names, or types cannot be represented losslessly by this reg.exe batch script. Use Copy PowerShell, which preserves raw bytes and all value types.");
    }
}
