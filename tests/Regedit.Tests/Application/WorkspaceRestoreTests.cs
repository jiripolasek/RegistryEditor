// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class WorkspaceRestoreTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        check(
            WorkspacesViewModel.CountSummary(1, 0, 2) == "1 tab · 0 searches · 2 watches"
            && WorkspacesViewModel.CountSummary(2, 1, 1) == "2 tabs · 1 search · 1 watch",
            "Workspace summaries use singular labels for individual targets");
        var root = RegistryLocation.Parse(@"HKCU\Software\WorkspaceRestoreTests");
        var workspace = new RegistryWorkspace(
            "Preview", [root], [new SavedSearch("Search", [root], new SearchOptions("needle"))], [new WatchDefinition(root, string.Empty)]);
        var settings = new AppSettings();
        var store = new InvestigationStore(settings, () => { });
        store.SaveWorkspace(workspace);
        var restored = 0;
        WorkspaceRestoreOptions? captured = null;
        var view = new WorkspacesViewModel(
            store, _ => workspace, (target, options, _) =>
            {
                restored++;
                captured = options;
                return Task.FromResult(new WorkspaceRestoreResult(2, 0, 0, false, []));
            });
        view.Selected = [view.Rows.Single()];
        var preview = new TaskCompletionSource<WorkspaceRestoreOptions?>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.PreviewRequested += target =>
        {
            check(ReferenceEquals(target, workspace), "Workspace preview contains the captured saved targets");
            return preview.Task;
        };
        var canceled = view.OpenCommand.ExecuteAsync(null);
        check(restored == 0, "Displaying workspace contents cannot begin restoration");
        preview.SetResult(null);
        await canceled;
        check(restored == 0, "Canceling workspace preview leaves the current session untouched");
        preview = new TaskCompletionSource<WorkspaceRestoreOptions?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var added = view.OpenCommand.ExecuteAsync(null);
        preview.SetResult(new WorkspaceRestoreOptions());
        await added;
        check(
            restored == 1 && captured is { ReplaceTabs: false, StartInvestigations: false },
            "Workspace preview defaults to Add and does not start investigations");
        preview = new TaskCompletionSource<WorkspaceRestoreOptions?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = view.OpenCommand.ExecuteAsync(null);
        view.Close();
        preview.SetResult(new WorkspaceRestoreOptions(true, true));
        await closed;
        check(restored == 1, "Closing a workspace tab while preview is pending cannot install a late restore");
        if (registryTests)
        {
            await RunRegistryAsync(check);
        }
    }

    private static async Task RunRegistryAsync(Action<bool, string> check)
    {
        var registry = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.WorkspaceRestoreTests." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(root.Parent!, root.Name);
        foreach (var name in new[] { "Original", "First", "Second", "Canceled" })
        {
            registry.CreateKey(root, name);
        }

        registry.Write(root.Child("First"), new RegistryValue("Matched", RegistryValueType.Binary, [1]), null);
        var settings = new AppSettings();
        var store = new InvestigationStore(settings, () => { });
        var shell = new MainWindowViewModel(
            settings, store, registry, location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(settings));
        try
        {
            var original = shell.OpenBrowser(root.Child("Original"));
            var note = shell.GetNote(original.Browser.Location);
            note.Text = "Unsaved evidence";
            shell.StartSearch(new SavedSearch("Existing", [root.Child("Original")], new SearchOptions("not present")));
            var oldSearch = (SearchResultsViewModel)shell.ActiveBottomTab!;
            await oldSearch.RunCommand.ExecutionTask!;
            shell.OpenToolCommand.Execute(ToolKind.Notes);
            var tool = shell.ActiveTab!;
            var oldWatch = shell.StartWatch(root.Child("Original"), "Tracked", shell.DispatchWatchUpdate);
            check(await oldWatch.BaselineReady.WaitAsync(TimeSpan.FromSeconds(10)), "Existing workspace watch baseline is ready");
            var workspace = new RegistryWorkspace(
                "Add", [root.Child("First")], [new SavedSearch("Ready", [root], new SearchOptions("Matched"))],
                [new WatchDefinition(root.Child("Original"), "tracked")]);
            var added = await shell.RestoreWorkspaceAsync(workspace, new WorkspaceRestoreOptions(), CancellationToken.None);
            var ready = shell.BottomTabs.OfType<SearchResultsViewModel>().Single(tab => tab.Search.Name == "Ready");
            check(
                added is { Tabs: 2, Searches: 0, Watches: 0, Replaced: false }
                && added.Errors.Length == 0
                && shell.Tabs.Contains(original)
                && shell.BottomTabs.Contains(oldSearch)
                && shell.Tabs.Contains(tool)
                && ready.RunCommand.ExecutionTask is null
                && ready.EmptyTitle == "Ready to search"
                && shell.Tabs.OfType<BrowserTab>().Any(tab => tab.Browser.Location == root.Child("First") && tab.Browser.HasSnapshot)
                && oldWatch.IsRunning
                && shell.Watches.Count == 1
                && note.IsDirty,
                "Add installs browsable prepared tabs and idle search tabs while preserving originals, drafts and running watches");

            var beforeCancel = shell.Tabs.ToArray();
            var queued = System.Threading.Channels.Channel.CreateUnbounded<(Action Action, TaskCompletionSource Done)>();
            var directDispatch = shell.DispatchWatchUpdate;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            shell.DispatchWatchUpdate = action =>
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                queued.Writer.TryWrite((action, done));
                return done.Task.WaitAsync(cancel.Token);
            };
            var preparing = shell.RestoreWorkspaceAsync(
                new RegistryWorkspace("Canceled", [root.Child("Second")], [], [new WatchDefinition(root.Child("Canceled"))]),
                new WorkspaceRestoreOptions(true, true), cancel.Token);
            var pendingBaseline = await queued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            var wasCanceled = false;
            try
            {
                await preparing;
            }
            catch (OperationCanceledException)
            {
                wasCanceled = true;
            }

            try
            {
                pendingBaseline.Action();
                pendingBaseline.Done.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                pendingBaseline.Done.TrySetCanceled();
            }

            shell.DispatchWatchUpdate = directDispatch;
            check(
                wasCanceled
                && shell.Tabs.SequenceEqual(beforeCancel)
                && shell.Watches.SequenceEqual([oldWatch])
                && oldWatch.IsRunning
                && note.IsDirty
                && note.Text == "Unsaved evidence",
                "Cancel during staged watch startup disposes only the new watch and preserves every original tab and draft");

            var staged = new TaskCompletionSource<(Action Action, TaskCompletionSource Done)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStagedDispatch = 0;
            shell.DispatchWatchUpdate = action =>
            {
                if (Interlocked.Exchange(ref firstStagedDispatch, 1) != 0)
                {
                    return directDispatch(action);
                }

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                staged.TrySetResult((action, done));
                return done.Task;
            };
            shell.ActiveTab = original;
            var beforeDialog = shell.Tabs.ToArray();
            var dialogRestore = shell.RestoreWorkspaceAsync(
                new RegistryWorkspace("Dialog race", [root.Child("Second")], [], [new WatchDefinition(root.Child("Canceled"))]),
                new WorkspaceRestoreOptions(true, true), CancellationToken.None);
            var pendingStage = await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(shell.TryBeginDialog(), "An edit dialog can open while workspace staging is pending");
            pendingStage.Action();
            pendingStage.Done.SetResult();
            var refusedForDialog = false;
            try
            {
                await dialogRestore;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Close it and retry", StringComparison.Ordinal))
            {
                refusedForDialog = true;
            }

            check(
                refusedForDialog && shell.Tabs.SequenceEqual(beforeDialog) && ReferenceEquals(shell.ActiveTab, original) && note.IsDirty,
                "A dialog opened during staging blocks workspace commit and keeps the original tab and edit state");
            shell.EndDialog();
            shell.DispatchWatchUpdate = directDispatch;

            shell.ActiveTab = tool;
            var missing = root.Child("Missing");
            var mixed = new RegistryWorkspace(
                "Partial", [root.Child("Second"), missing], [new SavedSearch("Missing search", [missing], new SearchOptions("needle"))],
                [new WatchDefinition(root.Child("Second"), string.Empty), new WatchDefinition(missing)]);
            var partial = await shell.RestoreWorkspaceAsync(mixed, new WorkspaceRestoreOptions(true, true), CancellationToken.None);
            check(
                !partial.Replaced
                && partial.Tabs == 1
                && partial.Watches == 1
                && partial.Errors.Length == 3
                && partial.Errors.All(error => error.Contains(missing.Address) && error.Contains("64-bit"))
                && partial.Errors.Any(error => error.StartsWith("Search: Missing search"))
                && partial.Errors.Any(error => error.StartsWith("Watch:"))
                && shell.Tabs.Contains(original)
                && shell.BottomTabs.Contains(oldSearch)
                && shell.Tabs.Contains(tool)
                && shell.Tabs.OfType<BrowserTab>().Any(tab => tab.Browser.Location == root.Child("Second") && tab.Browser.HasSnapshot)
                && oldWatch.IsRunning
                && note.IsDirty
                && ReferenceEquals(shell.ActiveTab, tool),
                "Failed exact targets retain original tabs while successful staged browsers and watches are added safely");
            var proposals = 0;
            shell.WatchProposed += _ => proposals++;
            var started = await shell.RestoreWorkspaceAsync(workspace, new WorkspaceRestoreOptions(false, true), CancellationToken.None);
            var running = shell.BottomTabs.OfType<SearchResultsViewModel>().Last(tab => tab.Search.Name == "Ready");
            await running.RunCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                started.Searches == 1
                && started.Watches == 1
                && running.Rows.Count == 1
                && shell.Watches.Count == 2
                && proposals == 0
                && ReferenceEquals(shell.Watches[0], oldWatch),
                "Approved workspace Start runs its search and deduplicates its exact watch without proposal prompts");
            var oldTabs = shell.Tabs.Concat(shell.BottomTabs).Where(tab => tab is BrowserTab or SearchResultsViewModel).ToArray();
            var replaced = await shell.RestoreWorkspaceAsync(
                new RegistryWorkspace("Replace", [root.Child("First")], [], []), new WorkspaceRestoreOptions(true, false),
                CancellationToken.None);
            check(
                replaced.Replaced
                && oldTabs.All(tab => !shell.Tabs.Contains(tab) && !shell.BottomTabs.Contains(tab))
                && shell.Tabs.Contains(tool)
                && shell.Tabs.OfType<BrowserTab>().Single().Browser.HasSnapshot
                && shell.Watches.Count == 2
                && shell.Watches.All(watch => watch.IsRunning)
                && ReferenceEquals(shell.Notes.Single(), note)
                && note.IsDirty
                && note.Text == "Unsaved evidence",
                "Explicit successful Replace closes only browser/search tabs and retains tools, note drafts and running watch sessions");
        }
        finally
        {
            shell.WindowClosed();
            await Task.WhenAll(shell.Watches.Select(watch => watch.Completion));
            await Task.WhenAll(
                shell.Tabs.Concat(shell.BottomTabs).OfType<SearchResultsViewModel>()
                    .Select(tab => tab.RunCommand.ExecutionTask ?? Task.CompletedTask));
            using var parent = registry.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
        }
    }
}
