// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.Specialized;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class WatchSurfaceTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var collection = new BulkObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => notifications.Add(args.Action);
        collection.AddRange([]);
        collection.AddRange([1]);
        check(
            notifications.SequenceEqual([NotifyCollectionChangedAction.Add]) && collection.SequenceEqual([1]),
            "Empty delivery is silent and one observation uses the native Add fast path");
        collection.AddRange([2, 3]);
        check(
            notifications.SequenceEqual([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Reset]) &&
            collection.SequenceEqual([1, 2, 3]), "Multiple observations use one bulk notification");
        var threw = false;
        try
        {
            collection.AddRange(Enumerable.Range(4, 2).Select(value => value == 4 ? value : throw new IOException()));
        }
        catch (IOException)
        {
            threw = true;
        }

        check(
            threw && collection.SequenceEqual([1, 2, 3]) && notifications.Count == 2,
            "A failed delivery enumeration cannot partially mutate retained observations");
        collection.AddRange(collection);
        check(
            collection.SequenceEqual([1, 2, 3, 1, 2, 3]),
            "Bulk append materializes a source that aliases its destination");

        var registry = new RegistryService();
        var settings = new AppSettings();
        var shell = new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry,
            location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(settings));
        var panel = new WatchSurfaceViewModel(shell);
        var tab = new WatchSurfaceViewModel(shell);
        panel.Activate();
        tab.Activate();
        try
        {
            check(
                !panel.StartBranchCommand.CanExecute(null) && !panel.StartValueCommand.CanExecute(null),
                "Watch start requires an explicit local key target");
            var target = RegistryLocation.Parse(@"HKCU\Software\WatchSurfaceTests", RegistryView.Registry32);
            shell.OpenBrowser(target);
            shell.ContextBrowser!.SelectedEntry = new RegistryEntry(null, null, true);
            check(
                panel.StartBranchCommand.CanExecute(null) && panel.StartValueCommand.CanExecute(null) &&
                panel.BranchTarget.Contains(target.Address) && panel.BranchTarget.Contains("32-bit") &&
                panel.BranchTarget.Contains(Environment.MachineName) && panel.BranchTarget.EndsWith("Branch") &&
                panel.ValueTarget.EndsWith("Value: (Default)"),
                "Before Start the surface distinguishes the exact local branch, view and unset default value");
            shell.ContextBrowser!.SetSelection(
            [
                new RegistryEntry(null, new RegistryValue("One", RegistryValueType.Binary, [])),
                new RegistryEntry(null, new RegistryValue("Two", RegistryValueType.Binary, []))
            ]);
            check(
                !panel.StartValueCommand.CanExecute(null),
                "Multiple selected values cannot silently choose a watch target");
            shell.ContextBrowser.SelectedEntry = new RegistryEntry(null, null, true);
            WatchDefinition? proposal = null;
            shell.WatchProposed += definition => proposal = definition;
            shell.ContextBrowser.WatchValueCommand.Execute(null);
            check(
                proposal is { ValueName: "" } && proposal.Location == target && shell.Watches.Count == 0,
                "Browser value commands capture an exact proposal without starting before approval");
            shell.ContextBrowser.WatchBranchCommand.Execute(null);
            var branchProposal = proposal;
            shell.OpenBrowser(new RegistryLocation());
            check(
                branchProposal is { ValueName: null } && branchProposal.Location == target &&
                shell.StartProposedWatchCommand.CanExecute(branchProposal),
                "Changing browser context cannot change an already displayed branch proposal");
            check(!panel.StartBranchCommand.CanExecute(null), "Computer overview cannot start a branch watch");
            if (!registryTests)
            {
                return;
            }

            await RunRegistryAsync(shell, panel, tab, registry, check);
        }
        finally
        {
            panel.Deactivate();
            tab.Deactivate();
            shell.WindowClosed();
            await Task.WhenAll(shell.Watches.Select(session => session.Completion));
        }
    }

    private static async Task RunRegistryAsync(
        MainWindowViewModel shell,
        WatchSurfaceViewModel panel,
        WatchSurfaceViewModel tab,
        RegistryService registry,
        Action<bool, string> check)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.WatchSurfaceTests." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(root.Parent!, root.Name);
        try
        {
            shell.OpenBrowser(new RegistryLocation());
            Func<Task<WatchDefinition?>> request = () =>
                Task.FromResult<WatchDefinition?>(new WatchDefinition(root, string.Empty));
            panel.TargetRequested += request;
            await panel.NewCommand.ExecuteAsync(null);
            panel.TargetRequested -= request;
            var original = panel.Selected!;
            await Ready(original);
            check(
                original.CaptureLabel.StartsWith("Active · ") && panel.CaptureStatus == original.Status,
                "The selected capture exposes its active state separately from viewing status");
            check(
                original.Definition.Location == root && original.Definition.ValueName == string.Empty,
                "New watch captures the explicit default value while the browser has no key context");
            shell.OpenBrowser(root);
            shell.ContextBrowser!.SelectedEntry = new RegistryEntry(null, null, true);
            tab.StartValueCommand.Execute(null);
            check(
                ReferenceEquals(tab.Selected, original) && shell.Watches.Count == 1 && shell.RunningWatchCount == 1,
                "Two surfaces share one window-owned session for the exact value target");

            WatchObservation Observation(string name, byte value)
            {
                return new WatchObservation(
                    new RegistryDifference(
                        root, RegistryChangeKind.ValueAdded,
                        After: new RegistryValue(name, RegistryValueType.Binary, [value])), DateTimeOffset.UtcNow);
            }

            var first = Observation("ExactOne", 1);
            original.Observations.Add(first);
            panel.SelectedObservations = [first];
            var appends = 0;
            panel.VisibleObservations.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                {
                    appends++;
                }
            };
            panel.Query = "ExactOne";
            panel.PauseViewingCommand.Execute(null);
            original.Observations.Add(Observation("Other", 2));
            check(
                panel.IsViewingPaused && !panel.IsFollowing && panel.PendingCount == 1 &&
                panel.VisibleObservations.Count == 1 && ReferenceEquals(panel.SelectedObservations.Single(), first) &&
                tab.VisibleObservations.Count == 2 && original.IsRunning,
                "Paused viewing retains its rows and selection while capture and the other surface continue");
            check(
                panel.PendingText == "Viewing paused · 1 new observation · Capture continues",
                "A single pending watch change uses singular observation grammar");
            tab.CopyFrom(panel);
            check(
                tab.Query == panel.Query && tab.IsViewingPaused && !tab.IsFollowing &&
                ReferenceEquals(tab.Selected, original) && ReferenceEquals(tab.SelectedObservations.Single(), first),
                "Panel and tab handoff copies view state without creating another watch");
            original.Observations.Add(Observation("ExactOne", 3));
            panel.PauseViewingCommand.Execute(null);
            check(
                panel.VisibleObservations.Count == 2 && panel.PendingCount == 0 && appends == 1 &&
                ReferenceEquals(panel.VisibleObservations[0], first) &&
                ReferenceEquals(panel.SelectedObservations.Single(), first) && tab.IsViewingPaused &&
                tab.VisibleObservations.Count == 1 && tab.PendingCount == 2,
                "Resuming appends pending matches once and leaves an independent paused reader stable");
            panel.FollowLatestCommand.Execute(null);
            check(panel.IsFollowing && !panel.IsViewingPaused, "Follow latest resumes live viewing explicitly");
            tab.Deactivate();
            var retained = tab.VisibleObservations.Count;
            original.Observations.Add(Observation("ExactOne", 4));
            check(
                tab.VisibleObservations.Count == retained && original.IsRunning,
                "Unloading a surface detaches it without stopping the session");
            tab.Activate();
            check(
                tab.IsViewingPaused && tab.PendingCount == 3 && tab.VisibleObservations.Count == retained,
                "Reopening a paused surface preserves its place and reconciles pending observations");

            panel.Deactivate();
            tab.Deactivate();
            original.Observations.Clear();
            for (byte value = 5; value < 9; value++)
            {
                original.Observations.Add(Observation("ExactOne", value));
            }

            panel.Activate();
            tab.Activate();
            check(
                panel.VisibleObservations.Count == 4 && !panel.VisibleObservations.Contains(first) &&
                panel.SelectedObservations.Count == 0 && tab.IsViewingPaused && tab.VisibleObservations.Count == 0 &&
                tab.PendingCount == 4 && tab.SelectedObservations.Count == 0,
                "Detached clear and refill replaces stale rows and selection even when the new count is at least the old count");

            tab.StartBranchCommand.Execute(null);
            var branch = tab.Selected!;
            await Ready(branch);
            check(
                !ReferenceEquals(branch, original) && shell.Watches.Count == 2 && shell.RunningWatchCount == 2,
                "The branch and unnamed value are distinct window-owned targets");
            check(
                !tab.RemoveCommand.CanExecute(null) && !shell.RemoveWatch(branch), "A running watch cannot be removed");
            var failures = new List<WatchSession>();
            shell.WatchFailed += failures.Add;
            tab.StopCommand.Execute(null);
            await branch.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            panel.PauseViewingCommand.Execute(null);
            tab.Selected = original;
            tab.PauseViewingCommand.Execute(null);
            tab.Deactivate();
            original.Observations.Add(Observation("ExactOne", 9));
            panel.StopCommand.Execute(null);
            await original.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                original.CaptureLabel.StartsWith("Stopped · ") && panel.CaptureStatus == original.Status,
                "Stopped captures retain a distinct selector label and capture status");
            check(
                failures.Count == 0 && shell.RunningWatchCount == 0 && original.Observations.Count == 5,
                "Typed Stop is quiet and retains stopped evidence");
            check(
                !panel.IsViewingPaused && panel.PendingCount == 0 && panel.VisibleObservations.Count == 5 &&
                !panel.PauseViewingCommand.CanExecute(null) && !panel.FollowLatestCommand.CanExecute(null),
                "Stopping a paused reader reveals every retained observation without a disabled Resume action");
            tab.Activate();
            check(
                !tab.IsViewingPaused && tab.PendingCount == 0 && tab.VisibleObservations.Count == 5,
                "Reopening a paused reader after its capture stops reconciles all retained evidence");
            panel.Deactivate();
            tab.Deactivate();
            original.Observations.Clear();
            ((BulkObservableCollection<WatchObservation>)original.Observations).AddRange(
                Enumerable.Repeat(first, 10000));
            panel.Activate();
            panel.Selected = original;
            panel.RestartCommand.Execute(null);
            var restarted = panel.Selected!;
            await Ready(restarted);
            check(
                !ReferenceEquals(restarted, original) && original.Observations.Count == 10000 &&
                restarted.Observations.Count == 0 && restarted.IsRunning && shell.Watches.Contains(original) &&
                original.DisplayTitle.Contains("Stopped") &&
                original.DisplayTitle.Contains(original.Started.ToString("HH:mm:ss.fff")),
                "Restart creates a fresh baseline and distinguishable session even when stopped evidence is full");
            tab.Activate();
            tab.Selected = branch;
            tab.RestartCommand.Execute(null);
            var restartedBranch = tab.Selected!;
            await Ready(restartedBranch);
            panel.Selected = branch;
            panel.RestartCommand.Execute(null);
            check(
                ReferenceEquals(panel.Selected, restartedBranch) && shell.Watches.Count == 4,
                "Restart selects an already running exact match instead of duplicating it");
            panel.Selected = original;
            panel.RemoveCommand.Execute(null);
            check(
                !shell.Watches.Contains(original) && restarted.IsRunning && restartedBranch.IsRunning,
                "Remove deletes only the selected stopped session and leaves running sessions intact");
            var failed = shell.StartWatch(root.Child("Missing"), null, shell.DispatchWatchUpdate);
            await failed.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                failures.Count == 1 && ReferenceEquals(failures[0], failed) && failed.Error is FileNotFoundException &&
                shell.Watches.Contains(failed), "An unexpected stop is reported once with its retained exact session");
            WatchSession? revealed = null;
            shell.WatchOpened += session => revealed = session;
            shell.RevealWatchCommand.Execute(failed);
            check(ReferenceEquals(revealed, failed), "The unexpected-stop action routes to the exact failed session");
            shell.ShowRunningWatchCommand.Execute(null);
            check(
                ReferenceEquals(revealed, restartedBranch) && shell.RunningWatchCount == 2,
                "The running count routes to a concrete running session and excludes stopped sessions");
            shell.WindowClosed();
            await Task.WhenAll(shell.Watches.Select(session => session.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            check(
                shell.RunningWatchCount == 0 && failures.Count == 1,
                "Window closure stops every session without generating unexpected-stop notices");
        }
        finally
        {
            foreach (var session in shell.Watches)
            {
                session.Stop();
            }

            await Task.WhenAll(shell.Watches.Select(session => session.Completion));
            using var parent = registry.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
        }
    }

    private static async Task Ready(WatchSession session)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (session.IsRunning && session.Status == "Capturing baseline…")
        {
            if (DateTime.UtcNow >= until)
            {
                throw new TimeoutException("Watch baseline did not finish.");
            }

            await Task.Delay(10);
        }

        if (!session.IsRunning)
        {
            throw new InvalidOperationException(session.Status);
        }
    }
}
