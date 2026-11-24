// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class InspectorNotesViewModel(
    NotesStore store, RegistryLocation location, bool isKey, RegistryValue? value,
    bool showContainingKeyNote, Action<NoteDraft> open) : ObservableObject
{
    private readonly KeyNote _keyTarget = new(location, string.Empty);
    private readonly KeyNote _valueTarget = new(location, string.Empty) { ValueName = value?.Name ?? string.Empty };
    private NoteDraft? _keyNote;
    private NoteDraft[] _matches = [];

    private NoteDraft? Primary => this._matches.Length > 0 ? this._matches[0] : null;
    private RegistryLocation? Origin => this.Primary is { Target.MatchRule: not null } note ? note.Location : null;

    public bool IsVisible => !isKey || this.HasKeyNote;
    public bool HasKeyNote => this._keyNote?.HasContent == true;
    public string KeyExcerpt => this._keyNote?.Text ?? string.Empty;
    public bool HasPrimary => this.Primary is not null;
    public string ValueExcerpt => this.Primary?.Text ?? string.Empty;
    public bool IsShared => this.Origin is not null;
    public string OriginLabel => this.Origin is { } source ? $"Shared from {source.Name}" : string.Empty;
    public string OriginAddress => this.Origin?.Address ?? string.Empty;
    public string EditLabel => this.IsShared ? "Edit shared note" : "Edit";
    public string EditName => this.IsShared ? "Edit shared note" : "Edit value note";
    public string EditId => this.IsShared ? "EditMatchingNote" : "EditValueNote";
    public bool CanAddValueNote => !isKey && (!this.HasPrimary || this.IsShared);
    public bool HasMore => this._matches.Length > 1;
    public bool HasValueActions => this.HasMore || this.CanAddValueNote;
    public string MoreLabel => this._matches.Length == 2 ? "1 more shared note" : $"{this._matches.Length - 1} more shared notes";
    internal IEnumerable<NoteDraft> OtherMatches => this._matches.Skip(1);

    internal void Activate()
    {
        store.Changed -= this.Refresh;
        store.Changed += this.Refresh;
        this.Refresh(null, EventArgs.Empty);
    }

    internal void Deactivate() => store.Changed -= this.Refresh;

    private void Refresh(object? sender, EventArgs args)
    {
        this._keyNote = isKey || showContainingKeyNote ? store.Find(this._keyTarget) : null;
        this._matches = isKey ? [] : store.ForValue(location, value?.Name ?? string.Empty, value).ToArray();
        this.OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void EditPrimary()
    {
        this.OpenMatch(this.Primary);
    }

    [RelayCommand]
    private void EditKey() => open(store.Get(this._keyTarget));

    [RelayCommand]
    private void AddValueNote() => open(store.Get(this._valueTarget));

    [RelayCommand]
    private void OpenMatch(NoteDraft? note)
    {
        if (note is not null)
        {
            open(note);
        }
    }
}
