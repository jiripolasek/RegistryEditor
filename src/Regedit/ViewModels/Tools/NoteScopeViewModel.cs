// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class NoteScopeViewModel : ObservableObject
{
    private readonly NotesStore _store;
    private readonly NoteDraft _note;
    private readonly KeyNote _original;
    private readonly RegistryLocation? _context;
    private readonly RegistryValue? _value;
    private readonly Action<Exception> _reportFailure;
    private readonly RegistryValueType[] _types;
    private KeyNote? _target;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMatching))]
    public partial int ScopeIndex { get; set; }
    [ObservableProperty]
    public partial int TypeIndex { get; set; }
    [ObservableProperty]
    public partial string ByteLength { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    public bool IsMatching => this.ScopeIndex == 0;
    public bool CanSave => this._target is not null;
    public bool HasError => this.Error.Length > 0;
    public string[] TypeLabels { get; }

    internal NoteScopeViewModel(
        NotesStore store, NoteDraft note, RegistryLocation? context, RegistryValue? value, Action<Exception> reportFailure)
    {
        this._store = store;
        this._note = note;
        this._original = note.Target;
        this._context = context;
        this._value = value;
        this._reportFailure = reportFailure;

        var preferredType = this._original.MatchRule?.Type ?? (this._original.MatchRule is null ? value?.Type : null);
        var types = Enum.GetValues<RegistryValueType>();
        this._types = preferredType is { } type && !types.Contains(type) ? [.. types, type] : types;
        this.TypeLabels = ["Any type", .. this._types.Select(RegistryValue.FormatType)];
        this.Address = note.Location.Address;
        this.TypeIndex = preferredType is { } selected ? Array.IndexOf(this._types, selected) + 1 : 0;
        this.ByteLength = this._original.MatchRule?.ByteLength?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        this.Refresh();
    }

    partial void OnAddressChanged(string value) => this.Refresh();
    partial void OnScopeIndexChanged(int value) => this.Refresh();
    partial void OnTypeIndexChanged(int value) => this.Refresh();
    partial void OnByteLengthChanged(string value) => this.Refresh();

    internal NoteDraft? Save()
    {
        if (this._target is not { } target)
        {
            return null;
        }

        this.Error = string.Empty;
        try
        {
            if (this._note.Target != this._original)
            {
                throw new InvalidOperationException("The note scope changed. Close this dialog and try again.");
            }

            return this._store.ChangeScope(this._note, target);
        }
        catch (Exception error)
        {
            this.Error = error.Message;
            this._reportFailure(error);
            return null;
        }
    }

    private KeyNote ReadTarget()
    {
        if (this.ScopeIndex is not (0 or 1))
        {
            throw new ArgumentException("Choose where the note should appear.");
        }

        var location = RegistryLocation.Parse(this.Address, this._original.Location.View);
        if (!this.IsMatching)
        {
            return this._original with { Location = location, MatchRule = null };
        }

        int? byteLength = null;
        if (!string.IsNullOrWhiteSpace(this.ByteLength))
        {
            if (!int.TryParse(this.ByteLength, NumberStyles.Integer | NumberStyles.AllowThousands,
                    CultureInfo.CurrentCulture, out var bytes) || bytes < 0)
            {
                throw new ArgumentException("Enter a nonnegative byte length or leave it blank.");
            }

            byteLength = bytes;
        }

        if (this.TypeIndex < 0 || this.TypeIndex > this._types.Length)
        {
            throw new ArgumentException("Choose a registry type or any type.");
        }

        var type = this.TypeIndex > 0 ? this._types[this.TypeIndex - 1] : (RegistryValueType?)null;
        return this._original with { Location = location, MatchRule = new ValueNoteRule(type, byteLength) };
    }

    private void Refresh()
    {
        this.Error = string.Empty;
        try
        {
            var target = this.ReadTarget();
            NotesStore.ValidateScope(this._original, target);
            if (this._store.CheckScopeDestination(this._note, target).Conflict is { } conflict)
            {
                throw new ArgumentException(conflict);
            }

            var criteria = target.MatchRule is { } rule
                ? " in this branch" + rule.Describe()
                : " in this key";
            this.Summary = this._note.Title + criteria + (this._value is null || this._context is null
                ? string.Empty
                : target.MatchesValue(this._context, this._value.Name, this._value)
                    ? "\nThe selected value matches."
                    : "\nThe selected value does not match.");
            this._target = target;
        }
        catch (ArgumentException error)
        {
            this.Summary = error.Message;
            this._target = null;
        }

        this.OnPropertyChanged(nameof(this.CanSave));
    }
}
