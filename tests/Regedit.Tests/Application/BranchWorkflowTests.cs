// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Services;
// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class BranchWorkflowTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool useRegistry)
    {
        var inspector = new ValueInspectorViewModel(new RegistryValue("Odd", (RegistryValueType)99, [0, 255, 0, 65]));
        inspector.Offset = 1;
        inspector.Length = 2;
        inspector.InterpretCommand.Execute(null);
        check(
            inspector.RawBytes == "00FF0041" && !inspector.HasNumber && inspector.Result.Contains("FF") &&
            inspector.Result.Contains("00"),
            "Value inspection retains the physical bytes and interprets the selected range only");
        inspector.Length = 1.5;
        inspector.InterpretCommand.Execute(null);
        check(inspector.Result.Contains("whole numbers"), "A fractional byte range reports a validation error");

        var registry = new RegistryService();
        var local = RegistryLocation.Parse(
            @"HKCU\Software\RegeditWorkflow-" + Guid.NewGuid().ToString("N"), RegistryView.Registry32);
        var remote = RegistryLocation.Parse(@"\\fixture\HKLM\Software", RegistryView.Registry32);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".regsnap");
        var exported = Path.ChangeExtension(file, ".reg");
        var now = DateTimeOffset.UtcNow;
        var before = new RegistryBranchSnapshot(
            1, local, now, now, [new SnapshotKey(string.Empty, [])], Environment.MachineName);
        var after = before with
        {
            Keys =
            [
                new SnapshotKey(
                    string.Empty, [
                        new RegistryValue("Chosen", RegistryValueType.Binary, [0, 255]),
                        new RegistryValue("Omitted", RegistryValueType.Binary, [9])
                    ])
            ]
        };
        var comparison = new RegistryComparison(before, after);
        var targetRoot = local.Child(new string('p', 200)).Child("Trailing key ");
        var targetBefore = before with { Root = targetRoot };
        var longName = new string('n', 4096) + " ";
        var targetAfter = targetBefore with
        {
            Keys =
            [
                new SnapshotKey(
                    string.Empty, [
                        new RegistryValue(string.Empty, RegistryValueType.Binary, [1]),
                        new RegistryValue(longName, RegistryValueType.Binary, [2])
                    ]),
                new SnapshotKey("Child ", [])
            ]
        };
        var targetComparison = new RegistryComparison(targetBefore, targetAfter);
        var targetInspector = new SnapshotComparisonViewModel(
            targetComparison, () => Task.FromResult<string?>(null), _ => { });
        check(
            !targetInspector.HasTarget && targetInspector.Target == string.Empty,
            "An unselected snapshot has no target readout");
        foreach (var difference in targetComparison.Differences)
        {
            targetInspector.Select([difference], difference);
            var expected = difference.Location.Address +
                           (difference.After is null ? string.Empty : "\r\n" + difference.After.DisplayName);
            check(
                targetInspector.HasTarget && targetInspector.Target == expected,
                "Snapshot target preserves full path name and trailing spaces: " + difference.Kind);
        }

        var unnamed = targetComparison.Differences.Single(item => item.After?.Name == string.Empty);
        var named = targetComparison.Differences.Single(item => item.After?.Name == longName);
        targetInspector.Select([named, unnamed], null);
        check(
            targetInspector.Target == targetRoot.Address + "\r\n(Default)",
            "Multiple snapshot selections retain the current inspected target and unnamed-value display semantics");
        targetInspector.Select([], null);
        check(
            !targetInspector.HasTarget && targetInspector.Target == string.Empty,
            "Clearing snapshot selection removes its target readout");
        var pick = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = new List<string>();
        var diff = new SnapshotComparisonViewModel(comparison, () => pick.Task, copies.Add);
        check(
            !diff.ExportCommand.CanExecute(null) && !diff.CopyPowerShellCommand.CanExecute(null),
            "Comparison commands require a selected difference");
        try
        {
            var chosen = comparison.Differences[0];
            diff.Select([chosen], chosen);
            diff.CopyPowerShellCommand.Execute(null);
            diff.CopyRegExeCommand.Execute(null);
            var export = diff.ExportCommand.ExecuteAsync(null);
            diff.Select([comparison.Differences[1]], null);
            pick.SetResult(exported);
            await export;
            var output = await File.ReadAllTextAsync(exported);
            check(
                output.Contains("Chosen") && !output.Contains("Omitted") && copies.Count == 2 &&
                copies.All(script => script.Contains("Chosen") && !script.Contains("Omitted")),
                "Comparison export captures its selection before the picker and both scripts contain only selected values");

            var foreign = before with { Root = remote, Computer = "fixture" };
            await foreign.SaveAsync(file);
            var snapshots = new BrowserSnapshotsViewModel(registry);
            using var progressSession = new NotificationSession();
            using var snapshotNotification = new OperationNotification(progressSession, "snapshot", snapshots.Cancel);
            snapshots.OperationStarted += snapshotNotification.Start;
            snapshots.Notice += snapshotNotification.ShowResult;
            snapshots.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(snapshots.IsBusy) && !snapshots.IsBusy)
                {
                    snapshotNotification.Complete();
                }
            };
            var snapshotStarts = new List<string>();
            snapshots.OperationStarted += (title, _) => snapshotStarts.Add(title);
            snapshots.OpenFileRequested += () => Task.FromResult<string?>(file);
            var confirmations = 0;
            snapshots.RemoteConfirmationRequested += target =>
            {
                if (target == remote)
                {
                    confirmations++;
                }

                return Task.FromResult(false);
            };
            string? failure = null;
            snapshots.Notice += (_, message, kind) =>
            {
                if (kind == NoticeKind.Error)
                {
                    failure = message;
                }
            };
            await snapshots.CompareFileCommand.ExecuteAsync(null);
            check(
                snapshotStarts is ["Loading snapshot…"],
                "Snapshot file loading posts progress before reading and before remote confirmation");
            check(
                confirmations == 1 && failure is null && snapshots.LastComparison is null && !snapshots.IsBusy,
                "Canceling the remote snapshot confirmation skips the registry read and clears busy state");
            check(
                progressSession.GetActive().Length == 0,
                "Declining remote comparison dismisses loading progress instead of restoring stale started text");

            var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            snapshots.SaveFileRequested += () => held.Task;
            var capture = snapshots.CaptureCommand.ExecuteAsync(local);
            check(
                snapshots.IsBusy && !snapshots.CompareFileCommand.CanExecute(null) && snapshotStarts.Count == 1,
                "A pending snapshot picker owns commands without posting progress or changing the previous notice");
            snapshots.Close();
            held.SetResult(file);
            await capture;
            check(
                snapshots.LastSnapshot is null && snapshotStarts.Count == 1 &&
                (await RegistryBranchSnapshot.LoadAsync(file)).Root == remote,
                "Closing the browser while the snapshot picker is open preserves the existing file");

            var prompts = new BranchDialogs();
            var commands = new RegistryBranchCommands(registry) { Dialogs = prompts };
            check(
                !commands.SaveHiveCommand.CanExecute(new RegistryLocation()) &&
                commands.LoadHiveCommand.CanExecute(RegistryLocation.Parse("HKU")) &&
                !commands.LoadHiveCommand.CanExecute(local) &&
                !commands.UnloadHiveCommand.CanExecute(RegistryLocation.Parse("HKLM")),
                "Hive eligibility follows the explicit target and rejects invalid roots");
            var refreshes = new List<RegistryLocation?>();
            commands.TreeChanged += navigateTo =>
            {
                refreshes.Add(navigateTo);
                return Task.CompletedTask;
            };
            await commands.PermissionsCommand.ExecuteAsync(local);
            check(
                prompts.PermissionsTarget == local && refreshes is [null],
                "Closing permissions refreshes the tree without navigating the browser to the dialog target");

            var exportStarts = new List<string>();
            commands.OperationStarted += (title, _) => exportStarts.Add(title);
            var completedExports = new List<string>();
            commands.ExportCompleted += completedExports.Add;
            await commands.SaveHiveCommand.ExecuteAsync(local);
            await commands.LoadHiveCommand.ExecuteAsync(RegistryLocation.Parse("HKU"));
            await commands.UnloadHiveCommand.ExecuteAsync(RegistryLocation.Parse(@"HKU\RegeditTestMount"));
            await commands.ExportCommand.ExecuteAsync(local);
            await commands.PrintCommand.ExecuteAsync(local);
            check(
                prompts.Calls.SequenceEqual(new[] { "save:.hiv", "open:*", "confirm", "save:.reg", "range" }) &&
                prompts.Preview is null,
                "Canceling pickers, hive confirmation and print range stops before side effects");
            check(
                completedExports.Count == 0 && exportStarts.Count == 0,
                "Canceled export pickers post neither progress nor an output folder");
            var exportNotices = new List<string>();
            var pageNotices = new List<string>();
            commands.ExportNotice += (title, _, _) => exportNotices.Add(title);
            commands.Notice += (title, _, _) => pageNotices.Add(title);
            using (var canceledExport = new CancellationTokenSource())
            {
                canceledExport.Cancel();
                prompts.SavePath = exported;
                await commands.ExportLocationAsync(local, false, canceledExport.Token);
                check(
                    exportNotices is ["Export canceled"] && pageNotices.Count == 0 && exportStarts.Count == 0,
                    "An export canceled after its picker routes the result to the export channel without touching page feedback");
                prompts.SavePath = null;
            }

            if (useRegistry)
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, local.View);
                try
                {
                    using (var key = root.CreateSubKey(local.SubKey))
                    {
                        key.SetValue("OnlyHere", new byte[] { 0, 255 }, RegistryValueKind.Binary);
                        using var child = key.CreateSubKey("Child");
                        child.SetValue("OnlyInChild", "child");
                    }

                    prompts.SavePath = exported;
                    await commands.ExportCommand.ExecuteAsync(local.Child("Child"));
                    var branch = await File.ReadAllTextAsync(exported);
                    check(exportStarts is ["Exporting…"], "Confirmed exports post their own operation progress");
                    check(
                        exportNotices is ["Export canceled", "Export complete"] && pageNotices.Count == 0,
                        "Successful exports publish their terminal result only to the export channel");
                    check(
                        completedExports.SequenceEqual(new[] { exported }),
                        "A completed export publishes its exact output path for the notification action");
                    check(
                        branch.Contains("OnlyInChild") && !branch.Contains("OnlyHere"),
                        "Registry export reads exactly the requested child branch");
                    prompts.Range = ReportRange.CurrentKey;
                    await commands.PrintCommand.ExecuteAsync(local);
                    check(
                        prompts.Preview is { } preview && preview.Title == local.Address &&
                        preview.Report.Contains("OnlyHere") && !preview.Report.Contains("OnlyInChild"),
                        "Print preview receives the original key title, raw value and nonrecursive range");

                    var previewOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    prompts.WaitForPreview = token =>
                    {
                        previewOpened.SetResult();
                        return Task.Delay(Timeout.Infinite, token);
                    };
                    var printing = commands.PrintCommand.ExecuteAsync(local);
                    await previewOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    commands.Close();
                    await printing.WaitAsync(TimeSpan.FromSeconds(5));
                    check(!commands.PrintCommand.IsRunning, "Closing the browser releases its pending print job");

                    var current = new BrowserSnapshotsViewModel(registry);
                    current.SaveFileRequested += () => Task.FromResult<string?>(file);
                    await current.CaptureCommand.ExecuteAsync(local);
                    using (var child = root.OpenSubKey(local.Child("Child").SubKey, true))
                    {
                        child!.SetValue("OnlyInChild", "changed");
                    }

                    await current.CompareLastCommand.ExecuteAsync(null);
                    check(
                        current.LastSnapshot?.Root == local && current.LastComparison?.Differences is
                            [{ Name: "OnlyInChild" }],
                        "The browser retains its successful capture and compares the same branch and view");
                }
                finally
                {
                    root.DeleteSubKeyTree(local.SubKey, false);
                }
            }
        }
        finally
        {
            File.Delete(file);
            File.Delete(exported);
        }
    }

    private sealed class BranchDialogs : IRegistryBranchDialogs
    {
        internal List<string> Calls { get; } = [];

        internal string? SavePath { get; set; }

        internal ReportRange? Range { get; set; }

        internal Func<CancellationToken, Task>? WaitForPreview { get; set; }

        internal (string Title, string Report)? Preview { get; private set; }

        public Task<string?> PickSaveAsync(string name, string extension, string description)
        {
            this.Calls.Add("save:" + extension);
            return Task.FromResult(this.SavePath);
        }

        public Task<string?> PickOpenAsync(string extension)
        {
            this.Calls.Add("open:" + extension);
            return Task.FromResult<string?>(null);
        }

        public Task<bool> ConfirmAsync(string title, string message, string accept)
        {
            this.Calls.Add("confirm");
            return Task.FromResult(false);
        }

        public Task<ReportRange?> ChoosePrintRangeAsync(RegistryLocation location)
        {
            this.Calls.Add("range");
            return Task.FromResult(this.Range);
        }

        public Task PreviewReportAsync(string title, string report, CancellationToken token)
        {
            this.Preview = (title, report);
            return this.WaitForPreview?.Invoke(token) ?? Task.CompletedTask;
        }

        public Task<string?> RequestMountNameAsync(string file, string previous, string? error)
        {
            throw new InvalidOperationException();
        }

        public Task<bool> RequestComputerNameAsync(Func<string, Task> connect)
        {
            throw new InvalidOperationException();
        }

        internal RegistryLocation? PermissionsTarget { get; private set; }

        public void ShowPermissions(RegistryLocation target)
        {
            this.PermissionsTarget = target;
        }
    }
}
