// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;

namespace JPSoftworks.Controls;

internal static class HexEditorRow
{
    public const int BytesPerRow = 16;

    public static int HexStart(int digits) => digits + 3;

    public static int AsciiStart(int digits) => HexStart(digits) + (3 * BytesPerRow) + 2;

    public static char Printable(byte value) => value is >= 0x20 and <= 0x7E ? (char)value : '.';

    public static string Format(ReadOnlySpan<byte> bytes, long offset, int digits)
    {
        var hex = HexStart(digits);
        var ascii = AsciiStart(digits);
        Span<char> line = stackalloc char[AsciiStart(16) + BytesPerRow];
        line.Fill(' ');
        offset.TryFormat(line, out _, digits == 8 ? "X8" : "X16", CultureInfo.InvariantCulture);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i].TryFormat(line.Slice(hex + (i * 3), 2), out _, "X2", CultureInfo.InvariantCulture);
            line[ascii + i] = Printable(bytes[i]);
        }

        return new string(line[..(ascii + bytes.Length)]);
    }

    public static bool TryGetColumn(int character, int digits, out int column, out bool ascii)
    {
        var start = HexStart(digits);
        ascii = character >= AsciiStart(digits);
        column = ascii ? character - AsciiStart(digits) : (character - start) / 3;
        return character >= start && (ascii || character < start + (3 * BytesPerRow) - 1) && column < BytesPerRow;
    }
}
