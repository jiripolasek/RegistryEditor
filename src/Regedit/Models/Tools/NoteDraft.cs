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

    private KeyNote _target;
    private NoteDraft? _replacement;

    internal NoteDraft Current
    {
        get
        {
            var draft = this;
            while (draft._replacement is { } replacement)
            {
                draft = replacement;
            }

            return draft;
        }
    }

    internal KeyNote Target
    {
        get => this._target;
        set => this.SetProperty(ref this._target, value);
    }

    public RegistryLocation Location => this.Target.Location;

    public string? ValueName => this.Target.ValueName;

    public string Title => this.ValueName switch
    {
        null => this.Location.Name,
        "" => "(Default)",
        var name => name
    };

    public string Kind => this.Target.MatchRule is not null ? "Match rule" :
        this.ValueName is null ? "Key note" : "Value note";

    internal string RuleSummary => this.Target.MatchRule is { } rule
        ? this.Location.Name + " and subkeys" + rule.Describe()
        : string.Empty;

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
        : this(new KeyNote(location, text))
    {
    }

    internal NoteDraft(KeyNote note)
    {
        this._text = note.Text;
        this._savedText = note.Text;
        this._target = note with { Text = string.Empty };
    }

    public bool Matches(RegistryLocation location)
    {
        return this.Target.Matches(location);
    }

    internal void RedirectTo(NoteDraft replacement)
    {
        this._replacement = replacement;
        this.OnPropertyChanged(nameof(this.Current));
    }

    internal void MarkSaved(string text)
    {
        this._savedText = text;
        this.OnPropertyChanged(nameof(this.IsDirty));
    }
}
