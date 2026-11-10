// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;

namespace JPSoftworks.Regedit.Models.Tools;

public sealed class NoteDraft : ObservableObject
{
    private string _text;
    private string _savedText;
    private string? _deletedText;
    private string? _saveError;
    private bool _isSaving;

    public RegistryLocation Location { get; }

    public string Text
    {
        get => this._text;
        set
        {
            if (this._text == value)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(this._text))
            {
                this._deletedText = this._text;
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                this._deletedText = null;
            }

            if (this.SetProperty(ref this._text, value))
            {
                this.OnPropertyChanged(nameof(this.IsDirty));
                this.OnPropertyChanged(nameof(this.HasContent));
                this.OnPropertyChanged(nameof(this.CanUndoDelete));
            }
        }
    }

    public bool IsDirty => this.Text != this._savedText;

    public bool HasContent => !string.IsNullOrWhiteSpace(this.Text);

    public bool CanUndoDelete => this._deletedText is not null;

    internal string? DeletedText => this._deletedText;

    public string? SaveError
    {
        get => this._saveError;
        internal set => this.SetProperty(ref this._saveError, value);
    }

    public bool IsSaving
    {
        get => this._isSaving;
        internal set => this.SetProperty(ref this._isSaving, value);
    }

    public NoteDraft(RegistryLocation location, string text)
    {
        this._text = text;
        this._savedText = text;
        this.Location = location;
    }

    public bool Matches(RegistryLocation location)
    {
        return new KeyNote(this.Location, this.Text).Matches(location);
    }

    internal void MarkSaved(string text)
    {
        this._savedText = text;
        this.OnPropertyChanged(nameof(this.IsDirty));
    }
}
