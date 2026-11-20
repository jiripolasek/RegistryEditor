// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Import;

public sealed record RegistryFileDiagnostic(int Line, string Message)
{
    public override string ToString()
    {
        return $"Line {this.Line}: {this.Message}";
    }
}

public sealed record RegistryFileValue(string Name, RegistryValue? Value, int Line)
{
    public string DisplayName => this.Name.Length == 0 ? "(Default)" : this.Name;

    public string Operation => this.Value is null ? "Delete value" : "Set value";
}

public sealed record RegistryFileSection(string Path, bool IsDeletion, int Line, IReadOnlyList<RegistryFileValue> Values);

/// <summary>Ordered file instructions, independent of the live registry and the editor control.</summary>
public sealed record RegistryFileDocument(
    IReadOnlyList<RegistryFileSection> Sections,
    IReadOnlyList<RegistryFileDiagnostic> Diagnostics,
    bool IsLegacy)
{
    private static readonly string[] LocalHives =
        ["HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG"];

    public RegistryFileMetadata Metadata { get; init; } = RegistryFileMetadata.Empty;

    public IReadOnlyDictionary<string, RegistryFileKeyChange> KeyChanges { get; init; }
        = new Dictionary<string, RegistryFileKeyChange>(StringComparer.OrdinalIgnoreCase);

    public bool CanImport => this.Diagnostics.Count == 0 && this.Sections.Count > 0;

    public static RegistryFileDocument Parse(string text)
    {
        List<RegistryFileSection> sections = [];
        List<RegistryFileDiagnostic> errors = [];
        List<RegistryFileValue>? values = null;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var header = false;
        var legacy = false;
        var deleting = false;
        var metadata = RegistryFileMetadata.Empty;
        var metadataAllowed = true;
        if (text.Contains('\0'))
        {
            errors.Add(new(1, "The source contains a null character."));
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();
            if (line.StartsWith(';'))
            {
                if (metadataAllowed)
                {
                    metadata = metadata.ReadDirective(line);
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                metadataAllowed = false;
            }

            if (!header)
            {
                legacy = line.Equals("REGEDIT4", StringComparison.Ordinal);
                if (!legacy && line != "Windows Registry Editor Version 5.00")
                {
                    errors.Add(new(lineNumber, "Expected a Windows Registry Editor Version 5.00 or REGEDIT4 header."));
                }

                header = true;
                continue;
            }

            try
            {
                if (line.StartsWith('['))
                {
                    values = null;
                    var closing = line.LastIndexOf(']');
                    if (closing < 0 || !IsCommentOrEmpty(line[(closing + 1)..]))
                    {
                        throw new FormatException("Invalid key section.");
                    }

                    var path = line[1..closing];
                    deleting = path.StartsWith('-');
                    if (deleting)
                    {
                        path = path[1..];
                    }

                    var hive = path.Split('\\', 2)[0];
                    var canonicalHive = LocalHives.FirstOrDefault(name => name.Equals(hive, StringComparison.OrdinalIgnoreCase));
                    if (canonicalHive is null || path.Contains('\0') || path.EndsWith('\\'))
                    {
                        throw new FormatException("Expected a full local registry key path.");
                    }

                    if (deleting && !path.Contains('\\'))
                    {
                        throw new FormatException("A registry hive cannot be deleted.");
                    }

                    path = canonicalHive + path[hive.Length..];
                    values = [];
                    sections.Add(new(path, deleting, lineNumber, values));
                    continue;
                }

                if (values is null || deleting)
                {
                    throw new FormatException("Values require a preceding key section that is not a key deletion.");
                }

                var remaining = line;
                string name;
                if (remaining.StartsWith('@'))
                {
                    name = string.Empty;
                    remaining = remaining[1..].TrimStart();
                }
                else
                {
                    name = ReadQuoted(ref remaining);
                }

                if (!remaining.StartsWith('='))
                {
                    throw new FormatException("Expected '=' after the value name.");
                }

                remaining = remaining[1..].TrimStart();
                var isHex = remaining.StartsWith("hex", StringComparison.OrdinalIgnoreCase);
                if (isHex)
                {
                    var continued = new StringBuilder();
                    while (remaining.EndsWith('\\'))
                    {
                        continued.Append(remaining.AsSpan(0, remaining.Length - 1));
                        if (++index >= lines.Length)
                        {
                            throw new FormatException("Unfinished hexadecimal continuation.");
                        }

                        remaining = lines[index].Trim();
                    }

                    remaining = continued.Append(remaining).ToString();
                }

                RegistryValue? value;
                if (remaining.StartsWith('-') && IsCommentOrEmpty(remaining[1..]))
                {
                    values.Add(new(name, null, lineNumber));
                    continue;
                }

                if (remaining.StartsWith('"'))
                {
                    var data = ReadQuoted(ref remaining);
                    if (!IsCommentOrEmpty(remaining))
                    {
                        throw new FormatException("Unexpected content after the string.");
                    }

                    value = new(name, RegistryValueType.String, Encoding.Unicode.GetBytes(data + '\0'));
                }
                else if (remaining.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                {
                    var numberText = StripComment(remaining[6..]);
                    if (numberText.Length is < 1 or > 8
                        || !uint.TryParse(numberText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number))
                    {
                        throw new FormatException("Expected up to eight hexadecimal DWORD digits.");
                    }

                    var bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes, number);
                    value = new(name, RegistryValueType.DWord, bytes);
                }
                else if (isHex)
                {
                    var colon = remaining.IndexOf(':');
                    var typeText = colon < 0 ? string.Empty : remaining[..colon];
                    uint type = (uint)RegistryValueType.Binary;
                    if (typeText != "hex" && !typeText.Equals("hex", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!typeText.StartsWith("hex(", StringComparison.OrdinalIgnoreCase)
                            || !typeText.EndsWith(')')
                            || !uint.TryParse(typeText[4..^1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out type))
                        {
                            throw new FormatException("Invalid hexadecimal value type.");
                        }
                    }

                    var dataText = StripComment(remaining[(colon + 1)..]);
                    var parts = dataText.Length == 0 ? [] : dataText.Split(',');
                    if (parts.Length > 0 && parts[^1].Trim().Length == 0)
                    {
                        parts = parts[..^1];
                    }

                    var bytes = new byte[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                    {
                        var part = parts[i].Trim();
                        if (part.Length is < 1 or > 2
                            || !byte.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                        {
                            throw new FormatException("Expected comma-separated hexadecimal bytes.");
                        }
                    }

                    if (legacy && type is 1 or 2 or 7)
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        bytes = Encoding.Unicode.GetBytes(Encoding.GetEncoding(0).GetString(bytes));
                    }

                    value = new(name, (RegistryValueType)type, bytes);
                }
                else
                {
                    throw new FormatException("Expected a quoted string, dword:, hex:, hex(type):, or '-'.");
                }

                values.Add(new(name, value, lineNumber));
            }
            catch (FormatException ex)
            {
                errors.Add(new(lineNumber, ex.Message));
            }
        }

        if (!header)
        {
            errors.Add(new(1, "The registry file is empty."));
        }

        return new(sections, errors, legacy) { Metadata = metadata, KeyChanges = SummarizeKeys(sections) };
    }

    private static IReadOnlyDictionary<string, RegistryFileKeyChange> SummarizeKeys(IReadOnlyList<RegistryFileSection> sections)
    {
        Dictionary<string, int> opened = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> deleted = new(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            if (section.IsDeletion)
            {
                deleted[section.Path] = index;
            }

            // Opening a child also opens or creates its ancestors, but never its siblings.
            for (var path = section.Path; path.Length > 0; path = ParentPath(path))
            {
                opened.TryAdd(path, -1);
                if (!section.IsDeletion)
                {
                    opened[path] = index;
                }
            }
        }

        Dictionary<string, RegistryFileKeyChange> changes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, lastOpen) in opened)
        {
            var lastDelete = -1;
            for (var ancestor = path; ancestor.Length > 0; ancestor = ParentPath(ancestor))
            {
                if (deleted.TryGetValue(ancestor, out var index))
                {
                    lastDelete = Math.Max(lastDelete, index);
                }
            }

            changes[path] = lastDelete < 0 ? RegistryFileKeyChange.None :
                lastOpen > lastDelete ? RegistryFileKeyChange.Recreate : RegistryFileKeyChange.Delete;
        }

        return changes;
    }

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string ReadQuoted(ref string remaining)
    {
        if (!remaining.StartsWith('"'))
        {
            throw new FormatException("Expected a quoted value name or '@'.");
        }

        var result = new StringBuilder();
        for (var i = 1; i < remaining.Length; i++)
        {
            var character = remaining[i];
            if (character == '"')
            {
                remaining = remaining[(i + 1)..].TrimStart();
                return result.ToString();
            }

            if (character == '\\')
            {
                if (++i >= remaining.Length || remaining[i] is not ('\\' or '"'))
                {
                    throw new FormatException("Only escaped quotes and backslashes are supported in strings.");
                }

                character = remaining[i];
            }

            if (character == '\0')
            {
                throw new FormatException("Strings and names cannot contain a null character.");
            }

            result.Append(character);
        }

        throw new FormatException("Unterminated quoted string.");
    }

    private static bool IsCommentOrEmpty(string text)
    {
        return text.TrimStart() is "" || text.TrimStart().StartsWith(';');
    }

    private static string StripComment(string text)
    {
        return text.Split(';', 2)[0].Trim();
    }
}
