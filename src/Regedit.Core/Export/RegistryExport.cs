// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.ComponentModel;
using System.Security;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Export;

public static class RegistryExport
{
    public static async Task<int> ExportAsync(
        RegistryService service,
        IEnumerable<RegistryLocation> roots,
        string file,
        CancellationToken token = default,
        bool skipInaccessible = false)
    {
        var skipped = 0;
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var writer = new StreamWriter(temporary, false, Encoding.Unicode))
            {
                await writer.WriteLineAsync("Windows Registry Editor Version 5.00\r\n");
                foreach (var root in roots)
                {
                    var stack = new Stack<RegistryLocation>();
                    stack.Push(root);
                    while (stack.TryPop(out var location))
                    {
                        token.ThrowIfCancellationRequested();
                        ValidatePath(location.Path);
                        RegistrySnapshot snapshot;
                        try
                        {
                            snapshot = service.Read(location);
                        }
                        catch (Exception ex) when (skipInaccessible && ex is UnauthorizedAccessException
                                                       or SecurityException or IOException or Win32Exception)
                        {
                            skipped++;
                            await writer.WriteLineAsync($"; Skipped inaccessible key: {location.Path}");
                            continue;
                        }

                        await writer.WriteLineAsync($"[{location.Path}]");
                        foreach (var value in snapshot.Values)
                        {
                            await WriteValueAsync(writer, value, token);
                        }

                        await writer.WriteLineAsync();
                        snapshot.PushChildren(stack, location);
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, file, true);
            return skipped;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static void ValidatePath(string path)
    {
        if (path.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new IOException(
                "A key name contains a line break that cannot be represented in a .reg file. Save a hive backup instead.");
        }
    }

    internal static string ValueName(string name)
    {
        if (name.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new IOException(
                "A value name contains a line break that cannot be represented in a .reg file. Save a hive backup instead.");
        }

        return name.Length == 0 ? "@" : $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    internal static async Task WriteValueAsync(TextWriter writer, RegistryValue value, CancellationToken token)
    {
        var name = ValueName(value.Name);
        if (value.Type == RegistryValueType.String)
        {
            var text = value.EditText(false);
            // Quoted strings must preserve the original UTF-16 bytes and fit on one line.
            if (text.IndexOfAny(['\0', '\r', '\n']) < 0 &&
                value.Data.AsSpan().SequenceEqual(RegistryValue.ParseData(value.Type, text)))
            {
                await writer.WriteLineAsync(
                    $"{name}=\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"".AsMemory(), token);
                return;
            }
        }
        else if (value.Type == RegistryValueType.DWord && value.Data.Length == 4)
        {
            await writer.WriteLineAsync(
                $"{name}=dword:{BinaryPrimitives.ReadUInt32LittleEndian(value.Data):x8}".AsMemory(), token);
            return;
        }

        const int wrapColumn = 76;
        const string hex = "0123456789abcdef";
        var type = value.Type == RegistryValueType.Binary ? "hex:" : $"hex({(uint)value.Type:x}):";
        var line = new StringBuilder($"{name}={type}");
        for (var i = 0; i < value.Data.Length; i++)
        {
            if (i != 0 && line.Length >= wrapColumn)
            {
                line.Append(",\\\r\n");
                await writer.WriteAsync(line, token);
                line.Clear().Append("  ");
            }
            else if (i != 0)
            {
                line.Append(',');
            }

            var b = value.Data[i];
            line.Append(hex[b >> 4]).Append(hex[b & 15]);
        }

        await writer.WriteLineAsync(line, token);
    }
}
