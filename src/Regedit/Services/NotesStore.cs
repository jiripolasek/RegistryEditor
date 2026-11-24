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
    internal event EventHandler? Changed;

    internal event Action<KeyNote>? Saved;

    internal event Action<NoteDraft, Exception>? SaveFailed;

    private readonly AppSettings _settings;
    private readonly Action<NoteScopeMove?> _save;
    private readonly Func<CancellationToken, Task> _delay;
    private readonly ObservableCollection<NoteDraft> _drafts = [];
    private readonly Dictionary<NoteDraft, CancellationTokenSource> _pending = [];
    private KeyNote[] _savedNotes;
    private bool _paused, _disposed;

    internal ReadOnlyObservableCollection<NoteDraft> Notes { get; }

    internal IReadOnlyList<KeyNote> SavedNotes => Volatile.Read(ref this._savedNotes);
    internal bool IsPaused => this._paused || this._disposed;

    internal NotesStore(AppSettings settings, Action<NoteScopeMove?> save, Func<CancellationToken, Task>? delay = null)
    {
        this._settings = settings;
        this._save = save;
        this._delay = delay ?? (token => Task.Delay(TimeSpan.FromSeconds(1), token));
        this._savedNotes = settings.Notes.ToArray();
        this.Notes = new ReadOnlyObservableCollection<NoteDraft>(this._drafts);
        foreach (var note in settings.Notes)
        {
            if (!this._drafts.Any(draft => draft.Target.HasSameTarget(note)))
            {
                this.Add(new NoteDraft(note));
            }
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

    internal NoteDraft Get(RegistryLocation location)
    {
        return this.Get(new KeyNote(location, string.Empty));
    }

    internal NoteDraft Get(KeyNote target)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        target.Validate();
        var note = this._drafts.FirstOrDefault(note => note.Target.HasSameTarget(target));
        if (note is null)
        {
            note = new NoteDraft(target with { Text = string.Empty });
            this.Add(note);
        }

        return note;
    }

    internal NoteDraft? Find(KeyNote target)
    {
        return this._drafts.FirstOrDefault(note => note.Target.HasSameTarget(target));
    }

    internal IEnumerable<NoteDraft> ForValue(RegistryLocation location, string name, RegistryValue? value)
    {
        return RankValueNotes(this._drafts.Where(note => note.HasContent && note.Target.MatchesValue(location, name, value)),
            note => note.Target);
    }

    internal SavedNoteLookup SavedAt(RegistryLocation location) => new(this.SavedNotes, location);

    private static IOrderedEnumerable<T> RankValueNotes<T>(IEnumerable<T> notes, Func<T, KeyNote> target)
    {
        return notes.OrderBy(note => target(note).MatchRule is not null)
            .ThenByDescending(note => target(note).Location.SubKey.Length);
    }

    internal sealed class SavedNoteLookup
    {
        private readonly RegistryLocation _location;
        private readonly ILookup<string, KeyNote> _keys;
        private readonly ILookup<string, KeyNote> _values;

        internal SavedNoteLookup(IReadOnlyList<KeyNote> notes, RegistryLocation location)
        {
            this._location = location;
            this._keys = notes.Where(note => note.ValueName is null && note.Location.View == location.View)
                .ToLookup(note => note.Location.Address, StringComparer.OrdinalIgnoreCase);
            this._values = RankValueNotes(notes.Where(note => note.ValueName is not null && note.AppliesAt(location)), note => note)
                .ToLookup(note => note.ValueName!, StringComparer.OrdinalIgnoreCase);
        }

        internal KeyNote? ForKey(RegistryLocation key)
        {
            return this._keys[key.Address].FirstOrDefault(note => note.Matches(key));
        }

        internal KeyNote? ForValue(RegistryValue? value)
        {
            var name = value?.Name ?? string.Empty;
            return this._values[name].FirstOrDefault(note => note.MatchesValue(this._location, name, value));
        }
    }

    internal static void ValidateScope(KeyNote original, KeyNote target)
    {
        if (target.Location is null || target.Location.IsComputer ||
            !string.Equals(target.Location.Machine, original.Location.Machine, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Enter a registry key on the same computer.");
        }

        if (target.Location.View != original.Location.View)
        {
            throw new ArgumentException("Keep the note in the same registry view.");
        }

        if (!string.Equals(target.ValueName, original.ValueName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Keep the original value name when changing scope.");
        }

        target.Validate();
    }

    internal (NoteDraft? Destination, string? Conflict) CheckScopeDestination(NoteDraft source, KeyNote target)
    {
        var destination = this.Find(target);
        if (destination == source)
        {
            return (null, null);
        }

        var conflict = destination switch
        {
            { CanUndoDelete: true } => "Undo Delete is available for a note at this scope. Choose another scope.",
            { HasContent: true } or { IsDirty: true } => target.MatchRule is null
                ? "A note already exists for this scope."
                : "A matching rule already exists for this value name and branch.",
            _ => null
        };
        return (destination, conflict);
    }

    internal NoteDraft ChangeScope(NoteDraft note, KeyNote target)
    {
        if (note.Current != note)
        {
            throw new InvalidOperationException("The note scope changed. Close this dialog and try again.");
        }

        ValidateScope(note.Target, target);
        if (!note.HasContent)
        {
            throw new InvalidOperationException("Write a note before changing where it appears.");
        }

        var (destination, conflict) = this.CheckScopeDestination(note, target);
        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }

        this.Cancel(note);
        this.Persist(note, note.Text, false, target, destination);
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

        if (args.PropertyName is nameof(NoteDraft.Text) or nameof(NoteDraft.IsDirty) or nameof(NoteDraft.SaveError) or nameof(NoteDraft.Target))
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
            if (!pending.IsCancellationRequested && this._pending.TryGetValue(note, out var current) && current == pending)
            {
                this._pending.Remove(note);
            }

            pending.Dispose();
        }
    }

    internal void Save(NoteDraft note)
    {
        this.Cancel(note);
        if (note.Current == note && note.IsDirty)
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

    private void Persist(
        NoteDraft note, string text, bool replaceText, KeyNote? newTarget = null, NoteDraft? destination = null)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        var previous = this._settings.Notes.ToList();
        var draftText = note.Text;
        var previousTarget = note.Target;
        var target = newTarget ?? previousTarget;
        note.IsSaving = true;
        if (newTarget is null)
        {
            note.SaveError = null;
        }

        try
        {
            this._settings.Notes.RemoveAll(saved => saved.HasSameTarget(previousTarget) ||
                newTarget is not null && saved.HasSameTarget(target));
            if (!string.IsNullOrWhiteSpace(text))
            {
                this._settings.Notes.Add(target with { Text = text });
            }

            try
            {
                var move = previousTarget.HasSameTarget(target) ? null :
                    new NoteScopeMove(previousTarget, previous.Find(saved => saved.HasSameTarget(previousTarget)), target);
                this._save(move);
            }
            catch (Exception ex)
            {
                this._settings.Notes = previous;
                if (newTarget is null)
                {
                    note.SaveError = ex.Message;
                }

                throw;
            }

            // Searches read the last successful save from a worker thread.
            Volatile.Write(ref this._savedNotes, this._settings.Notes.ToArray());
            if (replaceText && note.Text == draftText)
            {
                note.Text = text;
            }

            if (destination is not null)
            {
                this.Cancel(destination);
                destination.PropertyChanged -= this.DraftChanged;
                this._drafts.Remove(destination);
            }

            if (newTarget is not null)
            {
                note.Target = target;
                note.SaveError = null;
            }

            note.MarkSaved(text);
            destination?.RedirectTo(note);

            if (!previousTarget.HasSameTarget(target))
            {
                this.Saved?.Invoke(previousTarget);
            }

            this.Saved?.Invoke(target with { Text = text });
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
}
