// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Inspection;

public static class RegistryValueInspector
{
    public static NumericInspection? Number(RegistryValue value)
    {
        ulong number;
        if (value.Type == RegistryValueType.DWord && value.Data.Length == 4)
        {
            number = BinaryPrimitives.ReadUInt32LittleEndian(value.Data);
        }
        else if (value.Type == RegistryValueType.DWordBigEndian && value.Data.Length == 4)
        {
            number = BinaryPrimitives.ReadUInt32BigEndian(value.Data);
        }
        else if (value.Type == RegistryValueType.QWord && value.Data.Length == 8)
        {
            number = BinaryPrimitives.ReadUInt64LittleEndian(value.Data);
        }
        else
        {
            return null;
        }

        return new NumericInspection(
            number.ToString(CultureInfo.InvariantCulture),
            "0x" + number.ToString(value.Data.Length == 8 ? "X16" : "X8", CultureInfo.InvariantCulture),
            Convert.ToString(unchecked((long)number), 2).PadLeft(value.Data.Length * 8, '0'));
    }

    public static string Interpret(byte[] data, int offset, int length, ByteInterpretation format)
    {
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The byte selection is outside the value.");
        }

        if (length > 65536)
        {
            throw new ArgumentException("Interpret at most 65,536 bytes at a time.");
        }

        var bytes = data.AsSpan(offset, length);
        if (format is ByteInterpretation.WindowsGuid or ByteInterpretation.Rfc4122Guid)
        {
            if (length != 16)
            {
                throw new FormatException("A GUID requires exactly 16 bytes.");
            }

            return new Guid(bytes, format == ByteInterpretation.Rfc4122Guid).ToString("D");
        }

        if (format is ByteInterpretation.UnixSecondsInt64LittleEndian or ByteInterpretation.UnixMillisecondsInt64LittleEndian
            or ByteInterpretation.FileTimeLittleEndian)
        {
            if (length != 8)
            {
                throw new FormatException("This timestamp format requires exactly 8 bytes.");
            }

            var value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            var time = format switch
            {
                ByteInterpretation.UnixSecondsInt64LittleEndian => DateTimeOffset.FromUnixTimeSeconds(value),
                ByteInterpretation.UnixMillisecondsInt64LittleEndian => DateTimeOffset.FromUnixTimeMilliseconds(value),
                _ => DateTimeOffset.FromFileTime(value)
            };
            return time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        return format switch
        {
            ByteInterpretation.Hex => Convert.ToHexString(bytes),
            ByteInterpretation.Utf8 => new UTF8Encoding(false, true).GetString(bytes),
            ByteInterpretation.Utf16LittleEndian => new UnicodeEncoding(false, false, true).GetString(bytes),
            ByteInterpretation.Utf16BigEndian => new UnicodeEncoding(true, false, true).GetString(bytes),
            ByteInterpretation.Ascii => Encoding.GetEncoding(
                "us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }
}
