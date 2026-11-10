// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class NetworkRegistryLifetimeTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var computer = "ConnectionTest-" + Guid.NewGuid().ToString("N");
        var remote = new RegistryLocation(Machine: computer);
        var other = remote with { Machine = computer + "-other" };
        var first = CreateWindow();
        var second = CreateWindow();
        try
        {
            var local = first.OpenBrowser(new RegistryLocation());
            second.OpenBrowser(new RegistryLocation());
            var tab = first.OpenBrowser(remote);
            var duplicate = first.OpenBrowser(
                remote with { Machine = computer.ToUpperInvariant(), View = RegistryView.Registry32 });
            var elsewhere = second.OpenBrowser(remote);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 3,
                "Tabs across windows and registry views share one computer's credential lifetime");

            first.CloseTab(tab);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 2,
                "Closing one remote tab preserves the other windows' sign-in");
            await duplicate.Browser.NavigateAsync(other);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 2 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 1,
                "Navigating a tab to another computer retains both computers' credentials");
            await duplicate.Browser.NavigateAsync(new RegistryLocation());
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 2 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 1,
                "Navigating to the local computer preserves credentials for remote history");
            await duplicate.Browser.BackCommand.ExecuteAsync(null);
            check(
                duplicate.Browser.Location == other && RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 1,
                "Back navigation reuses a retained remote connection");
            await duplicate.Browser.BackCommand.ExecuteAsync(null);
            await duplicate.Browser.NavigateAsync(remote);
            await duplicate.Browser.NavigateAsync(new RegistryLocation());
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 2 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 1,
                "Repeated visits and computer-name casing do not retain extra connections");

            second.CloseTab(elsewhere);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 1,
                "Closing a remote tab preserves credentials held by another tab's history");
            second.ReopenClosedTabCommand.Execute(null);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 2,
                "Reopening a closed remote tab creates a fresh connection lifetime");
            second.CloseCurrentTabCommand.Execute(null);
            first.CloseTab(duplicate);
            duplicate.Close();
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 0 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 0,
                "Closing a local tab releases all visited computers and repeated cleanup is harmless");

            var settings = new AppSettings();
            var search = new SearchResultsViewModel(
                new SavedSearch(
                    "Remote", [remote, remote with { Machine = computer.ToUpperInvariant() }, other],
                    new SearchOptions("needle")), new InvestigationStore(settings, () => { }),
                (_, _) => throw new InvalidOperationException("The lifetime test must not contact a registry"),
                (_, _) => { });
            first.DockTab(search);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 1 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 1,
                "A remote search retains each distinct computer once without reading the network");
            first.PromoteBottomTab();
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 1,
                "Moving a search between the bottom panel and tab strip keeps its connection");
            first.CloseTab(search);
            search.Close();
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 0 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 0,
                "Closing a search releases its computers and repeated cleanup is harmless");

            var workingTab = first.OpenBrowser(remote);
            var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var snapshots = new BrowserSnapshotsViewModel(new RegistryService());
            snapshots.SaveFileRequested += () => picker.Task;
            var capture = snapshots.CaptureCommand.ExecuteAsync(remote with { Hive = RegistryHive.LocalMachine });
            first.CloseTab(workingTab);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 1,
                "A pending snapshot retains its sign-in after the last browser tab closes");
            picker.SetResult(null);
            await capture;
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 0,
                "Canceling the snapshot picker releases the operation's sign-in");

            var editingTab = first.OpenBrowser(remote with { Hive = RegistryHive.LocalMachine });
            var prompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            editingTab.Browser.EditPromptRequested += _ => prompt.Task;
            var edit = editingTab.Browser.CreateKeyCommand.ExecuteAsync(null);
            first.CloseTab(editingTab);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 1,
                "A pending edit retains its target sign-in until its prompt unwinds after tab closure");
            prompt.SetResult(false);
            await edit;
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 0,
                "Canceling an edit releases the operation's sign-in without accessing the remote registry");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runningSearch = new SearchResultsViewModel(
                new SavedSearch(
                    "Remote", [remote, remote with { Machine = computer.ToUpperInvariant() }, other],
                    new SearchOptions("needle")), new InvestigationStore(settings, () => { }), async (_, _) =>
                {
                    started.SetResult();
                    await finish.Task;
                    check(
                        RemoteRegistryCredentials.ConnectionCount(computer) == 1 &&
                        RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 1,
                        "A closing search keeps all distinct computers until its worker finishes");
                    return new RegistrySearchResults([], 0, false);
                }, (_, _) => { });
            var run = runningSearch.RunCommand.ExecuteAsync(null);
            await started.Task;
            runningSearch.Close();
            finish.SetResult();
            await run;
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 0 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 0,
                "Completed search workers release their remaining sign-ins");

            var last = first.OpenBrowser(remote);
            first.CloseOtherTabs(last);
            first.CloseCurrentTabCommand.Execute(null);
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 1 && first.Tabs.Contains(last),
                "A pending last-tab window close retains credentials until the window actually closes");

            if (registryTests)
            {
                var disconnect = second.OpenBrowser(remote);
                await second.DisconnectCommand.ExecuteAsync(null);
                check(
                    disconnect.Browser.Location.Machine is null &&
                    RemoteRegistryCredentials.ConnectionCount(computer) == 2,
                    "Disconnect preserves credentials for the tab's remote navigation history");
                second.CloseTab(disconnect);
                check(
                    RemoteRegistryCredentials.ConnectionCount(computer) == 1,
                    "Closing a disconnected tab releases its credentials without disturbing another window");
            }
        }
        finally
        {
            first.WindowClosed();
            second.WindowClosed();
        }

        check(
            RemoteRegistryCredentials.ConnectionCount(computer) == 0 &&
            RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 0,
            "Closing windows releases their final remote connections");

        using var canceled = new CancellationTokenSource();
        var workspaceWindow = CreateWindow(location =>
        {
            if (location.Machine == other.Machine)
            {
                canceled.Cancel();
            }
        });
        try
        {
            workspaceWindow.OpenBrowser(new RegistryLocation());
            await workspaceWindow.RestoreWorkspaceAsync(
                new RegistryWorkspace("Canceled", [remote, other], [], []), new WorkspaceRestoreOptions(),
                canceled.Token);
            check(false, "A canceled workspace stops restoring");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested)
        {
            check(
                RemoteRegistryCredentials.ConnectionCount(computer) == 0 &&
                RemoteRegistryCredentials.ConnectionCount(other.Machine!) == 0,
                "A canceled workspace releases staged remote tabs that never reached the window");
        }
        finally
        {
            workspaceWindow.WindowClosed();
        }
    }

    private static MainWindowViewModel CreateWindow(Action<RegistryLocation>? beforeCreate = null)
    {
        var settings = new AppSettings();
        var registry = new RegistryService();
        return new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry, location =>
            {
                beforeCreate?.Invoke(location);
                return new BrowserViewModel(registry, location);
            }, NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
    }
}
