// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

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
            if (this.GetDataCorrection() is { } correction)
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

    private (string Text, string Help)? GetDataCorrection()
    {
        var text = this.Data.Trim();
        try
        {
            byte[] bytes;
            var prefix = this._type == RegistryValueType.Binary ? "hex:" : $"hex({(uint)this._type:x}):";
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
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..];
                }
                else if (!this.IsRaw || !text.Contains(','))
                {
                    return null;
                }

                if (text.Contains(','))
                {
                    var parts = text.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Any(part => part.Length != 2 || !part.All(char.IsAsciiHexDigit)))
                    {
                        return null;
                    }

                    text = string.Concat(parts);
                }

                bytes = RegistryValue.ParseData(this._type, text, binary: true);
            }

            var value = new RegistryValue(this.Name, this._type, bytes);
            var correction = this.IsRaw ? FormatHex(bytes) : value.EditText(this.BaseIndex == 0);
            // Check the proposed representation with the same parser used by Save.
            _ = RegistryValue.ParseData(this._type, correction, this.BaseIndex == 0, this.IsRaw);
            return (correction,
                this.IsRaw
                    ? $"Recognized byte data ({bytes.Length:N0} bytes). Apply conversion to use hexadecimal pairs."
                    : $"Recognized numeric data. {this.NumberHelp(bytes)}. Apply conversion to use the selected base.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
