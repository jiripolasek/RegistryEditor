// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class JournalSurfaceViewModel : ObservableObject
{
    internal event Action<JournalEntry>? EntryRequested;

    private readonly Func<RegistryLocation?> _currentLocation;
    private readonly JournalViewModel _history;

    internal JournalArchiveChoice[] ArchiveChoices { get; } =
    [
        new(JournalArchiveView.Active, "Active"), new(JournalArchiveView.All, "All"),
        new(JournalArchiveView.Archived, "Archived")
    ];

    internal JournalOperationChoice[] OperationChoices { get; } =
    [
        new(null, "All operations"), new(JournalOperation.CreateKey, "Create key"),
        new(JournalOperation.RenameKey, "Rename key"),
        new(JournalOperation.DeleteKey, "Delete key"), new(JournalOperation.CopyKey, "Copy key"),
        new(JournalOperation.CreateValue, "Create value"), new(JournalOperation.EditValue, "Edit value"),
        new(JournalOperation.RenameValue, "Rename value"), new(JournalOperation.DeleteValue, "Delete value"),
        new(JournalOperation.Revert, "Revert"), new(JournalOperation.Activity, "Windows activity"),
        new(JournalOperation.Unknown, "Unknown")
    ];

    [ObservableProperty]
    internal partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    internal partial int ScopeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterSummary))]
    internal partial JournalArchiveChoice? ArchiveFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterSummary))]
    internal partial JournalOperationChoice? OperationFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveAction))]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))]
    internal partial JournalEntry? SelectedEntry { get; set; }

    [ObservableProperty]
    internal partial JournalLink[] Links { get; private set; } = [];

    internal string ArchiveAction => this.SelectedEntry is { } entry && this._history.ArchivedIds.Contains(entry.Id)
        ? "Unarchive edit"
        : "Archive edit";

    internal string FilterSummary => (this.ArchiveFilter ?? this.ArchiveChoices[0]).Label + " · " +
                                     (this.OperationFilter ?? this.OperationChoices[0]).Label;

    internal bool IsRevealing { get; private set; }

    internal string BranchScopeLabel => "Branch: " + ToolPresentation.Address(this._currentLocation());

    internal IEnumerable<JournalEntry> VisibleEntries => this._history.Entries.Reverse().Where(this.Includes);

    internal IEnumerable<InvestigationRow> VisibleRows =>
        this.VisibleEntries.Select(entry => InvestigationRow.Journal(entry, this.Status(entry)));

    private bool CanToggleArchive => this.SelectedEntry is { } entry && this._history.FindEntry(entry.Id) is not null;

    internal JournalSurfaceViewModel(JournalViewModel history, Func<RegistryLocation?> currentLocation)
    {
        this._history = history;
        this._currentLocation = currentLocation;
        this.ArchiveFilter = this.ArchiveChoices[0];
        this.OperationFilter = this.OperationChoices[0];
    }

    internal void RefreshScope()
    {
        this.OnPropertyChanged(nameof(this.BranchScopeLabel));
    }

    /// <summary>Describes which history is shown: its owner, scope and any non-default filters.</summary>
    internal string Context(bool administratorOnly)
    {
        var text = (administratorOnly ? "Administrator-only history" : "User-profile history") + (this.ScopeIndex == 1
            ? " · " + ToolPresentation.Address(this._currentLocation())
            : " · All locations");
        if (this.ArchiveFilter?.View != JournalArchiveView.Active || this.OperationFilter?.Operation is not null)
        {
            text += " · " + this.FilterSummary;
        }

        return text;
    }

    internal string Status(JournalEntry entry)
    {
        return ToolPresentation.JournalStatus(entry, this._history.RevertedIds.Contains(entry.Id)) +
               (this._history.ArchivedIds.Contains(entry.Id) ? " · Archived" : string.Empty);
    }

    private bool Includes(JournalEntry entry)
    {
        var archived = this._history.ArchivedIds.Contains(entry.Id);
        if ((this.ArchiveFilter?.View ?? JournalArchiveView.Active) is var view &&
            (view == JournalArchiveView.Active && archived || view == JournalArchiveView.Archived && !archived))
        {
            return false;
        }

        var kind = Enum.IsDefined(entry.OperationKind) ? entry.OperationKind : JournalOperation.Unknown;
        if (this.OperationFilter?.Operation is { } operation && kind != operation)
        {
            return false;
        }

        if (this.ScopeIndex == 1)
        {
            if (this._currentLocation() is not { } scope || !entry.Before.Select(state => state.Scope.Location)
                    .Concat(entry.ActivityLocation is { } location ? (RegistryLocation[])[location] : [])
                    .Any(location => location.View == scope.View &&
                                     string.Equals(
                                         location.Machine, scope.Machine, StringComparison.OrdinalIgnoreCase) &&
                                     (scope.IsComputer || location.Hive == scope.Hive &&
                                         (location.SubKey.Equals(scope.SubKey, StringComparison.OrdinalIgnoreCase) ||
                                          location.SubKey.StartsWith(
                                              scope.SubKey + "\\", StringComparison.OrdinalIgnoreCase) ||
                                          scope.SubKey.Length == 0))))
            {
                return false;
            }
        }

        return this.Query.Length == 0 || entry.Operation.Contains(this.Query, StringComparison.OrdinalIgnoreCase) ||
               ToolPresentation.JournalTarget(entry).Contains(this.Query, StringComparison.OrdinalIgnoreCase) ||
               this.Status(entry).Contains(this.Query, StringComparison.OrdinalIgnoreCase) || entry.Before
                   .SelectMany(state => state.Scope.ValueNames ?? []).Any(name =>
                       (name.Length == 0 ? "(Default)" : name).Contains(
                           this.Query, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSelectedEntryChanged(JournalEntry? value)
    {
        _ = this._history.SetSelectionAsync(this, value);
        this.RefreshLinks();
    }

    internal void Activate()
    {
        this._history.Changed += this.HistoryChanged;
        this._history.EligibilityChanged += this.EligibilityChanged;
        this.HistoryChanged(null, EventArgs.Empty);
        _ = this._history.SetSelectionAsync(this, this.SelectedEntry);
    }

    internal void Deactivate()
    {
        this._history.Changed -= this.HistoryChanged;
        this._history.EligibilityChanged -= this.EligibilityChanged;
        this.ToggleArchiveCommand.Cancel();
        _ = this._history.SetSelectionAsync(this, null);
    }

    private void HistoryChanged(object? sender, EventArgs args)
    {
        this.SelectedEntry = this.SelectedEntry is { } entry ? this._history.FindEntry(entry.Id) : null;
        this.OnPropertyChanged(nameof(this.ArchiveAction));
        this.ToggleArchiveCommand.NotifyCanExecuteChanged();
        this.RefreshLinks();
    }

    private void EligibilityChanged(object? sender, EventArgs args)
    {
        this.RefreshLinks();
    }

    private void RefreshLinks()
    {
        var links = new List<JournalLink>();
        if (this.SelectedEntry is { } entry)
        {
            if (entry.Reverts is { } id && this._history.FindEntry(id) is { } original)
            {
                Add("Original edit", original);
            }

            foreach (var reverted in this._history.Entries.Where(candidate => candidate.Reverts == entry.Id))
            {
                Add(reverted.After is not null && reverted.Error is null ? "Revert edit" : "Revert attempt", reverted);
            }

            if (this._history.BlockingEdit(entry) is { } blocker)
            {
                Add("Blocking later edit", blocker.Entry);
            }
        }

        this.Links = links.ToArray();

        void Add(string label, JournalEntry target)
        {
            links.Add(
                new JournalLink(
                    label + (this._history.ArchivedIds.Contains(target.Id) ? " · Archived" : string.Empty),
                    $"{target.Operation} · {target.Time.ToLocalTime():G} · {target.Path}", target,
                    this.NavigateCommand));
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleArchive))]
    private Task ToggleArchiveAsync(CancellationToken token)
    {
        return this.SelectedEntry is { } entry
            ? this._history.SetArchivedAsync(entry, !this._history.ArchivedIds.Contains(entry.Id), token)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void Navigate(Guid id)
    {
        if (this._history.FindEntry(id) is not { } entry)
        {
            return;
        }

        this.IsRevealing = true;
        try
        {
            this.Query = string.Empty;
            this.ScopeIndex = 0;
            this.ArchiveFilter = this.ArchiveChoices[1];
            this.OperationFilter = this.OperationChoices[0];
        }
        finally
        {
            this.IsRevealing = false;
        }

        this.EntryRequested?.Invoke(entry);
    }

    internal void CopyFiltersFrom(JournalSurfaceViewModel source)
    {
        this.Query = source.Query;
        this.ScopeIndex = source.ScopeIndex;
        this.ArchiveFilter = this.ArchiveChoices.Single(choice =>
            choice.View == (source.ArchiveFilter?.View ?? JournalArchiveView.Active));
        this.OperationFilter
            = this.OperationChoices.Single(choice => choice.Operation == source.OperationFilter?.Operation);
    }
}
