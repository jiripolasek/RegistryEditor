// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class WorkspacesViewModel : InvestigationViewModel
{
    private readonly InvestigationStore _store;
    private readonly Func<string, RegistryWorkspace> _capture;

    private readonly Func<RegistryWorkspace, WorkspaceRestoreOptions, CancellationToken, Task<WorkspaceRestoreResult>>
        _restore;

    private int _open;

    internal event Func<RegistryWorkspace, Task<WorkspaceRestoreOptions?>>? PreviewRequested;

    internal event Func<string[], Task<bool>>? ConfirmRemote;

    internal WorkspacesViewModel(
        InvestigationStore store,
        Func<string, RegistryWorkspace> capture,
        Func<RegistryWorkspace, WorkspaceRestoreOptions, CancellationToken, Task<WorkspaceRestoreResult>> restore)
        : base(
            "Workspaces",
            "Save groups of registry tabs, search definitions, and watched branches or values for an investigation. Preview a workspace before adding or replacing tabs and choosing whether to start its saved searches and watches.",
            "No saved workspaces",
            "Save your current tabs, searches, and watches to return to this investigation later.")
    {
        this._store = store;
        this._capture = capture;
        this._restore = restore;
        this.Refresh();
        store.WorkspacesChanged += this.Refresh;
    }

    internal override IRelayCommand ActivateCommand => this.OpenCommand;

    internal static string Target(RegistryLocation location)
    {
        return (location.Machine ?? Environment.MachineName) + " · " + location.Address +
               (location.View == Microsoft.Win32.RegistryView.Registry32 ? " · 32-bit" : " · 64-bit");
    }

    internal static string Count(int count, string singular, string plural)
    {
        return $"{count} {(count == 1 ? singular : plural)}";
    }

    internal static string CountSummary(int tabs, int searches, int watches)
    {
        return
            $"{Count(tabs, "tab", "tabs")} · {Count(searches, "search", "searches")} · {Count(watches, "watch", "watches")}";
    }

    private void Refresh()
    {
        this.ReplaceNamedRows(
            this._store.Workspaces.Select(workspace => new InvestigationRow(
                "Workspace", workspace.Name,
                CountSummary(workspace.Tabs.Length, workspace.Searches.Length, workspace.Watches.Length), string.Empty,
                string.Join("\n", workspace.Tabs.Select(tab => tab.Address)),
                string.Join(
                    "\n",
                    workspace.Searches.Select(search => "Search: " + search.Name).Concat(
                        workspace.Watches.Select(watch => "Watch: " + watch.Location.Address +
                                                          (watch.ValueName is null
                                                              ? string.Empty
                                                              : " · " + watch.ValueName)))), workspace)));
    }

    protected override void SelectionChanged()
    {
        base.SelectionChanged();
        this.RemoveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (await this.RequestNameAsync(string.Empty) is { } name && !this.IsClosed)
            {
                var workspace = this._capture(name.Trim());
                this._store.SaveWorkspace(workspace);
                this.Report("Workspace saved", workspace.Name, NoticeKind.Success);
            }
        }
        catch (Exception ex)
        {
            this.Report("Could not save workspace", ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingle), IncludeCancelCommand = true)]
    private async Task OpenAsync(CancellationToken token)
    {
        if (this.Selected is not [{ Source: RegistryWorkspace workspace }])
        {
            return;
        }

        var open = ++this._open;
        try
        {
            workspace.Validate();
            var options = this.PreviewRequested is null ? null : await this.PreviewRequested(workspace);
            token.ThrowIfCancellationRequested();
            if (options is null || this.IsClosed || open != this._open)
            {
                return;
            }

            var machines = workspace.Tabs.Concat(workspace.Searches.SelectMany(search => search.Roots))
                .Select(location => location.Machine).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (machines.Length > 0 && (this.ConfirmRemote is null || !await this.ConfirmRemote(machines)))
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            if (this.IsClosed || open != this._open)
            {
                return;
            }

            var result = await this._restore(workspace, options, token);
            if (this.IsClosed || open != this._open)
            {
                return;
            }

            var summary
                = $"{Count(result.Tabs, "tab", "tabs")} added · {Count(result.Searches, "search", "searches")} started · {Count(result.Watches, "watch", "watches")} requested" +
                  (options.ReplaceTabs
                      ? result.Replaced ? " · Previous browser/search tabs replaced" : " · Existing tabs kept"
                      : string.Empty) + (!options.StartInvestigations
                      ? "\nSaved searches are ready to run; saved watches were not started."
                      : string.Empty);
            this.Report(
                result.Errors.Length == 0 ? "Workspace opened" : "Some workspace items couldn't be opened",
                summary + (result.Errors.Length == 0 ? string.Empty : "\n\n" + string.Join("\n", result.Errors)),
                result.Errors.Length == 0 ? NoticeKind.Success : NoticeKind.Warning);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (open == this._open)
            {
                this.Report("Could not open workspace", ex.Message, NoticeKind.Error);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        try
        {
            if (this.HasSelection)
            {
                this._store.RemoveWorkspaces(this.Selected.Select(row => (RegistryWorkspace)row.Source).ToHashSet());
            }
        }
        catch (Exception ex)
        {
            this.Report("Could not remove workspaces", ex.Message, NoticeKind.Error);
        }
    }

    internal override void Close()
    {
        this._store.WorkspacesChanged -= this.Refresh;
        ++this._open;
        this.OpenCommand.Cancel();
        base.Close();
    }
}
