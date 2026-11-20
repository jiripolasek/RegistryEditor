// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Notifications;

namespace JPSoftworks.Regedit.Tests.Notifications;

internal static class NotificationSessionTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        void Reject<T>(Action action, string message)
            where T : Exception
        {
            try
            {
                action();
                check(false, message);
            }
            catch (T)
            {
                check(true, message);
            }
        }

        var message = new Notification { Title = "Saved" };
        using var session = new NotificationSession();
        Reject<ArgumentException>(() => session.Show(message with { Title = " " }), "Notification titles must be meaningful");
        Reject<ArgumentException>(
            () => session.Show(message with { Duration = TimeSpan.Zero }), "Zero notification durations are rejected");
        Reject<ArgumentException>(() => session.Show(message with { Key = " " }), "Empty deduplication keys are rejected");
        Reject<ArgumentException>(
            () => session.Show(message with { Severity = (NotificationSeverity)99 }), "Unknown severities are rejected");
        foreach (var value in new[] { -1d, 1.1, double.NaN, double.PositiveInfinity })
        {
            Reject<ArgumentOutOfRangeException>(
                () => session.Show(message with { Progress = new NotificationProgress(value) }),
                "Invalid notification progress is rejected");
        }

        Reject<ArgumentException>(
            () => session.Show(message with { Actions = [new NotificationAction(" ", _ => Task.CompletedTask)] }),
            "Actions need accessible labels");
        Reject<ArgumentNullException>(() => session.Show(message with { Actions = null! }), "Action collections cannot be null");
        Reject<ArgumentNullException>(() => session.Show(message with { Actions = [null!] }), "Action entries cannot be null");
        var selectedAction = 0;
        var secondary = new NotificationAction(
            "Open", _ =>
            {
                selectedAction = 1;
                return Task.CompletedTask;
            });
        var preferred = new NotificationAction(
            "Undo", _ =>
            {
                selectedAction = 2;
                return Task.CompletedTask;
            }) { IsDefault = true };
        Reject<ArgumentException>(
            () => session.Show(message with { Actions = [preferred, secondary with { IsDefault = true }] }),
            "Only one notification action can be the default");
        var choiceActions = new List<NotificationAction> { secondary, preferred };
        var choice = session.Show(message with { Actions = choiceActions });
        choiceActions.Clear();
        check(session.GetActive().Single().Content.Actions.Count == 2, "Published action collections are insulated from caller mutation");
        check(
            ((IList<NotificationAction>)session.GetActive().Single().Content.Actions).IsReadOnly,
            "Snapshots expose a read-only action collection");
        await session.InvokeActionAsync(choice.Id);
        check(selectedAction == 2, "Implicit activation selects the marked default rather than the first action");
        choice = session.Show(message with { Actions = [secondary, preferred] });
        await session.InvokeActionAsync(choice.Id, secondary);
        check(selectedAction == 1, "Explicit activation can choose a secondary action");
        check(
            session.GetHistory().All(item => item.Content.Actions.Count == 0),
            "History releases every action in a multi-action notification");
        Reject<ArgumentException>(() => session.Show(message with { DismissLabel = " " }), "Custom dismiss labels must be meaningful");
        Reject<ArgumentException>(() => session.Show(message with { IconKey = "" }), "Empty custom icon keys are rejected");
        Reject<ArgumentException>(() => session.Show(message with { IconKey = " " }), "Whitespace custom icon keys are rejected");
        var iconMessage = message with { Severity = NotificationSeverity.Neutral, DismissLabel = "Got it", IconKey = "save" };
        var neutral = session.Show(iconMessage);
        var ordinary = session.Show(message);
        check(
            session.GetActive().Single(item => item.Id == neutral.Id).Content.Severity == NotificationSeverity.Neutral,
            "Neutral is an independent notification severity");
        check(
            session.GetActive().Single(item => item.Id == neutral.Id).Content.DismissLabel == "Got it"
            && session.GetActive().Single(item => item.Id == ordinary.Id).Content.DismissLabel is null,
            "A custom dismiss label belongs only to its notification instance");
        check(
            session.GetActive().Single(item => item.Id == neutral.Id).Content.IconKey == "save"
            && session.GetActive().Single(item => item.Id == ordinary.Id).Content.IconKey is null,
            "Custom icon keys belong only to their notification instance");
        neutral.Update(iconMessage with { IconKey = "edit" });
        check(
            session.GetActive().Single(item => item.Id == neutral.Id).Content is
                { IconKey: "edit", Severity: NotificationSeverity.Neutral },
            "A custom icon can change without changing severity or identity");
        neutral.Update(iconMessage with { IconKey = null });
        check(
            session.GetActive().Single(item => item.Id == neutral.Id).Content.IconKey is null,
            "An update can clear a custom icon and restore the severity default");
        ordinary.Update(message with { IconKey = "save", Severity = NotificationSeverity.Error });
        ordinary.Dismiss();
        check(
            session.GetHistory().Single(item => item.Id == ordinary.Id).Content is
                { IconKey: "save", Severity: NotificationSeverity.Error }, "History preserves custom icons independently of severity");
        check(
            (int)NotificationSeverity.Information == 0 && (int)NotificationSeverity.Error == 3,
            "Adding neutral preserves existing severity values");
        session.DismissAll();
        var first = session.Show(message with { Key = "save", Duration = null });
        var second = session.Show(message with { Key = "save", Severity = NotificationSeverity.Success });
        check(first.Id == second.Id && session.GetActive().Length == 1, "Matching keys replace one active message");
        first.Dismiss();
        first.Update(message);
        check(session.GetActive().Length == 0, "A late update cannot resurrect a dismissed notification");
        var next = session.Show(message with { Key = "save" });
        check(next.Id != first.Id, "A dismissed key can be reused with a fresh identity");
        first.Dismiss();
        check(session.GetActive().Single().Id == next.Id, "An old handle cannot dismiss a replacement with the same key");
        var other = session.Show(message with { Key = "other" });
        Reject<ArgumentException>(() => other.Update(message with { Key = "save" }), "Updates cannot steal another active key");
        session.DismissAll();

        using var timed = new NotificationSession(TimeSpan.FromSeconds(5));
        var visible = timed.Show(message with { Duration = TimeSpan.FromSeconds(1) });
        var hidden = timed.Show(message);
        timed.Advance(TimeSpan.FromSeconds(4), [visible.Id]);
        check(timed.GetActive().Length == 2, "The renderer's minimum readable duration is respected");
        timed.Advance(TimeSpan.FromSeconds(20), []);
        check(timed.GetActive().Length == 2, "Hidden and paused cards consume no lifetime");
        timed.Advance(TimeSpan.FromSeconds(1), [visible.Id]);
        check(timed.GetActive().Single().Id == hidden.Id, "Only a presented card expires at its visible deadline");
        timed.Advance(TimeSpan.FromSeconds(4), [hidden.Id]);
        hidden.Update(message);
        timed.Advance(TimeSpan.FromSeconds(1), [hidden.Id]);
        check(timed.GetActive().Length == 1, "An explicit content update restarts the visible lifetime");
        hidden.Dismiss();
        var progress = timed.Show(message with { Progress = NotificationProgress.Indeterminate });
        timed.Advance(TimeSpan.FromDays(1), [progress.Id]);
        check(timed.GetActive().Length == 1, "An operation does not disappear while progress is active");
        progress.Update(message with { Severity = NotificationSeverity.Success });
        timed.Advance(TimeSpan.FromSeconds(5), [progress.Id]);
        check(timed.GetActive().Length == 0, "Completion removes progress and starts the completion lifetime");
        var persistent = timed.Show(message with { Duration = null });
        timed.Advance(TimeSpan.FromDays(1), [persistent.Id]);
        check(timed.GetActive().Length == 1, "Persistent warnings remain available until dismissal");

        using var bounded = new NotificationSession();
        for (var i = 0; i < 120; i++)
        {
            bounded.Show(message with { Actions = [new NotificationAction("Open", _ => Task.CompletedTask)] });
        }

        check(
            bounded.GetActive().Length == 50 && bounded.GetHistory().Length == 50,
            "A notification flood has bounded active and history storage");
        check(bounded.OmittedCount == 20, "Truncated history is counted for the presentation layer");
        check(bounded.GetHistory().All(item => item.Content.Actions.Count == 0), "History releases application action callbacks");
        bounded.DismissAll();
        check(
            bounded.GetActive().Length == 0 && bounded.GetHistory().Length == 50, "Dismiss all archives messages within the history bound");

        using var parallel = new NotificationSession();
        await Task.Run(() => Parallel.For(0, 200, i => parallel.Show(message with { Key = (i % 10).ToString() })));
        check(parallel.GetActive().Length == 10, "Concurrent publishers deduplicate atomically");
        check(session.GetActive().Length == 0, "Window sessions remain independent");

        using var actions = new NotificationSession();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var actionable = actions.Show(
            message with
            {
                Actions =
                [
                    new NotificationAction(
                        "Open", _ =>
                        {
                            calls++;
                            return release.Task;
                        })
                ]
            });
        await actions.InvokeActionAsync(actionable.Id, new NotificationAction("Stale", _ => Task.CompletedTask));
        check(calls == 0, "A stale button cannot invoke a replacement action");
        var invocation = actions.InvokeActionAsync(actionable.Id);
        await actions.InvokeActionAsync(actionable.Id);
        actions.Advance(TimeSpan.FromDays(1), [actionable.Id]);
        check(calls == 1 && actions.GetActive().Single().IsActionRunning, "An awaited action runs once and prevents expiry");
        release.SetResult();
        await invocation;
        check(actions.GetActive().Length == 0, "A successful action dismisses its original message");

        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changing = actions.Show(message with { Actions = [new NotificationAction("Open", _ => release.Task)] });
        invocation = actions.InvokeActionAsync(changing.Id);
        changing.Update(message with { Title = "Newer state" });
        release.SetResult();
        await invocation;
        check(actions.GetActive().Single().Content.Title == "Newer state", "Action completion cannot dismiss a newer revision");
        actions.DismissAll();

        var attempts = 0;
        var failing = actions.Show(
            message with
            {
                IconKey = "save",
                Actions =
                [
                    new NotificationAction(
                        "Retry", _ => ++attempts == 1 ? Task.FromException(new IOException("Unavailable")) : Task.CompletedTask)
                ]
            });
        try
        {
            await actions.InvokeActionAsync(failing.Id);
            check(false, "Action failures reach the application's error handler");
        }
        catch (IOException)
        {
            check(true, "Action failures reach the application's error handler");
        }

        var failed = actions.GetActive().Single();
        check(
            failed.Content.Severity == NotificationSeverity.Error && failed.Content.Duration is null && !failed.IsActionRunning,
            "A failed action remains visible and can be retried");
        check(failed.Content.IconKey == "save", "Action failure changes severity without removing its custom icon");
        await actions.InvokeActionAsync(failing.Id);
        check(actions.GetActive().Length == 0 && attempts == 2, "Retry succeeds after an action failure");

        foreach (var change in new[] { "dismissal", "replacement", "eviction" })
        {
            using var lifetime = new NotificationSession();
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken observed = default;
            var handle = lifetime.Show(
                message with
                {
                    Actions =
                    [
                        new NotificationAction(
                            "Continue", token =>
                            {
                                observed = token;
                                return finish.Task;
                            })
                    ]
                });
            var running = lifetime.InvokeActionAsync(handle.Id);
            if (change == "dismissal")
            {
                handle.Dismiss();
            }
            else if (change == "replacement")
            {
                handle.Update(message with { Title = "Replacement" });
            }
            else
            {
                for (var index = 0; index < NotificationSession.Capacity; index++)
                {
                    lifetime.Show(message with { Duration = null });
                }
            }

            check(
                observed.CanBeCanceled && !observed.IsCancellationRequested && !running.IsCompleted,
                $"Notification {change} preserves the running action's session token");
            finish.SetResult();
            await running;
            check(
                change == "replacement"
                    ? lifetime.GetActive().Single().Content.Title == "Replacement"
                    : lifetime.GetActive().All(item => item.Id != handle.Id),
                $"Action completion after {change} does not resurrect or dismiss newer content");
        }

        var closing = new NotificationSession();
        var canceled = closing.Show(
            message with { Actions = [new NotificationAction("Wait", token => Task.Delay(Timeout.InfiniteTimeSpan, token))] });
        invocation = closing.InvokeActionAsync(canceled.Id);
        closing.Dispose();
        try
        {
            await invocation;
            check(false, "Closing a session cancels its running action");
        }
        catch (OperationCanceledException)
        {
            check(true, "Closing a session cancels its running action");
        }

        canceled.Update(message);
        canceled.Dismiss();
        closing.Dispose();
        check(closing.GetActive().Length == 0 && closing.GetHistory().Length == 0, "Shutdown releases messages and tolerates late handles");
        Reject<ObjectDisposedException>(() => closing.Show(message), "Closed sessions reject new work explicitly");
    }
}
