// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;


namespace JPSoftworks.Regedit.ViewModels.Browser;

public sealed partial class RegistryValueEditorViewModel : RegistryEditPrompt
{
    private readonly RegistryValueType _type;
    private readonly RegistryValue? _original;
    private readonly string _initialData;
    private int _baseIndex;
    private bool _isMultiline;
    private string _data;
    private byte[] _previewBytes = [];

    internal RegistryValueEditorViewModel(RegistryValue? original, RegistryValueType type, bool binary, bool creating)
    {
        this._type = type;
        this.IsCreating = creating;
        this.Name = original?.Name ?? string.Empty;
        var value = original ?? new RegistryValue(
            string.Empty, type,
            RegistryValue.ParseData(
                type,
                type is RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.QWord
                    ? "0"
                    : string.Empty));
        this.Value = value;
        this.Title = creating ? "New value" : "Edit value";
        this.TypeName = value.TypeName;
        var supportsTypedEditing = type is RegistryValueType.String or RegistryValueType.ExpandString
            or RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.MultiString
            or RegistryValueType.QWord;
        this.IsRaw = binary || !supportsTypedEditing || !CanRoundTrip(value);
        this.RawFallbackMessage = this.IsRaw && !binary && supportsTypedEditing
            ? (type is RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.QWord
                  ? $"{this.TypeName} requires {(type == RegistryValueType.QWord ? 8 : 4)} bytes, but this value contains {value.Data.Length:N0}. "
                  : $"This value cannot be edited as {this.TypeName} without changing its data. ") +
              "The binary editor is used to preserve the stored bytes."
            : string.Empty;
        this.IsNumeric = !this.IsRaw &&
                         type is RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.QWord;
        this._original = original;
        this._data = this.IsRaw ? FormatHex(value.Data) : value.EditText(true);
        this._initialData = this._data;
        this._isMultiline = !this.IsNumeric && (!this.IsString || HasLineBreaks(this.Data));
        this.UpdateDataHelp(this.IsRaw ? value.Data : null);
    }

    internal RegistryValue Value { get; private set; }

    public bool IsNameReadOnly => !this.IsCreating;

    public bool IsString => !this.IsRaw && this._type is RegistryValueType.String or RegistryValueType.ExpandString;

    // Switching a TextBox back to single-line mode truncates existing text at the first break.
    public bool CanToggleMultiline => this.IsString && (!this.IsMultiline || !HasLineBreaks(this.Data));

    public bool IsMultiline
    {
        get => this._isMultiline;
        set
        {
            if (this.CanToggleMultiline && this.SetProperty(ref this._isMultiline, value))
            {
                this.OnPropertyChanged(nameof(this.CanToggleMultiline));
                this.ValidateLineBreaks(this.Data);
            }
        }
    }

    public string DataHeader => this.IsRaw ? "Hexadecimal text" : "Value data";

    public string Name { get; set; }

    public string DisplayName => this.Name.Length == 0 ? "(Default)" : this.Name;

    public bool IsCreating { get; }

    public bool IsRaw { get; }

    public string RawFallbackMessage { get; }

    public bool HasRawFallback => this.RawFallbackMessage.Length > 0;

    public bool IsNumeric { get; }

    public bool IsMultiString => !this.IsRaw && this._type == RegistryValueType.MultiString;

    public string Title { get; }

    public string TypeName { get; }

    public string Data
    {
        get => this._data;
        set
        {
            if (this.SetProperty(ref this._data, value))
            {
                this.OnPropertyChanged(nameof(this.CanToggleMultiline));
                this.UpdateDataHelp();
            }
        }
    }

    /// <summary>
    /// The bytes shown in the hex preview while editing raw data. Keeps the last valid bytes when the text
    /// does not parse.
    /// </summary>
    public byte[] PreviewBytes
    {
        get => this._previewBytes;
        private set => this.SetProperty(ref this._previewBytes, value);
    }

    public int BaseIndex
    {
        get => this._baseIndex;
        set
        {
            if (this._baseIndex == value || value is not (0 or 1))
            {
                return;
            }

            if (this.TryGetValue() is { } parsed)
            {
                this._baseIndex = value;
                this.Data = parsed.EditText(value == 0);
            }

            this.OnPropertyChanged();
        }
    }

    internal bool Validate()
    {
        if (this.TryGetValue() is not { } value)
        {
            return false;
        }

        this.Value = value;
        return true;
    }

    internal bool ValidateLineBreaks(string text)
    {
        const string message = "Select Allow line breaks to enter multiline text.";
        if (this.IsString && !this.IsMultiline && HasLineBreaks(text))
        {
            this.Error = message;
            return false;
        }

        if (this.Error == message)
        {
            this.Error = null;
        }

        return true;
    }

    private RegistryValue? TryGetValue()
    {
        try
        {
            if (!this.ValidateLineBreaks(this.Data))
            {
                return null;
            }

            // Leaving the text untouched keeps the original bytes, including a nonstandard terminator.
            var data = this.ParseDraft();
            this.Error = null;
            return new RegistryValue(this.Name, this._type, data);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            this.Error = this._dataInputError ?? ex.Message;
            return null;
        }
    }

    private byte[] ParseDraft()
    {
        return this._original is not null && !this.IsRaw && !this.IsNumeric && this.Data == this._initialData
            ? this._original.Data
            : RegistryValue.ParseData(this._type, this.Data, this.BaseIndex == 0, this.IsRaw);
    }

    private static bool HasLineBreaks(string text)
    {
        return text.AsSpan().IndexOfAny("\r\n\f\u0085\u2028\u2029") >= 0;
    }

    /// <summary>
    /// Formats bytes as space-separated pairs, 16 per line, matching the rows of the hex preview.
    /// </summary>
    internal static string FormatHex(ReadOnlySpan<byte> data)
    {
        const int perLine = 16;
        var builder = new System.Text.StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(i % perLine == 0 ? Environment.NewLine : " ");
            }

            builder.Append(data[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Applies bytes edited directly in the hex view, keeping the text in sync.</summary>
    internal void ApplyBytes(byte[] bytes)
    {
        this._previewBytes = bytes;
        this.OnPropertyChanged(nameof(this.PreviewBytes));
        // Set the field so the text does not re-parse into a new array and reload the view.
        this._data = FormatHex(bytes);
        this.OnPropertyChanged(nameof(this.Data));
        this.UpdateDataHelp(bytes);
    }

    private static bool CanRoundTrip(RegistryValue value)
    {
        try
        {
            var parsed = RegistryValue.ParseData(value.Type, value.EditText(true));
            if (value.Type is RegistryValueType.String or RegistryValueType.ExpandString
                or RegistryValueType.MultiString)
            {
                // Strings differ only in how many terminators they carry, e.g. an empty REG_MULTI_SZ stored as
                // 00 00 by regedit versus 00 00 00 00 written by this editor.
                return RegistryValue.TrimUtf16Nulls(value.Data).SequenceEqual(RegistryValue.TrimUtf16Nulls(parsed));
            }

            return value.Data.AsSpan().SequenceEqual(parsed);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
