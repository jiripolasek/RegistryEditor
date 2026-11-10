// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JPSoftworks.Regedit.Core.Registry;

public sealed record RegistryValue(string Name, RegistryValueType Type, byte[] Data)
{
    public string DisplayName => this.Name.Length == 0 ? "(Default)" : this.Name;

    public string TypeName => this.Type switch
    {
        RegistryValueType.None => "REG_NONE",
        RegistryValueType.String => "REG_SZ",
        RegistryValueType.ExpandString => "REG_EXPAND_SZ",
        RegistryValueType.Binary => "REG_BINARY",
        RegistryValueType.DWord => "REG_DWORD",
        RegistryValueType.DWordBigEndian => "REG_DWORD_BIG_ENDIAN",
        RegistryValueType.Link => "REG_LINK",
        RegistryValueType.MultiString => "REG_MULTI_SZ",
        RegistryValueType.ResourceList => "REG_RESOURCE_LIST",
        RegistryValueType.FullResourceDescriptor => "REG_FULL_RESOURCE_DESCRIPTOR",
        RegistryValueType.ResourceRequirementsList => "REG_RESOURCE_REQUIREMENTS_LIST",
        RegistryValueType.QWord => "REG_QWORD",
        _ => $"REG_TYPE_{(uint)this.Type}"
    };

    public string DisplayData => this.Type switch
    {
        RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.Link => Encoding.Unicode
            .GetString(this.Data).TrimEnd('\0'),
        RegistryValueType.MultiString => string.Join(
            " · ", Encoding.Unicode.GetString(this.Data).TrimEnd('\0').Split('\0')),
        RegistryValueType.DWord when this.Data.Length == 4 =>
            $"0x{BinaryPrimitives.ReadUInt32LittleEndian(this.Data):x8} ({BinaryPrimitives.ReadUInt32LittleEndian(this.Data)})",
        RegistryValueType.DWordBigEndian when this.Data.Length == 4 =>
            $"0x{BinaryPrimitives.ReadUInt32BigEndian(this.Data):x8} ({BinaryPrimitives.ReadUInt32BigEndian(this.Data)})",
        RegistryValueType.QWord when this.Data.Length == 8 =>
            $"0x{BinaryPrimitives.ReadUInt64LittleEndian(this.Data):x16} ({BinaryPrimitives.ReadUInt64LittleEndian(this.Data)})",
        _ => string.Join(" ", this.Data.Take(256).Select(b => b.ToString("X2"))) +
             (this.Data.Length > 256 ? " …" : string.Empty)
    };

    public string DisplayPreview
    {
        get
        {
            const int limit = 256;
            if (this.Type is not (RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.Link
                or RegistryValueType.MultiString))
            {
                return this.DisplayData;
            }

            // Match TrimEnd without decoding the full string, including malformed odd byte counts.
            var data = TrimUtf16Nulls(this.Data);

            var prefix = Encoding.Unicode.GetString(data[..Math.Min(data.Length, (limit + 1) * 2)]);
            if (this.Type == RegistryValueType.MultiString)
            {
                prefix = prefix.Replace("\0", " · ");
            }

            if (prefix.Length > limit)
            {
                var length = char.IsHighSurrogate(prefix[limit - 1]) ? limit - 1 : limit;
                prefix = prefix[..length] + "…";
            }

            return prefix.ReplaceLineEndings(" ");
        }
    }

    /// <summary>
    /// Removes trailing UTF-16 null characters. Data with an odd byte count is returned unchanged.
    /// </summary>
    public static ReadOnlySpan<byte> TrimUtf16Nulls(ReadOnlySpan<byte> data)
    {
        if (data.Length % 2 != 0)
        {
            return data;
        }

        while (data.Length >= 2 && data[^1] == 0 && data[^2] == 0)
        {
            data = data[..^2];
        }

        return data;
    }

    public bool SameData(RegistryValue? other)
    {
        return other is not null && this.Type == other.Type && this.Data.AsSpan().SequenceEqual(other.Data);
    }

    public string EditText(bool hex, bool binary = false)
    {
        if (binary || this.Type is not (RegistryValueType.String or RegistryValueType.ExpandString
                or RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.MultiString
                or RegistryValueType.QWord))
        {
            return Convert.ToHexString(this.Data);
        }

        if (this.Type is RegistryValueType.String or RegistryValueType.ExpandString)
        {
            return Encoding.Unicode.GetString(this.Data).TrimEnd('\0');
        }

        if (this.Type == RegistryValueType.MultiString)
        {
            return Encoding.Unicode.GetString(this.Data).TrimEnd('\0').Replace("\0", "\r\n");
        }

        ulong number = this.Type switch
        {
            RegistryValueType.DWord when this.Data.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(this.Data),
            RegistryValueType.DWordBigEndian when this.Data.Length == 4 => BinaryPrimitives.ReadUInt32BigEndian(
                this.Data),
            RegistryValueType.QWord when this.Data.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(this.Data),
            _ => throw new FormatException("This value has a nonstandard size. Use Modify binary data to preserve it.")
        };
        return number.ToString(hex ? "X" : "D", CultureInfo.InvariantCulture);
    }

    public static byte[] ParseData(RegistryValueType type, string text, bool hex = true, bool binary = false)
    {
        if (binary || type is not (RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.DWord
                or RegistryValueType.DWordBigEndian or RegistryValueType.MultiString or RegistryValueType.QWord))
        {
            return Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        }

        if (text.Contains('\0'))
        {
            throw new FormatException(
                "Data cannot contain embedded null characters. Use the binary editor for raw data.");
        }

        if (type is RegistryValueType.String or RegistryValueType.ExpandString)
        {
            return Encoding.Unicode.GetBytes(text + '\0');
        }

        if (type == RegistryValueType.MultiString)
        {
            if (text.Length == 0)
            {
                return [0, 0, 0, 0];
            }

            var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            if (lines.Any(line => line.Length == 0))
            {
                throw new FormatException("Multi-string data cannot contain empty lines.");
            }

            return Encoding.Unicode.GetBytes(string.Join('\0', lines) + "\0\0");
        }

        text = text.Trim();
        if (hex && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var number = ulong.Parse(
            text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture);
        var result = new byte[type == RegistryValueType.QWord ? 8 : 4];
        if (type == RegistryValueType.QWord)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(result, number);
        }
        else if (type == RegistryValueType.DWordBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)number));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result, checked((uint)number));
        }

        return result;
    }
}
