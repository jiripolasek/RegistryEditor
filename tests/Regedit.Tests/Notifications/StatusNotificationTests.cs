// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.Tests.Notifications;

internal static class StatusNotificationTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var kind in Enum.GetValues<NoticeKind>())
        {
            var content = StatusNotifications.Create("Operation", "Result", kind);
            var expected = kind switch
            {
                NoticeKind.Information => NotificationSeverity.Information,
                NoticeKind.Success => NotificationSeverity.Success,
                NoticeKind.Warning => NotificationSeverity.Warning,
                _ => NotificationSeverity.Error
            };
            check(
                content.Severity == expected && content.Title == "Operation" && content.Message == "Result",
                $"App feedback preserves {kind} severity and content");
            check(
                content.Duration == (kind is NoticeKind.Error or NoticeKind.Warning ? null : TimeSpan.FromSeconds(6)),
                $"App feedback keeps {kind} for its appropriate lifetime");
        }

        var invoked = 0;
        var action = new NotificationAction(
            "Open watch", _ =>
            {
                invoked++;
                return Task.CompletedTask;
            });
        using var session = new NotificationSession();
        var first = StatusNotifications.Show(
            session, "Stopped", "A watch stopped", NoticeKind.Information, [action], "watch-status");
        check(
            session.GetActive() is [{ Content.Duration: null, Content.Actions.Count: 1 }],
            "Feedback with a recovery action stays available until dismissed");
        var replacement = StatusNotifications.Show(
            session, "Stopped again", "Updated reason", NoticeKind.Error, [action], "watch-status");
        check(
            first.Id == replacement.Id && session.GetActive().Length == 1,
            "Keyed app feedback replaces its own status without adding duplicate cards");
        await session.InvokeActionAsync(replacement.Id, action);
        check(
            invoked == 1 && session.GetActive().Length == 0,
            "App feedback executes its recovery action and dismisses the completed card");
        check(
            session.GetHistory() is [{ Content.Actions.Count: 0 }],
            "Closed app feedback releases application callbacks");

        var canceledOperation = 0;
        var previous = StatusNotifications.WithOpenFolderAction(
            StatusNotifications.Create("Hive saved", "Previous output", NoticeKind.Success, key: "operation"),
            Path.Combine(Path.GetTempPath(), "previous.hiv"));
        var previousHandle = session.Show(previous);
        var started = StatusNotifications.Create(
            "Exporting…", "Current output", NoticeKind.Information, key: "operation");
        var running = StatusNotifications.Running(started, () => canceledOperation++);
        var progress = session.Show(running);
        check(
            progress.Id == previousHandle.Id && session.GetActive() is
            [
                {
                    Content:
                    {
                        Title: "Exporting…", Duration: null, Progress: { Value: null }, Actions: [{ Label: "Cancel" }]
                    }
                }
            ], "Explicit operation progress replaces the old message with its own title and Cancel action");
        check(
            previous.Title == "Hive saved" && previous.Progress is null && previous.Actions[0].Label == "Open folder",
            "Preparing operation progress preserves the previous notification content and actions");
        await session.InvokeActionAsync(progress.Id, running.Actions[0]);
        check(canceledOperation == 1, "Operation progress invokes only its supplied cancellation callback");
        var finished = StatusNotifications.Create(
            "Export complete", "Current output", NoticeKind.Success, key: "operation");
        session.Show(finished);
        check(
            session.GetActive() is [{ Content.Progress: null, Content.Actions.Count: 0 }],
            "Terminal operation feedback has no progress or Cancel action");
        session.DismissAll();

        await CheckOperationIsolationAsync(check);

        using (var errors = new NotificationSession())
        {
            var other = StatusNotifications.Show(errors, "Other owner", "Retained", NoticeKind.Warning, key: "other");
            var error = StatusNotifications.UpdateError(errors, null, "Watch action failed", "First failure", "watch");
            var id = error!.Id;
            error = StatusNotifications.UpdateError(errors, error, "Watch action failed", "Second failure", "watch");
            check(
                error!.Id == id && errors.GetActive().Length == 2 &&
                errors.GetActive().Single(item => item.Id == id).Content.Message == "Second failure",
                "Repeated action errors replace one owner's active error without accumulating cards");
            error = StatusNotifications.UpdateError(errors, error, "Watch action failed", null, "watch");
            check(
                error is null && errors.GetActive() is [{ Id: var remaining }] && remaining == other.Id,
                "Clearing an action error dismisses only its own notification and releases its handle");
            error = StatusNotifications.UpdateError(errors, error, "Watch action failed", null, "watch");
            check(error is null && errors.GetActive().Length == 1, "Repeated recovery is harmless");
        }

        var missingExport = Path.Combine(
            Path.GetTempPath(), "Regedit export " + Guid.NewGuid().ToString("N"), "output.reg");
        var exported = StatusNotifications.WithOpenFolderAction(
            StatusNotifications.Create("Export complete", missingExport, NoticeKind.Success), missingExport);
        check(
            exported.Duration is null && exported.Actions is [{ Label: "Open folder", IsDefault: false }],
            "Completed exports retain a lightweight Open folder action without making it the default");
        try
        {
            await exported.Actions[0].ExecuteAsync(CancellationToken.None);
            check(false, "An unavailable export reports its exact file instead of opening another folder");
        }
        catch (FileNotFoundException error)
        {
            check(
                error.FileName == missingExport,
                "An unavailable export reports its exact file instead of opening another folder");
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await exported.Actions[0].ExecuteAsync(canceled.Token);
            check(false, "Canceled folder actions stop before filesystem checks or launching Explorer");
        }
        catch (OperationCanceledException)
        {
            check(true, "Canceled folder actions stop before filesystem checks or launching Explorer");
        }

        try
        {
            StatusNotifications.Create("Invalid", "Invalid severity", (NoticeKind)42);
            check(false, "App feedback rejects an unsupported severity");
        }
        catch (ArgumentOutOfRangeException)
        {
            check(true, "App feedback rejects an unsupported severity");
        }
    }

    private static async Task CheckOperationIsolationAsync(Action<bool, string> check)
    {
        using var session = new NotificationSession();
        var exportCanceled = 0;
        var snapshotCanceled = 0;
        using var export = new OperationNotification(session, "page:export", () => exportCanceled++);
        using var snapshot = new OperationNotification(session, "page:snapshot", () => snapshotCanceled++);
        export.Start("Exporting", "Export work");
        var exportId = session.GetActive().Single().Id;
        snapshot.Start("Loading snapshot", "Snapshot work");
        var snapshotId = session.GetActive().Single(item => item.Content.Key == "page:snapshot").Id;
        var plain = StatusNotifications.Show(
            session, "History changed", "Review the change", NoticeKind.Warning, key: "page");
        check(
            session.GetActive().Length == 3 &&
            session.GetActive().Count(item => item.Content.Progress is not null) == 2,
            "Concurrent export, snapshot, and page feedback retain separate entries and progress");
        snapshot.Start("Comparing snapshot", "Next phase");
        check(
            session.GetActive().Single(item => item.Content.Key == "page:snapshot").Id == snapshotId &&
            session.GetActive().Single(item => item.Id == exportId).Content.Title == "Exporting",
            "A new snapshot phase updates only its own operation entry");
        var exportAction = session.GetActive().Single(item => item.Id == exportId).Content.Actions[0];
        var snapshotAction = session.GetActive().Single(item => item.Id == snapshotId).Content.Actions[0];
        await exportAction.ExecuteAsync(CancellationToken.None);
        await snapshotAction.ExecuteAsync(CancellationToken.None);
        check(exportCanceled == 1 && snapshotCanceled == 1, "Each operation's Cancel action targets its own command");
        export.ShowResult("Export complete", "Output", NoticeKind.Success);
        export.AddOpenFolderAction(Path.Combine(Path.GetTempPath(), "operation-output.reg"));
        export.Complete();
        check(
            session.GetActive().Single(item => item.Id == exportId).Content is
                { Title: "Export complete", Progress: null, Actions: [{ Label: "Open folder" }] } &&
            session.GetActive().Single(item => item.Id == snapshotId).Content.Progress is not null,
            "An export result and folder action preserve concurrent snapshot progress");
        snapshot.Complete();
        check(
            session.GetActive().Length == 2 && session.GetActive().All(item => item.Id != snapshotId) &&
            session.GetActive().Any(item => item.Id == plain.Id),
            "Completing without a result dismisses only the unfinished progress card");
        snapshot.Start("Capturing snapshot", "Work");
        snapshot.ShowResult("Snapshot failed", "Failure", NoticeKind.Error);
        snapshot.Complete();
        check(
            session.GetActive().Single(item => item.Content.Key == "page:snapshot").Content is
                { Title: "Snapshot failed", Duration: null, Progress: null, Actions.Count: 0 },
            "A terminal error survives operation completion without a stale spinner or Cancel");
        export.Dispose();
        export.Start("Late progress", "Closed view");
        export.ShowResult("Late result", "Closed view", NoticeKind.Success);
        check(
            session.GetActive().All(item => item.Content.Key != "page:export") && session.GetActive().Length == 2,
            "Releasing one operation dismisses its card and ignores late publications without touching other entries");
        snapshot.Start("Pending snapshot", "Work");
        snapshot.Complete();
        snapshot.Complete();
        check(
            exportCanceled == 1 && snapshotCanceled == 1,
            "Silent completion and owner disposal do not invoke cancellation callbacks");
    }
}
