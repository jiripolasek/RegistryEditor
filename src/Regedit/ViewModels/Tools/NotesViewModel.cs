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

internal sealed partial class NotesViewModel : ObservableObject
{
    internal event Action<string, Exception>? ActionFailed;

    internal event Func<Task<RegistryLocation?>>? TargetRequested;

    private readonly NotesStore _notes;
    private readonly Func<RegistryLocation, bool, Task> _navigate;
    private readonly Action<RegistryLocation> _openBackground;
    private readonly bool _followContext;
    private readonly Action<string> _copy;
    private readonly Func<NoteDraft, Task<bool>> _confirmDelete;
    private bool _active = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Text), nameof(HasNote), nameof(Title), nameof(Path), nameof(Scope), nameof(Status), nameof(ShowUseCurrentKey),
        nameof(ShowOpenKey), nameof(CanUndoDelete), nameof(HasSaveError))]
    [NotifyCanExecuteChangedFor(
        nameof(SaveCommand), nameof(OpenCommand), nameof(DeleteCommand), nameof(UndoDeleteCommand), nameof(CopyPathCommand),
        nameof(CopyNoteCommand))]
    public partial NoteDraft? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentKeyHelp), nameof(ShowUseCurrentKey), nameof(ShowOpenKey))]
    [NotifyCanExecuteChangedFor(nameof(UseCurrentKeyCommand))]
    public partial RegistryLocation? CurrentLocation { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

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
        this.Selected?.Location.Name ?? (this._followContext ? "Select a registry key" : "Select a note");

    public string Path => this.Selected?.Location.Address
                          ?? (this._followContext
                              ? "Select a key in the registry tab to view or write its note."
                              : "Choose a note or use New note to enter a registry key.");

    public string Scope => this.Selected is { } note
        ? (note.Location.Machine ?? "This computer")
          + (note.Location.View == RegistryView.Registry32 ? " · 32-bit registry" : " · 64-bit registry")
        : string.Empty;

    public string CurrentKeyHelp => this.CurrentLocation is { } location ? Address(location) : "Select a registry tab";

    public string Status => this.Selected is null ? string.Empty :
        this.Selected.IsSaving ? "Saving…" :
        this.HasSaveError ? "Couldn’t save" :
        this.Selected.IsDirty ? "Unsaved changes" :
        this.Selected.HasContent ? "Saved" : "No note for this key";

    public bool ShowUseCurrentKey => !this._followContext || this.IsDetached;

    public bool ShowOpenKey => !this._followContext || this.IsDetached;

    private bool IsDetached => this.Selected is { } note && (this.CurrentLocation is null || !note.Matches(this.CurrentLocation));

    public bool CanUndoDelete => this.Selected?.CanUndoDelete == true;

    public bool HasSaveError => this.Selected?.SaveError is not null;

    internal NotesViewModel(
        NotesStore notes,
        Func<RegistryLocation, bool, Task> navigate,
        Action<RegistryLocation> openBackground,
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

    internal void Activate()
    {
        this.DetachDraft();
        this._active = true;
        this.AttachDraft();
        this.OnPropertyChanged(nameof(this.Text));
        this.OnPropertyChanged(nameof(this.Status));
        this.OnPropertyChanged(nameof(this.CanUndoDelete));
        this.OnPropertyChanged(nameof(this.HasSaveError));
        this.Error = this.Selected?.SaveError;
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
        return notes.Where(note => note.HasContent || note.IsDirty || note == this.Selected).Where(note =>
            new[] { note.Location.Name, Address(note.Location), note.Text }.Any(text => text.Contains(
                query, StringComparison.OrdinalIgnoreCase))).Select(InvestigationRow.Note);
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
        if (args.PropertyName == nameof(NoteDraft.Text))
        {
            this.OnPropertyChanged(nameof(this.Text));
            this.Error = this.Selected?.SaveError;
        }

        if (args.PropertyName is nameof(NoteDraft.IsDirty) or nameof(NoteDraft.IsSaving) or nameof(NoteDraft.SaveError))
        {
            this.OnPropertyChanged(nameof(this.Status));
            this.OnPropertyChanged(nameof(this.HasSaveError));
            this.Error = this.Selected?.SaveError;
        }

        this.OnPropertyChanged(nameof(this.CanUndoDelete));
        this.RefreshCommands();
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
        return this.CurrentLocation is { IsComputer: false };
    }

    [RelayCommand(CanExecute = nameof(CanUseCurrentKey))]
    private void UseCurrentKey()
    {
        if (this.CurrentLocation is { IsComputer: false } location)
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
            await this._navigate(note.Location, true);
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            this.ActionFailed?.Invoke("Open note key", ex);
        }
    }

    [RelayCommand]
    private void OpenBackground(NoteDraft? note)
    {
        if (note is not null)
        {
            this._openBackground(note.Location);
        }
    }
}
