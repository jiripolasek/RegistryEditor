// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;

namespace JPSoftworks.Regedit.Services;

internal sealed class NotesStore : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Func<CancellationToken, Task> _delay;
    private readonly ObservableCollection<NoteDraft> _drafts = [];
    private readonly Dictionary<NoteDraft, CancellationTokenSource> _pending = [];
    private bool _paused, _disposed;

    internal NotesStore(AppSettings settings, Action save, Func<CancellationToken, Task>? delay = null)
    {
        this._settings = settings;
        this._save = save;
        this._delay = delay ?? (token => Task.Delay(TimeSpan.FromSeconds(1), token));
        this.Notes = new ReadOnlyObservableCollection<NoteDraft>(this._drafts);
        foreach (var note in settings.Notes)
        {
            if (!this._drafts.Any(draft => draft.Matches(note.Location)))
            {
                this.Add(new NoteDraft(note.Location, note.Text));
            }
        }
    }

    internal ReadOnlyObservableCollection<NoteDraft> Notes { get; }

    internal bool IsPaused => this._paused || this._disposed;

    internal event EventHandler? Changed;

    internal event Action<NoteDraft, Exception>? SaveFailed;

    internal NoteDraft Get(RegistryLocation location)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        RegistryWorkspace.ValidateLocation(location);
        var note = this._drafts.FirstOrDefault(note => note.Matches(location));
        if (note is null)
        {
            note = new NoteDraft(location, string.Empty);
            this.Add(note);
        }

        return note;
    }

    private void Add(NoteDraft note)
    {
        this._drafts.Add(note);
        note.PropertyChanged += this.DraftChanged;
    }

    private void DraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        var note = (NoteDraft)sender!;
        if (args.PropertyName == nameof(NoteDraft.Text) && !note.IsSaving)
        {
            note.SaveError = null;
            this.Schedule(note);
        }

        if (args.PropertyName is nameof(NoteDraft.Text) or nameof(NoteDraft.IsDirty) or nameof(NoteDraft.SaveError))
        {
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Cancel(NoteDraft note)
    {
        if (this._pending.Remove(note, out var pending))
        {
            pending.Cancel();
        }
    }

    private void Schedule(NoteDraft note)
    {
        this.Cancel(note);
        if (this.IsPaused || !note.IsDirty || note.SaveError is not null)
        {
            return;
        }

        var pending = new CancellationTokenSource();
        this._pending.Add(note, pending);
        _ = this.AutosaveAsync(note, pending);
    }

    private async Task AutosaveAsync(NoteDraft note, CancellationTokenSource pending)
    {
        try
        {
            // Capture the caller's UI context so drafts and settings stay on their owning window thread.
            await this._delay(pending.Token);
            if (!pending.IsCancellationRequested && !this._disposed && !this._paused)
            {
                this._pending.Remove(note);
                this.TrySave(note);
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            note.SaveError = ex.Message;
            this.SaveFailed?.Invoke(note, ex);
        }
        finally
        {
            if (!pending.IsCancellationRequested && this._pending.TryGetValue(note, out var current) &&
                current == pending)
            {
                this._pending.Remove(note);
            }

            pending.Dispose();
        }
    }

    internal void Save(NoteDraft note)
    {
        this.Cancel(note);
        if (note.IsDirty)
        {
            this.Persist(note, note.Text, false);
        }
    }

    internal bool TrySave(NoteDraft note)
    {
        try
        {
            this.Save(note);
            return true;
        }
        catch (Exception ex)
        {
            this.SaveFailed?.Invoke(note, ex);
            return false;
        }
    }

    private void Persist(NoteDraft note, string text, bool replaceText)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        var previous = this._settings.Notes.ToList();
        var draftText = note.Text;
        note.IsSaving = true;
        note.SaveError = null;
        try
        {
            this._settings.Notes.RemoveAll(saved => saved.Matches(note.Location));
            if (!string.IsNullOrWhiteSpace(text))
            {
                this._settings.Notes.Add(new KeyNote(note.Location, text));
            }

            try
            {
                this._save();
            }
            catch (Exception ex)
            {
                this._settings.Notes = previous;
                note.SaveError = ex.Message;
                throw;
            }

            if (replaceText && note.Text == draftText)
            {
                note.Text = text;
            }

            note.MarkSaved(text);
            this._settings.NotifyNoteSaved(new KeyNote(note.Location, text));
        }
        finally
        {
            note.IsSaving = false;
            this.Schedule(note);
        }
    }

    internal void Delete(NoteDraft note)
    {
        this.Cancel(note);
        try
        {
            this.Persist(note, string.Empty, true);
        }
        catch
        {
            if (!note.IsDirty)
            {
                note.SaveError = null;
            }

            throw;
        }
    }

    internal void UndoDelete(NoteDraft note)
    {
        if (note.DeletedText is { } text)
        {
            note.Text = text;
            this.TrySave(note);
        }
    }

    internal bool Flush()
    {
        var saved = true;
        foreach (var note in this._drafts.Where(note => note.IsDirty).ToArray())
        {
            saved = this.TrySave(note) && saved;
        }

        return saved;
    }

    internal void Pause()
    {
        this._paused = true;
        foreach (var note in this._pending.Keys.ToArray())
        {
            this.Cancel(note);
        }
    }

    internal void Resume()
    {
        this._paused = false;
        foreach (var note in this._drafts)
        {
            this.Schedule(note);
        }
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this.Pause();
        foreach (var note in this._drafts)
        {
            note.PropertyChanged -= this.DraftChanged;
        }
    }
}
