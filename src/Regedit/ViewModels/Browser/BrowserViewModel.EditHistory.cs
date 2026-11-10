// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

public sealed partial class BrowserViewModel
{
    private sealed record EditAction(
        Guid[] Entries,
        string Label,
        RegistryLocation Target,
        string? ValueName,
        bool HasKeys,
        string? BlockedReason = null);

    private readonly List<EditAction> _undoEdits = [];
    private readonly List<EditAction> _redoEdits = [];
    private Guid _historyJournalEntry;
    private RegistryLocation? _historyTarget;
    private string? _historyValue;

    internal event Action<string, string, bool>? HistoryNotice;

    internal event Func<Guid, Task>? HistoryJournalRequested;

    internal Func<string, string, Task<bool>>? ConfirmHistoryChange { get; set; }

    public string UndoLabel => HistoryLabel(this._undoEdits, "Undo");

    public string RedoLabel => HistoryLabel(this._redoEdits, "Redo");

    public string UndoHelp => HistoryHelp(this._undoEdits, "Undo", "Ctrl+Z");

    public string RedoHelp => HistoryHelp(this._redoEdits, "Redo", "Ctrl+Y");

    private bool CanUndo => this.CanPrompt && this._undoEdits.LastOrDefault() is { BlockedReason: null };

    private bool CanRedo => this.CanPrompt && this._redoEdits.LastOrDefault() is { BlockedReason: null };

    private static string HistoryLabel(List<EditAction> stack, string verb)
    {
        return stack.LastOrDefault() is { } edit
            ? edit.BlockedReason is null ? verb + " " + edit.Label : verb + " unavailable"
            : verb;
    }

    private static string HistoryTarget(EditAction edit)
    {
        return edit.Target.Address + $" · {edit.Target.Machine ?? "This computer"}" +
               $" · {(edit.Target.View == RegistryView.Registry32 ? "32-bit" : "64-bit")} registry" +
               (edit.ValueName is null
                   ? string.Empty
                   : "\n" + (edit.ValueName.Length == 0 ? "(Default)" : edit.ValueName));
    }

    private static string HistoryHelp(List<EditAction> stack, string verb, string shortcut)
    {
        return stack.LastOrDefault() is { } edit
            ? $"{HistoryLabel(stack, verb)} ({shortcut})\n{HistoryTarget(edit)}" +
              (edit.BlockedReason is null ? string.Empty : "\n" + edit.BlockedReason)
            : $"{verb} ({shortcut})\nNo edits to {verb.ToLowerInvariant()} in this tab.";
    }

    private void HistoryChanged()
    {
        this.OnPropertyChanged(nameof(this.UndoLabel));
        this.OnPropertyChanged(nameof(this.RedoLabel));
        this.OnPropertyChanged(nameof(this.UndoHelp));
        this.OnPropertyChanged(nameof(this.RedoHelp));
        this.UndoCommand.NotifyCanExecuteChanged();
        this.RedoCommand.NotifyCanExecuteChanged();
    }

    private void RememberEdit(List<JournalEntry> recorded, bool succeeded)
    {
        var changed = recorded.Where(entry => entry.After is null || entry.HasChanges).ToArray();
        if (changed.Length == 0 || this._closed)
        {
            return;
        }

        if (!succeeded || changed.Any(entry => entry.After is null || entry.Error is not null))
        {
            this.BlockHistory(
                changed,
                "An edit failed or its final state could not be recorded. Inspect it in the Journal before continuing.");
            return;
        }

        var entry = changed[0];
        var scope = entry.Before[0].Scope;
        var value = scope.ValueNames?.FirstOrDefault();
        var name = value is null ? scope.Location.Name : value.Length == 0 ? "(Default)" : value;
        var label = changed.Length > 1
            ? $"delete {changed.Length} items"
            : entry.OperationKind switch
            {
                JournalOperation.CreateKey => $"create key “{name}”",
                JournalOperation.RenameKey => $"rename key “{name}”",
                JournalOperation.DeleteKey => $"delete key “{name}”",
                JournalOperation.CopyKey => $"paste key “{name}”",
                JournalOperation.CreateValue => $"create “{name}”",
                JournalOperation.EditValue => $"edit “{name}”",
                JournalOperation.RenameValue => $"rename “{name}”",
                JournalOperation.DeleteValue => $"delete “{name}”",
                _ => entry.Operation.ToLowerInvariant()
            };
        this._undoEdits.Add(
            new EditAction(
                changed.Select(item => item.Id).ToArray(), label, scope.Location, value,
                changed.Any(item => item.Before.Any(state => state.Scope.ValueNames is null))));
        this._redoEdits.Clear();
        this.HistoryChanged();
    }

    private void BlockHistory(IReadOnlyList<JournalEntry> recorded, string reason)
    {
        var entry = recorded.Last();
        this._historyJournalEntry = entry.Id;
        this._undoEdits.Add(
            new EditAction(
                recorded.Select(item => item.Id).ToArray(), "incomplete edit", entry.Before[0].Scope.Location, null,
                false, reason));
        this._redoEdits.Clear();
        this.HistoryChanged();
        this.HistoryNotice?.Invoke("Undo history needs review", reason, true);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync()
    {
        return this.ApplyHistoryAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task RedoAsync()
    {
        return this.ApplyHistoryAsync(true);
    }

    private async Task ApplyHistoryAsync(bool redo)
    {
        var from = redo ? this._redoEdits : this._undoEdits;
        var to = redo ? this._undoEdits : this._redoEdits;
        if (!this.CanPrompt || from.LastOrDefault() is not { BlockedReason: null } edit ||
            this.Registry.Journal is not { } journal)
        {
            return;
        }

        var verb = redo ? "Redo" : "Undo";
        this.CanPrompt = false;
        var recorded = new List<JournalEntry>();
        try
        {
            if (edit.HasKeys && this.ConfirmHistoryChange is { } confirm && !await confirm(
                    verb + " " + edit.Label + "?",
                    HistoryTarget(edit) +
                    "\n\nThis can change an entire key and its subkeys. Registry writes are not atomic. Recreated keys restore owner, group and access rules, but not auditing or other key metadata."))
            {
                return;
            }

            if (this._closed)
            {
                return;
            }

            using var connection = RemoteRegistryCredentials.Retain([edit.Target.Machine]);
            this._historyJournalEntry = edit.Entries.Last();
            try
            {
                await Task.Run(() => journal.TrackEdits(
                    () => journal.Revert(edit.Entries, this._editCancellation.Token), recorded));
            }
            catch (Exception ex)
            {
                this.EditFailed?.Invoke(ex);
                if (this._closed)
                {
                    return;
                }

                var changed = recorded.Where(entry => entry.After is null || entry.HasChanges).ToArray();
                if (recorded.Count > 0)
                {
                    this._historyJournalEntry = recorded.Last().Id;
                    this.RegistryChanged?.Invoke(this, EventArgs.Empty);
                }

                if (changed.Length > 0)
                {
                    this.BlockHistory(
                        changed,
                        verb + " may have partially changed the registry. Inspect the Journal before recovery. " +
                        ex.Message);
                    await this.RefreshHistoryViewsAsync(recorded);
                }
                else
                {
                    this.HistoryNotice?.Invoke("Cannot " + verb.ToLowerInvariant(), ex.Message, true);
                }

                return;
            }

            from.RemoveAt(from.Count - 1);
            to.Add(edit with { Entries = recorded.Select(entry => entry.Id).ToArray() });
            this._historyJournalEntry = recorded.Last().Id;
            this.HistoryChanged();
            var final = recorded.Last();
            var after = final.After!;
            var restored = after.FirstOrDefault(state => state.Snapshot.Keys.Length > 0) ?? after[0];
            this._historyTarget = restored.Scope.ValueNames is null && restored.Snapshot.Keys.Length == 0
                ? restored.Scope.Location.Parent ?? restored.Scope.Location
                : restored.Scope.Location;
            this._historyValue = restored.Scope.ValueNames is null
                ? null
                : restored.Snapshot.Keys[0].Values.FirstOrDefault()?.Name;
            this.RegistryChanged?.Invoke(this, EventArgs.Empty);
            if (await this.RefreshHistoryViewsAsync(recorded) && !this._closed)
            {
                this.Status = verb + " completed: " + edit.Label;
                this.HistoryNotice?.Invoke(verb + " completed", edit.Label + "\n" + HistoryTarget(edit), false);
            }
        }
        finally
        {
            this.CanPrompt = !this._closed;
        }
    }

    private async Task<bool> RefreshHistoryViewsAsync(IReadOnlyList<JournalEntry> recorded)
    {
        try
        {
            await Task.WhenAll(
                this.RefreshAfterHistoryAsync(recorded, true),
                this._historyBroadcaster?.PublishAsync(this, recorded) ?? Task.CompletedTask);

            return true;
        }
        catch (Exception ex)
        {
            this.EditFailed?.Invoke(ex);
            this.HistoryNotice?.Invoke("Registry changed; refresh failed", ex.Message, true);
            return false;
        }
    }

    internal async Task RefreshAfterHistoryAsync(IReadOnlyList<JournalEntry> recorded, bool selectChangedValue = false)
    {
        if (this._closed)
        {
            return;
        }

        await this.WaitForNavigationAsync();
        if (this._closed)
        {
            return;
        }

        var destination = this.Location;
        foreach (var entry in recorded.Where(entry => entry.After is not null))
        {
            foreach (var pair in entry.Before.Zip(entry.After!))
            {
                var removed = pair.First.Scope.Location;
                if (pair.First.Scope.ValueNames is null && pair.First.Snapshot.Keys.Length > 0 &&
                    pair.Second.Snapshot.Keys.Length == 0 && this.LocationAfterKeyChange(removed, null) is { } parent)
                {
                    var replacement = entry.After!.FirstOrDefault(state => state.Scope.ValueNames is null &&
                                                                           state.Snapshot.Keys.Length > 0 &&
                                                                           state.Scope.Location.Parent ==
                                                                           removed.Parent)?.Scope.Location;
                    destination = replacement is null
                        ? parent
                        : this.LocationAfterKeyChange(removed, replacement.Name) ?? parent;
                }
            }
        }

        this.TreeRefreshRequested?.Invoke();
        await this.NavigateAsync(destination, !destination.IsSameLocation(this.Location));
        if (selectChangedValue && this._historyTarget?.IsSameLocation(this.Location) == true &&
            this._historyValue is { } name)
        {
            this.SelectedEntry = this.Entries.FirstOrDefault(entry =>
                entry.Value?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }
        else if (selectChangedValue && this._historyTarget?.Parent?.IsSameLocation(this.Location) == true)
        {
            this.SelectedEntry
                = this.Entries.FirstOrDefault(entry => entry.Key?.IsSameLocation(this._historyTarget) == true);
        }
    }

    [RelayCommand]
    private Task ShowHistoryJournalAsync()
    {
        return this.HistoryJournalRequested?.Invoke(this._historyJournalEntry) ?? Task.CompletedTask;
    }

    [RelayCommand]
    private async Task GoToHistoryChangeAsync()
    {
        if (this._historyTarget is not { } location || this._closed)
        {
            return;
        }

        await this.NavigateAsync(location);
        if (this._historyValue is { } name)
        {
            this.SelectedEntry = this.Entries.FirstOrDefault(entry =>
                entry.Value?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }
    }
}
