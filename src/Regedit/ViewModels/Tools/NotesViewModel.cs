// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal enum NoteSaveState
{
    None,
    Saving,
    Failed,
    Unsaved,
    Saved
}

internal sealed partial class NotesViewModel : ObservableObject
{
    internal event Action<string, Exception>? ActionFailed;

    internal event Func<Task<RegistryLocation?>>? TargetRequested;

    private readonly NotesStore _notes;
    private readonly Func<RegistryLocation, string?, bool, Task> _navigate;
    private readonly Action<RegistryLocation, string?> _openBackground;
    private readonly bool _followContext;
    private readonly Action<string> _copy;
    private readonly Func<NoteDraft, Task<bool>> _confirmDelete;
    private bool _active = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Text), nameof(HasNote), nameof(Title), nameof(Path), nameof(Scope), nameof(Status),
        nameof(ShowMetadata), nameof(NoteType), nameof(NoteTypeGlyph), nameof(RegistryContext), nameof(MatchScope), nameof(SaveState),
        nameof(ShowContextNavigation), nameof(CanUndoDelete), nameof(HasSaveError),
        nameof(OpenLabel), nameof(CanChangeScope), nameof(ScopeLabel))]
    [NotifyCanExecuteChangedFor(
        nameof(SaveCommand), nameof(OpenCommand), nameof(DeleteCommand), nameof(UndoDeleteCommand), nameof(CopyPathCommand),
        nameof(CopyNoteCommand))]
    public partial NoteDraft? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentKeyHelp), nameof(ShowContextNavigation))]
    [NotifyCanExecuteChangedFor(nameof(UseCurrentKeyCommand))]
    public partial RegistryLocation? CurrentLocation { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public string OpenLabel => this.Selected?.Target.MatchRule is not null ? "Open branch" :
        this.Selected?.ValueName is null ? "Open key" : "Open value";

    public bool CanChangeScope => this.Selected is { ValueName: not null, HasContent: true };

    public string ScopeLabel => this.Selected?.Target.MatchRule is not null
        ? "Edit matching rule…"
        : "Show on matching values…";

    public string Text
    {
        get => this.Selected?.Text ?? string.Empty;
        set
        {
            if (this.Selected is { } note && note.Text != value)
            {
                note.Text = value;
            }
        }
    }

    public bool HasNote => this.Selected is not null;

    public string Title =>
        this.Selected?.Title ?? (this._followContext ? "Select a registry key" : "Select a note");

    public string Path => this.Selected?.Location.Address
                          ?? (this._followContext
                              ? "Select a key in the registry tab to view or write its note."
                              : "Choose a note or use New note to enter a registry key.");

    public bool ShowMetadata => !this._followContext && this.HasNote;

    public string NoteType => this.Selected?.Kind ?? string.Empty;

    public string NoteTypeGlyph => this.Selected?.Target.MatchRule is not null ? "\uE71C" :
        this.Selected?.ValueName is null ? "\uE8B7" : "\uE8A5";

    public string RegistryContext => this.Selected is { } note
        ? (note.Location.Machine ?? "This computer") + (note.Location.View == RegistryView.Registry32
            ? " · 32-bit registry"
            : " · 64-bit registry")
        : string.Empty;

    public string MatchScope => this.Selected?.RuleSummary ?? string.Empty;

    public string Scope => this.MatchScope is { Length: > 0 } match
        ? this.RegistryContext + "\n" + match
        : this.RegistryContext;

    public string CurrentKeyHelp => this.CurrentLocation is { } location ? Address(location) : "Select a registry tab";

    public NoteSaveState SaveState => this.Selected switch
    {
        { IsSaving: true } => NoteSaveState.Saving,
        { SaveError: not null } => NoteSaveState.Failed,
        { IsDirty: true } => NoteSaveState.Unsaved,
        { HasContent: true } => NoteSaveState.Saved,
        _ => NoteSaveState.None
    };

    public string Status => this.SaveState switch
    {
        NoteSaveState.Saving => "Saving…",
        NoteSaveState.Failed => "Couldn’t save",
        NoteSaveState.Unsaved => "Unsaved changes",
        NoteSaveState.Saved => "Saved",
        _ => this.Selected is null ? string.Empty :
            this.Selected.ValueName is null ? "No note for this key" : "No note for this value"
    };

    public bool ShowContextNavigation => this._followContext && this.IsDetached;

    private bool IsDetached => this.Selected is { } note &&
                               (note.ValueName is not null || this.CurrentLocation is null || !note.Matches(this.CurrentLocation));

    public bool CanUndoDelete => this.Selected?.CanUndoDelete == true;

    public bool HasSaveError => this.Selected?.SaveError is not null;

    internal NotesViewModel(
        NotesStore notes,
        Func<RegistryLocation, string?, bool, Task> navigate,
        Action<RegistryLocation, string?> openBackground,
        bool followContext,
        Action<string> copy,
        Func<NoteDraft, Task<bool>> confirmDelete)
    {
        this._notes = notes;
        this._navigate = navigate;
        this._openBackground = openBackground;
        this._followContext = followContext;
        this._copy = copy;
        this._confirmDelete = confirmDelete;
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        try
        {
            if (this.TargetRequested is { } request && await request() is { } location)
            {
                this.Selected = this._notes.Get(location);
            }
        }
        catch (Exception error)
        {
            this.Error = error.Message;
            this.ActionFailed?.Invoke("Create note", error);
        }
    }

    partial void OnSelectedChanging(NoteDraft? value)
    {
        if (this._active && !this._notes.IsPaused && this.Selected is { } previous)
        {
            this._notes.TrySave(previous);
        }

        this.DetachDraft();
    }

    partial void OnSelectedChanged(NoteDraft? value)
    {
        this.AttachDraft();
        this.Error = value?.SaveError;
    }

    partial void OnCurrentLocationChanged(RegistryLocation? value)
    {
        if (this._followContext)
        {
            this.Selected = value is { IsComputer: false } ? this._notes.Get(value) : null;
        }
    }

    internal NoteScopeViewModel? CreateScopeEditor(RegistryLocation? context, RegistryValue? value)
    {
        if (this.Selected is not { ValueName: { } name, HasContent: true } note)
        {
            return null;
        }

        var matchingValue = context is not null && note.Target.AppliesAt(context) && value is not null &&
                            string.Equals(name, value.Name, StringComparison.OrdinalIgnoreCase) ? value : null;
        return new NoteScopeViewModel(this._notes, note, context, matchingValue,
            error => this.ActionFailed?.Invoke("Change note scope", error));
    }

    internal bool SaveScope(NoteScopeViewModel editor)
    {
        if (editor.Save() is not { } saved)
        {
            return false;
        }

        this.Selected = saved;
        return true;
    }

    internal void Activate()
    {
        this.DetachDraft();
        this._active = true;
        if (this.Selected is { } note && note.Current != note)
        {
            this.Selected = note.Current;
        }
        else
        {
            this.AttachDraft();
        }

        this.OnPropertyChanged(nameof(this.Text));
        this.OnPropertyChanged(nameof(this.CanUndoDelete));
        this.OnPropertyChanged(nameof(this.CanChangeScope));
        this.NotifyTargetChanged();
        this.NotifySaveStateChanged();
        this.RefreshCommands();
    }

    internal void Deactivate()
    {
        if (!this._notes.IsPaused && this.Selected is { } note)
        {
            this._notes.TrySave(note);
        }

        this.DetachDraft();
        this._active = false;
    }

    private static string Address(RegistryLocation location)
    {
        return ToolPresentation.Address(location);
    }

    /// <summary>Notes worth listing (written, unsaved or selected) whose row text matches the query.</summary>
    internal IEnumerable<InvestigationRow> VisibleRows(IEnumerable<NoteDraft> notes, string query)
    {
        var candidates = notes.Where(note => note.HasContent || note.IsDirty || note == this.Selected);
        query = query.Trim();
        if (query.Length == 0)
        {
            return candidates.Select(note => InvestigationRow.Note(note));
        }

        var matcher = new FuzzyStringMatcher(query, foldSlashes: false, foldDiacritics: false);
        var pathQuery = query.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        FuzzyStringMatcher[] pathMatchers = pathQuery is [var segment] && segment == query
            ? [matcher]
            : pathQuery.Select(part => new FuzzyStringMatcher(part, foldSlashes: false, foldDiacritics: false)).ToArray();
        return candidates.Select(note =>
            {
                var title = matcher.Match(note.Title);
                var path = MatchPath(Address(note.Location), pathMatchers);
                var bodyStart = note.Text.ReplaceLineEndings(" ").IndexOf(query, StringComparison.OrdinalIgnoreCase);
                return (Note: note, Title: title, Path: path, BodyStart: bodyStart);
            })
            .Where(match => match.Title.Score > 0 || match.Path.Score > 0 || match.BodyStart >= 0)
            .OrderByDescending(match => match.Title.Score)
            .ThenByDescending(match => match.BodyStart >= 0)
            .ThenByDescending(match => match.Path.Score)
            .ThenBy(match => match.BodyStart)
            .Select(match => InvestigationRow.Note(
                match.Note, match.Title.Positions, match.Path.Positions,
                match.BodyStart < 0 ? null : Enumerable.Range(match.BodyStart, query.Length).ToArray()));
    }

    private static (int Score, int[] Positions) MatchPath(string address, FuzzyStringMatcher[] query)
    {
        var segments = address.Split('\\');
        if (query.Length == 0)
        {
            return (0, []);
        }

        var bestScore = 0;
        int[] bestPositions = [];
        List<int>? positions = null;
        var offset = 0;
        for (var start = 0; start <= segments.Length - query.Length; start++)
        {
            var score = 0;
            var segmentOffset = offset;
            positions?.Clear();
            for (var part = 0; part < query.Length; part++)
            {
                var segment = segments[start + part];
                var match = query[part].Match(segment);
                if (match.Score == 0)
                {
                    score = 0;
                    break;
                }

                score += match.Score;
                foreach (var position in match.Positions)
                {
                    (positions ??= []).Add(segmentOffset + position);
                }

                segmentOffset += segment.Length + 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPositions = positions!.ToArray();
            }

            offset += segments[start].Length + 1;
        }

        return (bestScore, bestPositions);
    }

    private void AttachDraft()
    {
        if (this._active && this.Selected is { } note)
        {
            note.PropertyChanged += this.DraftChanged;
        }
    }

    private void DetachDraft()
    {
        if (this.Selected is { } note)
        {
            note.PropertyChanged -= this.DraftChanged;
        }
    }

    private void DraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NoteDraft.Current) && this.Selected is { } note)
        {
            this.Selected = note.Current;
            return;
        }

        if (args.PropertyName == nameof(NoteDraft.Target))
        {
            this.NotifyTargetChanged();
        }

        if (args.PropertyName == nameof(NoteDraft.Text))
        {
            this.OnPropertyChanged(nameof(this.Text));
            this.OnPropertyChanged(nameof(this.CanChangeScope));
            this.Error = this.Selected?.SaveError;
        }

        if (args.PropertyName is nameof(NoteDraft.IsDirty) or nameof(NoteDraft.IsSaving) or nameof(NoteDraft.SaveError))
        {
            this.NotifySaveStateChanged();
        }

        this.OnPropertyChanged(nameof(this.CanUndoDelete));
        this.RefreshCommands();
    }

    private void NotifyTargetChanged()
    {
        this.OnPropertyChanged(nameof(this.Title));
        this.OnPropertyChanged(nameof(this.Path));
        this.OnPropertyChanged(nameof(this.Scope));
        this.OnPropertyChanged(nameof(this.NoteType));
        this.OnPropertyChanged(nameof(this.NoteTypeGlyph));
        this.OnPropertyChanged(nameof(this.RegistryContext));
        this.OnPropertyChanged(nameof(this.MatchScope));
        this.OnPropertyChanged(nameof(this.ScopeLabel));
        this.OnPropertyChanged(nameof(this.OpenLabel));
        this.OnPropertyChanged(nameof(this.ShowContextNavigation));
    }

    private void NotifySaveStateChanged()
    {
        this.OnPropertyChanged(nameof(this.Status));
        this.OnPropertyChanged(nameof(this.SaveState));
        this.OnPropertyChanged(nameof(this.HasSaveError));
        this.Error = this.Selected?.SaveError;
    }

    private void RefreshCommands()
    {
        this.SaveCommand.NotifyCanExecuteChanged();
        this.DeleteCommand.NotifyCanExecuteChanged();
        this.UndoDeleteCommand.NotifyCanExecuteChanged();
        this.CopyNoteCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave()
    {
        return this.Selected is { IsDirty: true, IsSaving: false };
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (this.Selected is not { IsDirty: true } note)
        {
            return;
        }

        this._notes.TrySave(note);
    }

    private bool CanUseCurrentKey()
    {
        return this._followContext && this.CurrentLocation is { IsComputer: false };
    }

    [RelayCommand(CanExecute = nameof(CanUseCurrentKey))]
    private void UseCurrentKey()
    {
        if (this._followContext && this.CurrentLocation is { IsComputer: false } location)
        {
            this.Selected = this._notes.Get(location);
        }
    }

    private bool CanDelete()
    {
        return this.Selected?.HasContent == true;
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (this.Selected is not { HasContent: true } note)
        {
            return;
        }

        var text = note.Text;
        this.Error = null;
        try
        {
            if (!await this._confirmDelete(note))
            {
                return;
            }

            if (note.Text != text)
            {
                throw new InvalidOperationException("The note changed while confirming. Review it before deleting.");
            }

            this._notes.Delete(note);
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            this.ActionFailed?.Invoke("Delete note", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndoDeletion))]
    private void UndoDelete()
    {
        if (this.Selected is { } note)
        {
            this._notes.UndoDelete(note);
        }
    }

    private bool CanUndoDeletion()
    {
        return this.CanUndoDelete;
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void CopyPath()
    {
        if (this.Selected is { } note)
        {
            this.Copy(note.Location.Address);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyNote))]
    private void CopyNote()
    {
        this.Copy(this.Text);
    }

    private bool CanCopyNote()
    {
        return !string.IsNullOrEmpty(this.Selected?.Text);
    }

    private void Copy(string text)
    {
        this.Error = null;
        try
        {
            this._copy(text);
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            this.ActionFailed?.Invoke("Copy note", ex);
        }
    }

    private bool CanOpen()
    {
        return this.Selected is not null;
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (this.Selected is not { } note)
        {
            return;
        }

        this.Error = null;
        try
        {
            await this._navigate(note.Location, note.Target.MatchRule is null ? note.ValueName : null, true);
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            this.ActionFailed?.Invoke("Open note target", ex);
        }
    }

    [RelayCommand]
    private void OpenBackground(NoteDraft? note)
    {
        if (note is not null)
        {
            this._openBackground(note.Location, note.Target.MatchRule is null ? note.ValueName : null);
        }
    }
}
