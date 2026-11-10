// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.ViewModels.Tools;

/// <summary>Copy and script actions for the selected journal entries or watch observations.</summary>
internal sealed partial class ToolResultActionsViewModel(Action<string> copy) : ObservableObject
{
    private readonly JournalDifferenceCache _differences = new();

    /// <summary>The selected rows in display order.</summary>
    internal IReadOnlyList<InvestigationRow> Selected { get; private set; } = [];

    internal event Action<string, string, NoticeKind>? Notice;

    internal event Action<string, Exception>? ActionFailed;

    internal bool HasSelection => this.Selected.Count > 0;

    internal bool HasChanges => this.Selected.Any(row =>
        row.Source is WatchObservation || row.Source is JournalEntry entry && this._differences.Get(entry).Count > 0);

    internal void Select(IReadOnlyList<InvestigationRow> rows)
    {
        this.Selected = rows;
        this._differences.SetEntries(rows.Select(row => row.Source).OfType<JournalEntry>());
        this.CopyPowerShellCommand.NotifyCanExecuteChanged();
        this.CopyRegExeCommand.NotifyCanExecuteChanged();
        this.CopyPathsCommand.NotifyCanExecuteChanged();
        this.CopyResultsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Drops cached journal differences without changing the selection. Views call this when unloaded; a view
    /// moved to another parent can be unloaded after it was loaded again, so clearing the selection would lose it.
    /// </summary>
    internal void ReleaseCache()
    {
        this._differences.SetEntries([]);
    }

    internal IReadOnlyList<RegistryDifference> Differences(JournalEntry entry)
    {
        return this._differences.Get(entry);
    }

    /// <summary>Differences of the selection; journal entries are replayed in the order they were recorded.</summary>
    internal RegistryDifference[] SelectedChanges()
    {
        IEnumerable<InvestigationRow> rows = this.Selected;
        if (this.Selected.All(row => row.Source is JournalEntry))
        {
            rows = rows.OrderBy(row => ((JournalEntry)row.Source).Time);
        }

        return rows.SelectMany(row => row.Source switch
        {
            WatchObservation observation => [observation.Change],
            JournalEntry entry => this._differences.Get(entry),
            _ => Array.Empty<RegistryDifference>()
        }).ToArray();
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void CopyPowerShell()
    {
        this.CopyScript(RegistryScripts.PowerShell);
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void CopyRegExe()
    {
        this.CopyScript(RegistryScripts.RegExe);
    }

    private void CopyScript(Func<IReadOnlyList<RegistryDifference>, string> format)
    {
        this.Copy(
            () => format(this.SelectedChanges()), "Script copied",
            "Review it before running. Copying does not change the registry.", "Could not copy script");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPaths()
    {
        this.Copy(() => ResultText.Paths(this.Selected), null, null, "Could not copy results");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyResults()
    {
        this.Copy(() => ResultText.Table(this.Selected, row => row.TableSubject), null, null, "Could not copy results");
    }

    [RelayCommand]
    private void CopyExactBytes(RegistryValue value)
    {
        this.Copy(
            () => ResultText.Hex(value), "Exact bytes copied", ResultText.BytesCopied(value),
            "Could not copy exact bytes");
    }

    private void Copy(Func<string> text, string? successTitle, string? successMessage, string failureTitle)
    {
        try
        {
            copy(text());
            if (successTitle is not null)
            {
                this.Notice?.Invoke(successTitle, successMessage ?? string.Empty, NoticeKind.Success);
            }
        }
        catch (Exception ex)
        {
            this.ActionFailed?.Invoke(failureTitle, ex);
            this.Notice?.Invoke(failureTitle, ex.Message, NoticeKind.Error);
        }
    }
}
