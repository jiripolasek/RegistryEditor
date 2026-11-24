// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.ViewModels.Browser;

public sealed partial class RegistryValueEditorViewModel
{
    private string _dataHelp = string.Empty;
    private string? _dataInputError;
    private string? _dataCorrection;

    public string DataHelp
    {
        get => this._dataHelp;
        private set
        {
            if (this.SetProperty(ref this._dataHelp, value))
            {
                this.OnPropertyChanged(nameof(this.HasDataHelp));
            }
        }
    }

    public bool HasDataHelp => this.DataHelp.Length > 0;

    public bool CanApplyDataCorrection => this._dataCorrection is not null;

    [RelayCommand(CanExecute = nameof(CanApplyDataCorrection))]
    private void ApplyDataCorrection()
    {
        if (this._dataCorrection is { } correction)
        {
            this.Data = correction;
        }
    }

    private void UpdateDataHelp(byte[]? editedBytes = null)
    {
        this._dataInputError = null;
        this._dataCorrection = null;
        this.DataHelp = string.Empty;
        try
        {
            if (!this.IsNumeric && !this.IsRaw)
            {
                return;
            }

            var bytes = editedBytes ?? this.ParseDraft();
            if (this.IsRaw)
            {
                this.PreviewBytes = bytes;
                this.DataHelp = $"Bytes: {bytes.Length:N0}";
            }
            else
            {
                this.DataHelp = this.NumberHelp(bytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            this._dataInputError = this.DataError(ex);
            this.DataHelp = this._dataInputError;
            if (this.GetDataCorrection(this.Data) is { } correction)
            {
                this._dataCorrection = correction.Text;
                this.DataHelp = correction.Help;
            }

            if (this.IsRaw)
            {
                this.DataHelp += " The preview shows the last valid bytes.";
            }
        }
        finally
        {
            this.OnPropertyChanged(nameof(this.CanApplyDataCorrection));
            this.ApplyDataCorrectionCommand.NotifyCanExecuteChanged();
        }
    }

    private string NumberHelp(byte[] bytes)
    {
        var value = new RegistryValue(this.Name, this._type, bytes);
        return $"Decimal: {value.EditText(false)} · Hexadecimal: 0x{value.EditText(true)}";
    }

    private string DataError(Exception error)
    {
        if (this.IsNumeric)
        {
            var maximum = this._type == RegistryValueType.QWord ? ulong.MaxValue : uint.MaxValue;
            return error is OverflowException ? $"{this.TypeName} accepts values from 0 to {maximum} (0x{maximum:X})." :
                this.BaseIndex == 0 ? $"Enter hexadecimal digits (0–9, A–F), up to {maximum:X}." :
                $"Enter a whole decimal number from 0 to {maximum}.";
        }

        return this.Data.Any(character => !char.IsWhiteSpace(character) && !char.IsAsciiHexDigit(character))
            ? "Enter hexadecimal byte pairs, such as 01 A4 FF. Spaces and line breaks are allowed."
            : "Complete the final byte: each byte needs two hexadecimal digits.";
    }

    internal static string ByteConversionHelp(int count) =>
        $"Recognized byte data ({count:N0} bytes). Apply conversion to use hexadecimal pairs.";

    internal int ReadRegistryHex(string input, Span<byte> destination)
    {
        var text = input.AsSpan().Trim();
        var prefix = this._type == RegistryValueType.Binary ? "hex:" : $"hex({(uint)this._type:x}):";
        var prefixed = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (prefixed)
        {
            text = text[prefix.Length..];
        }

        if (!text.Contains(','))
        {
            if (!prefixed)
            {
                return -1;
            }

            var digits = 0;
            var high = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                if (!char.IsAsciiHexDigit(ch))
                {
                    return -1;
                }

                var digit = char.IsAsciiDigit(ch) ? ch - '0' : char.ToUpperInvariant(ch) - 'A' + 10;
                if (digits % 2 == 0)
                {
                    high = digit;
                }
                else if (digits / 2 < destination.Length)
                {
                    destination[digits / 2] = (byte)((high << 4) | digit);
                }

                digits++;
            }

            return digits % 2 == 0 ? digits / 2 : -1;
        }

        if (!prefixed && !this.IsRaw)
        {
            return -1;
        }

        var count = 0;
        foreach (var range in text.Split(','))
        {
            var token = text[range].Trim();
            if (count > 0 && token.StartsWith('\\'))
            {
                var nextLine = token[1..].TrimStart(" \t");
                if (nextLine.IsEmpty || nextLine[0] is not ('\r' or '\n'))
                {
                    return -1;
                }

                token = nextLine.TrimStart();
            }

            if (token.Length != 2 || !char.IsAsciiHexDigit(token[0]) || !char.IsAsciiHexDigit(token[1]))
            {
                return -1;
            }

            if (count < destination.Length)
            {
                destination[count] = byte.Parse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            }

            count++;
        }

        return count;
    }

    internal (string Text, string Help)? GetDataCorrection(string input)
    {
        var text = input.Trim();
        try
        {
            byte[] bytes;
            if (this.IsNumeric && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                bytes = RegistryValue.ParseData(this._type, text, hex: true);
            }
            else if (this.IsNumeric
                     && this._type == RegistryValueType.DWord
                     && text.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            {
                bytes = RegistryValue.ParseData(this._type, text[6..], hex: true);
            }
            else
            {
                var count = this.ReadRegistryHex(text, []);
                if (count < 0 || (this.IsNumeric && count != (this._type == RegistryValueType.QWord ? 8 : 4)))
                {
                    return null;
                }

                bytes = new byte[count];
                this.ReadRegistryHex(text, bytes);
            }

            var value = new RegistryValue(this.Name, this._type, bytes);
            var correction = this.IsRaw ? FormatHex(bytes) : value.EditText(this.BaseIndex == 0);
            // Check the proposed representation with the same parser used by Save.
            _ = RegistryValue.ParseData(this._type, correction, this.BaseIndex == 0, this.IsRaw);
            return (correction,
                this.IsRaw
                    ? ByteConversionHelp(bytes.Length)
                    : $"Recognized numeric data. {this.NumberHelp(bytes)}. Apply conversion to use the selected base.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
