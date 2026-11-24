// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Inspection;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class ValueInspectorViewModel : ObservableObject
{
    private const int HexPreviewByteLimit = 32;
    private readonly RegistryEntry _entry;

    public string TypeText => this._entry.Type;
    public string SizeText => this._entry.Value is { } value ? $"{value.Data.Length:N0} bytes" : string.Empty;

    public string Heading => this.HasStoredValue ? $"{this.TypeText} · {this.SizeText}" :
        this._entry.IsDefaultPlaceholder ? $"{this.TypeText} · Value not set" : this.TypeText;

    internal RegistryValue? InspectedValue => this._entry.Value;

    public string Name => this._entry.Name;

    public string NameLabel => this.HasNavigationIcon ? "Key name" : "Value name";

    public string CopyNameLabel => this.HasNavigationIcon ? "Copy key name" : "Copy value name";

    public bool HasText => this._entry.HasStringIcon || this._entry.HasLinkIcon;

    public bool HasStringIcon => this._entry.HasStringIcon;

    public bool HasLinkIcon => this._entry.HasLinkIcon;

    public bool HasNavigationIcon => this._entry.HasNavigationIcon;

    public bool HasKeyIcon => this._entry.IsKey;

    public bool HasParentIcon => this._entry.IsParent;

    public bool HasBinaryIcon => this._entry.HasBinaryIcon;

    public bool HasDataIcon => this._entry.HasDataIcon;

    public bool HasStoredValue => this._entry.Value is not null;

    public bool IsLink => this._entry.IsLink;

    public RegistryLocation? LinkLocation { get; }

    public string LinkTargetPath => this.LinkLocation is { } target ? RegistryLink.DisplayPath(target) : this.Data;

    private bool HasHexPreview => this.HasStoredValue && !this.HasText && !this.HasNumber;

    public bool HasDataReadout => this.HasText || this.HasHexPreview;
    public bool CanCopyData => this.HasText || (this.HasHexPreview && this.Bytes.Length > 0);
    public string DataLabel => this.IsLink ? "Stored target path" :
        !this.HasHexPreview ? "Value data (full contents)" :
        this.Bytes.Length > HexPreviewByteLimit ? $"Value data (first {HexPreviewByteLimit} bytes)" :
        "Value data (hex)";
    public string CopyDataLabel => this.HasHexPreview ? "Copy hex preview" : "Copy value data (full contents)";

    public IAsyncRelayCommand? OpenTargetCommand { get; }

    public bool CanOpenTarget => this.OpenTargetCommand is not null;

    [ObservableProperty]
    public partial string TargetStatus { get; private set; }

    public RegistryLocation? KeyLocation => this._entry.Key ?? this._entry.ParentLocation;

    public bool HasKeyPath => this.KeyLocation is not null;

    public string KeyPath => this.KeyLocation?.Address ?? string.Empty;

    public bool HasLastWriteTime => this._entry.LastWriteTimeUtc is not null;

    public string LastWriteText => this._entry.LastWriteText;

    public string LastWriteHelp => this._entry.LastWriteHelp;

    public string Glyph => this._entry.Glyph;

    public string IconFontFamily => this._entry.IconFontFamily;

    public string Data { get; }
    public string DataReadout { get; }

    public IAsyncRelayCommand? RevealFileCommand { get; }

    public bool CanRevealFile => this.RevealFileCommand is not null;

    public byte[] Bytes => this._entry.Value?.Data ?? [];

    public string RawBytes => Convert.ToHexString(this.Bytes.AsSpan(0, Math.Min(2048, this.Bytes.Length)));

    public double MaximumOffset => this.Bytes.Length;

    public double MaximumLength => Math.Min(65536, this.Bytes.Length);

    public NumericInspection? Number { get; }

    public bool HasNumber => this.Number is not null;

    [ObservableProperty]
    public partial double Offset { get; set; }

    [ObservableProperty]
    public partial double Length { get; set; }

    [ObservableProperty]
    public partial int FormatIndex { get; set; }

    [ObservableProperty]
    public partial string Result { get; private set; } = "Choose a format and byte range, then select Interpret.";

    internal ValueInspectorViewModel(RegistryValue value, string? data = null, IAsyncRelayCommand? revealFileCommand = null)
        : this(new RegistryEntry(null, value), data, revealFileCommand)
    {
    }

    internal ValueInspectorViewModel(
        RegistryEntry entry,
        string? data = null,
        IAsyncRelayCommand? revealFileCommand = null,
        IAsyncRelayCommand? openTargetCommand = null)
    {
        this._entry = entry;
        this.RevealFileCommand = revealFileCommand;
        this.Data = this.HasText ? data ?? entry.Data : string.Empty;
        this.Number = entry.Value is { } value ? RegistryValueInspector.Number(value) : null;
        var bytes = this.Bytes;
        this.DataReadout = !this.HasHexPreview ? this.Data :
            bytes.Length == 0 ? "(empty)" :
            RegistryValueEditorViewModel.FormatHex(bytes.AsSpan(0, Math.Min(HexPreviewByteLimit, bytes.Length)));
        this.Length = Math.Min(16, bytes.Length);
        this.LinkLocation = entry.LinkLocation;
        this.OpenTargetCommand = openTargetCommand;
        this.TargetStatus = this.LinkLocation is null ? "Invalid target path" : "Not checked";
    }

    internal async Task LoadTargetStatusAsync(RegistryService registry)
    {
        if (this.LinkLocation is not { } target)
        {
            return;
        }

        this.TargetStatus = "Checking target…";
        try
        {
            await Task.Run(() =>
            {
                using var key = registry.Open(target);
            });
            this.TargetStatus = "Available";
        }
        catch (FileNotFoundException)
        {
            this.TargetStatus = "Missing";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException
                                      || error is Win32Exception { NativeErrorCode: 5 })
        {
            this.TargetStatus = "Inaccessible";
        }
        catch (Exception error)
        {
            this.TargetStatus = "Unavailable: " + error.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasStoredValue))]
    private void Interpret()
    {
        if (this._entry.Value is not { } value)
        {
            return;
        }

        try
        {
            if (!double.IsFinite(this.Offset)
                || !double.IsFinite(this.Length)
                || this.Offset != Math.Truncate(this.Offset)
                || this.Length != Math.Truncate(this.Length))
            {
                throw new ArgumentException("Offset and byte count must be whole numbers.");
            }

            this.Result = RegistryValueInspector.Interpret(
                value.Data, checked((int)this.Offset), checked((int)this.Length), (ByteInterpretation)this.FormatIndex).Replace("\0", "␀");
        }
        catch (Exception ex)
        {
            this.Result = ex.Message;
        }
    }
}
