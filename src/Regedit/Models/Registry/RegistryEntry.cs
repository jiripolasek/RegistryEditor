// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.CompilerServices;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Models.Registry;

public sealed record RegistryEntry(
    RegistryLocation? Key,
    RegistryValue? Value,
    bool IsDefaultPlaceholder = false,
    RegistryLocation? ParentLocation = null) : INotifyPropertyChanged
{
    private RegistryValue? _value = Value;
    private bool _isDefaultPlaceholder = IsDefaultPlaceholder;
    private string? _namePreview;
    private string? _dataPreview;
    private string? _accessibleName;
    private string? _data;

    public RegistryValue? Value { get => this._value; init => this._value = value; }

    public bool IsDefaultPlaceholder { get => this._isDefaultPlaceholder; init => this._isDefaultPlaceholder = value; }

    public bool IsParent => this.ParentLocation is not null;

    public bool IsKey => this.Key is not null;

    public bool IsStored => this.Key is not null || this.Value is not null;

    public bool IsDefaultValue => this.IsDefaultPlaceholder || this.Value?.Name.Length == 0;

    public bool IsLink => this.Value?.Type == RegistryValueType.Link;

    internal RegistryLocation? SourceLocation { get; init; }

    public RegistryLocation? LinkLocation =>
        RegistryLink.Resolve(this.Value, this.SourceLocation ?? new RegistryLocation());

    internal RegistryKeyDetails? Details { get; private set; }

    internal bool DetailsRead { get; private set; }

    internal bool LinkRead { get; private set; }

    private DateTimeOffset? ContainingKeyLastWriteUtc { get; set; }

    internal DateTimeOffset? LastWriteTimeUtc =>
        this.IsKey ? this.Details?.LastWriteTimeUtc : this.ContainingKeyLastWriteUtc;

    public string LastWriteText => this.LastWriteTimeUtc?.ToLocalTime().ToString("G")
                                   ?? (this.IsParent || this.Key is { Machine: null } && !this.DetailsRead ? string.Empty : "Unavailable");

    public string LastWriteHelp => this.IsKey || this.IsParent
        ? this.LastWriteText
        : $"{this.LastWriteText} (containing key, shared by all its values)";

    public string ChildrenText => this.Details is { } info
        ? $"{info.SubKeyCount:N0} {(info.SubKeyCount == 1 ? "key" : "keys")} · {info.ValueCount:N0} {(info.ValueCount == 1 ? "value" : "values")}"
        : this.Unavailable;

    public string LinkTarget =>
        this.Value?.Type == RegistryValueType.Link ? this.DataPreview : this.Details?.LinkTarget ?? this.Unavailable;

    public string SizeText => this.Value?.Data.Length.ToString("N0") ?? string.Empty;

    public string Note { get; private set; } = string.Empty;

    public string NotePreview => Shorten(this.Note);

    private string Unavailable => this.DetailsRead && this.Details is null ? "Unavailable" : string.Empty;

    public string Name => this.IsParent ? ".." : this.Key?.Name ?? this.Value?.DisplayName ?? "(Default)";

    public string Type => this.IsParent ? "Parent key" :
        this.IsKey ? this.Key!.SubKey.Length == 0 ? "Hive" : "Key" : this.Value?.TypeName ?? "REG_SZ";

    public string Data => this._data ??= this.IsParent ? this.ParentLocation!.Address :
        this.IsKey ? string.Empty : this.Value?.DisplayData ?? "(value not set)";

    public string NamePreview => this._namePreview ??= Shorten(this.Name);

    public string DataPreview => this._dataPreview ??= this.IsParent ? Shorten(this.ParentLocation!.Address) :
        this.IsKey ? string.Empty :
        this.LinkLocation is { } target ? Shorten(RegistryLink.DisplayPath(target)).ReplaceLineEndings(" ") :
        this.Value?.DisplayPreview ?? "(value not set)";

    public bool HasNavigationIcon => this.IsKey || this.IsParent;

    public bool HasStringIcon =>
        !this.HasNavigationIcon
        && (this.Value?.Type is RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.MultiString
            || this.IsDefaultPlaceholder);

    public bool HasLinkIcon => !this.HasNavigationIcon && this.IsLink;

    public bool HasBinaryIcon =>
        !this.HasNavigationIcon && !this.IsDefaultValue && this.Value?.Type == RegistryValueType.Binary;

    public bool HasDataIcon =>
        !this.HasNavigationIcon && !this.HasStringIcon && !this.HasLinkIcon && !this.HasBinaryIcon;

    public string Glyph => this.IsParent ? "\uE0C3" :
        this.IsKey ? "\uE8B7" :
        this.IsLink ? "\uE71B" :
        this.IsDefaultValue ? "\uE8EC" :
        this.Value?.Type is RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.QWord ? "\uE887" :
        this.HasStringIcon ? "\uECE9" : "\uE943";

    public string IconFontFamily =>
        this.IsParent
        || !this.IsDefaultValue
        && (this.HasStringIcon
            || this.Value?.Type is RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.QWord)
            ? "/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"
            : "Segoe Fluent Icons, Segoe MDL2 Assets";

    public string AccessibleName => this._accessibleName ??= this.IsDefaultValue
        ? $"{this.NamePreview}, {this.Type}, {this.DataPreview}, unnamed default value"
        : $"{this.NamePreview}, {this.Type}, {this.DataPreview}";

    private RegistryEntry(RegistryEntry original)
    {
        // A with-copy can replace the value after this constructor, so its caches start empty.
        this.Key = original.Key;
        this.Value = original.Value;
        this.IsDefaultPlaceholder = original.IsDefaultPlaceholder;
        this.ParentLocation = original.ParentLocation;
        this.ContainingKeyLastWriteUtc = original.ContainingKeyLastWriteUtc;
        this.SourceLocation = original.SourceLocation;
        this.Note = string.Empty;
    }

    // A live row keeps its identity as its data and presentation change.
    public bool Equals(RegistryEntry? other)
    {
        return ReferenceEquals(this, other);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static RegistryEntry ForValue(RegistryValue? value, DateTimeOffset? containingKeyLastWriteUtc, RegistryLocation sourceLocation)
    {
        return new RegistryEntry(null, value, value is null)
        {
            ContainingKeyLastWriteUtc = containingKeyLastWriteUtc, SourceLocation = sourceLocation
        };
    }

    internal void SetDetails(RegistryKeyDetails? details, bool includeLink)
    {
        this.Details = details;
        this.DetailsRead = true;
        this.LinkRead = includeLink;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LastWriteText)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LastWriteHelp)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ChildrenText)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LinkTarget)));
    }

    internal void SetContainingKeyLastWriteTime(DateTimeOffset? value)
    {
        if (this.ContainingKeyLastWriteUtc != value)
        {
            this.ContainingKeyLastWriteUtc = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LastWriteText)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LastWriteHelp)));
        }
    }

    internal void SetNote(string text)
    {
        if (this.Note == text)
        {
            return;
        }

        this.Note = text;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.NotePreview)));
    }

    internal void SetValue(RegistryValue? value)
    {
        this._value = value;
        this._isDefaultPlaceholder = value is null;
        this._namePreview = null;
        this._dataPreview = null;
        this._accessibleName = null;
        this._data = null;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public override int GetHashCode()
    {
        return RuntimeHelpers.GetHashCode(this);
    }

    private static string Shorten(string text)
    {
        const int limit = 256;
        var length = text.Length > limit && char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text.Length <= limit ? text : text[..length] + "…";
    }
}
