// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class JournalViewModel
{
    internal event EventHandler? Changed;

    internal event EventHandler? EligibilityChanged;

    internal event Action<string, string, NoticeKind>? Notice;

    private readonly ILogger<JournalViewModel> _logger;
    private readonly JournalHistoryReader _reader;
    private readonly object _eligibilityGate = new();
    private readonly Dictionary<object, Guid> _selections = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, Eligibility> _eligibility = [];

    private readonly Func<IReadOnlyList<JournalEntry>, JournalEntry, IReadOnlySet<Guid>, JournalConflict?> _findConflict;

    private int _request;
    private int _completedRequest;
    private int _historyVersion;
    private Dictionary<Guid, JournalEntry> _byId = [];

    internal RegistryJournal Journal { get; }

    internal JournalEntry[] Entries { get; private set; } = [];

    internal IReadOnlySet<Guid> RevertedIds { get; private set; } = new HashSet<Guid>();

    internal IReadOnlySet<Guid> ArchivedIds { get; private set; } = new HashSet<Guid>();

    internal string? ArchiveError { get; private set; }

    internal string? Error { get; private set; }

    internal Func<JournalEntry, Task<bool>>? ConfirmRevert { get; set; }

    internal Func<Task>? RefreshBrowser { get; set; }

    private bool CanArchiveAll => this.Entries.Any(entry => !this.ArchivedIds.Contains(entry.Id));

    internal JournalViewModel(
        RegistryJournal journal,
        ILogger<JournalViewModel> logger,
        Func<IReadOnlyList<JournalEntry>, JournalEntry, IReadOnlySet<Guid>, JournalConflict?>? findConflict = null)
    {
        this.Journal = journal;
        this._reader = new JournalHistoryReader(journal);
        this._logger = logger;
        this._findConflict = findConflict ?? RegistryJournal.FindLaterConflict;
    }

    internal JournalEntry? FindEntry(Guid id)
    {
        lock (this._eligibilityGate)
        {
            return this._byId.GetValueOrDefault(id);
        }
    }

    internal async Task SetArchivedAsync(JournalEntry entry, bool archived, CancellationToken token)
    {
        if (this.FindEntry(entry.Id) is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => this.Journal.SetArchived(entry.Id, archived, token));
            await this.RefreshAsync();
            this.Notice?.Invoke(
                archived ? "Edit archived" : "Edit unarchived",
                archived
                    ? "The entry remains in history. Choose All or Archived history to find it."
                    : "The entry is visible in active history again.", NoticeKind.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this._logger.LogFailure(LogLevel.Warning, "Change journal archive state", ex);
            this.Notice?.Invoke("Couldn't update the archive", ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanArchiveAll))]
    private async Task ArchiveAllAsync(CancellationToken token)
    {
        var entries = this.Entries.Where(entry => !this.ArchivedIds.Contains(entry.Id)).ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    this.Journal.SetArchived(entry.Id, true, token);
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            this._logger.LogFailure(LogLevel.Warning, "Archive all journal edits", ex);
            this.Notice?.Invoke("Could not archive all edits", "Some entries may already be archived. " + ex.Message, NoticeKind.Error);
            return;
        }
        finally
        {
            await this.RefreshAsync();
        }

        this.Notice?.Invoke(
            "Edits archived", "The entries remain in history. Choose All or Archived history to find them.", NoticeKind.Success);
    }

    private (JournalHistory History, JournalArchiveState Archive, string? ArchiveError) ReadPresentation()
    {
        var history = this._reader.ReadHistory();
        try
        {
            var archive = this.Journal.ReadArchiveState();
            return (history, archive,
                archive.DamagedFiles.Length == 0
                    ? null
                    : $"Unreadable archive markers: {archive.DamagedFiles.Length}. Affected entries remain visible.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (history, new JournalArchiveState(new HashSet<Guid>(), []), ex.Message);
        }
    }

    private string? BasicRevertUnavailableReason(JournalEntry? selected)
    {
        if (this.Error is not null)
        {
            return "Revert requires a complete, readable journal.";
        }

        if (this._request != this._completedRequest)
        {
            return "Refreshing journal history…";
        }

        return selected is null || !this._byId.TryGetValue(selected.Id, out var entry)
            ? "Select one journal edit."
            : entry.RevertUnavailableReason(this.RevertedIds);
    }

    internal string? RevertUnavailableReason(JournalEntry? selected)
    {
        lock (this._eligibilityGate)
        {
            return this.BasicRevertUnavailableReason(selected)
                   ?? (this._eligibility.TryGetValue(selected!.Id, out var eligibility) && eligibility.Ready
                       ? eligibility.Error ?? eligibility.Conflict?.Reason
                       : "Checking for later journal edits…");
        }
    }

    internal JournalConflict? BlockingEdit(JournalEntry selected)
    {
        lock (this._eligibilityGate)
        {
            return this._eligibility.TryGetValue(selected.Id, out var eligibility) && eligibility.Ready ? eligibility.Conflict : null;
        }
    }

    internal Task SetSelectionAsync(object owner, JournalEntry? selected)
    {
        lock (this._eligibilityGate)
        {
            if (selected is null)
            {
                this._selections.Remove(owner);
            }
            else
            {
                this._selections[owner] = selected.Id;
            }

            foreach (var id in this._eligibility.Keys.Where(id => !this._selections.ContainsValue(id)).ToArray())
            {
                this._eligibility.Remove(id);
            }

            return selected is null ? Task.CompletedTask : this.CheckSelected(selected.Id);
        }
    }

    private Task CheckSelected(Guid id)
    {
        if (!this._byId.TryGetValue(id, out var entry) || this.BasicRevertUnavailableReason(entry) is not null)
        {
            return Task.CompletedTask;
        }

        if (!this._eligibility.TryGetValue(id, out var eligibility))
        {
            eligibility = new Eligibility(entry, this._historyVersion);
            this._eligibility.Add(id, eligibility);
            eligibility.Work = this.CheckEligibilityAsync(eligibility, this.Entries, this.RevertedIds);
        }

        return eligibility.Work;
    }

    private async Task CheckEligibilityAsync(Eligibility eligibility, JournalEntry[] entries, IReadOnlySet<Guid> reverted)
    {
        JournalConflict? conflict = null;
        Exception? failure = null;
        try
        {
            conflict = await Task.Run(() => this._findConflict(entries, eligibility.Entry, reverted));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        lock (this._eligibilityGate)
        {
            if (eligibility.Version != this._historyVersion
                || !this._eligibility.TryGetValue(eligibility.Entry.Id, out var current)
                || !ReferenceEquals(current, eligibility))
            {
                return;
            }

            eligibility.Conflict = conflict;
            eligibility.Error = failure is null ? null : "Could not check later journal edits. " + failure.Message;
            eligibility.Ready = true;
        }

        if (failure is not null)
        {
            this._logger.LogFailure(LogLevel.Warning, "Check later journal edits", failure);
        }

        this.RevertCommand.NotifyCanExecuteChanged();
        this.EligibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRevert(JournalEntry? selected)
    {
        return this.RevertUnavailableReason(selected) is null;
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync(JournalEntry? selected, CancellationToken cancellationToken)
    {
        if (selected is null || !this.CanRevert(selected) || this.ConfirmRevert is not { } confirm)
        {
            return;
        }

        using var connection = RemoteRegistryCredentials.Retain(selected.Before.Select(state => state.Scope.Location.Machine));
        try
        {
            if (!await confirm(selected))
            {
                return;
            }

            lock (this._eligibilityGate)
            {
                if (this.BasicRevertUnavailableReason(selected) is { } reason)
                {
                    throw new InvalidOperationException(reason);
                }
            }

            await Task.Run(() => this.Journal.Revert(selected.Id, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            this._logger.LogFailure(LogLevel.Warning, "Revert journal edit", ex);
            await this.RefreshAsync();
            this.Notice?.Invoke("Could not revert edit", ex.Message, NoticeKind.Error);
            return;
        }

        await this.RefreshAsync();
        try
        {
            if (this.RefreshBrowser is { } refresh)
            {
                await refresh();
            }
        }
        catch (Exception ex)
        {
            this._logger.LogFailure(LogLevel.Warning, "Refresh browser after journal revert", ex);
            this.Notice?.Invoke("Edit reverted", "The browser could not refresh. " + ex.Message, NoticeKind.Warning);
            return;
        }

        this.Notice?.Invoke("Edit reverted", "The revert is recorded as a new journal entry.", NoticeKind.Success);
    }

    public async Task RefreshAsync()
    {
        var request = Interlocked.Increment(ref this._request);
        this.RevertCommand.NotifyCanExecuteChanged();
        try
        {
            var display = await Task.Run(this.ReadPresentation);
            var history = display.History;
            if (request != this._request)
            {
                return;
            }

            var error = history.DamagedFiles.Length == 0
                ? null
                : $"Unreadable journal files: {history.DamagedFiles.Length} (including {history.DamagedFiles[0]}). "
                  + "Intact entries are available for inspection. Revert requires the complete history.";
            lock (this._eligibilityGate)
            {
                if (request != this._request)
                {
                    return;
                }

                var changed = this.Error != error
                              || this.Entries.Length != history.Entries.Length
                              || !this.Entries.Zip(history.Entries).All(pair => ReferenceEquals(pair.First, pair.Second));
                this.Entries = history.Entries;
                this._byId = history.Entries.ToDictionary(entry => entry.Id);
                this.RevertedIds = JournalEntry.GetRevertedIds(history.Entries);
                this.ArchivedIds = display.Archive.ArchivedIds;
                this.ArchiveError = display.ArchiveError;
                this.Error = error;
                this._completedRequest = request;
                if (changed)
                {
                    this._historyVersion++;
                    this._eligibility.Clear();
                }

                foreach (var id in this._selections.Values.Distinct().ToArray())
                {
                    _ = this.CheckSelected(id);
                }
            }
        }
        catch (Exception ex)
        {
            if (request != this._request)
            {
                return;
            }

            this._logger.LogFailure(LogLevel.Warning, "Read journal", ex);
            lock (this._eligibilityGate)
            {
                if (request != this._request)
                {
                    return;
                }

                this.Error = ex.Message;
                this._completedRequest = request;
                this._historyVersion++;
                this._eligibility.Clear();
            }
        }

        this.RevertCommand.NotifyCanExecuteChanged();
        this.ArchiveAllCommand.NotifyCanExecuteChanged();
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RegistryActivityCompleted()
    {
        if (this.Changed is not null)
        {
            _ = this.RefreshAsync();
        }
    }

    private sealed record Eligibility(JournalEntry Entry, int Version)
    {
        internal Task Work { get; set; } = Task.CompletedTask;

        internal bool Ready { get; set; }

        internal JournalConflict? Conflict { get; set; }

        internal string? Error { get; set; }
    }
}
