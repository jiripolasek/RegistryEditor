// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class BranchWatchTests
{
    public static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.BranchWatchTests");
        var missingErrors = new Exception[]
        {
            new FileNotFoundException("Enumerated child disappeared"), new IOException("Opened child was deleted", 1018),
            new IOException("Opened child was deleted", unchecked((int)0x800703FA)), new Win32Exception(1018)
        };
        foreach (var missing in missingErrors)
        {
            RegistrySnapshot Read(RegistryLocation location)
            {
                if (location == root)
                {
                    return new RegistrySnapshot(["Gone", "Sibling"], []);
                }

                return location.Name == "Gone"
                    ? throw missing
                    : new RegistrySnapshot([], [new RegistryValue("kept", RegistryValueType.Binary, [7])]);
            }

            var observed = RegistryBranchSnapshot.Capture(Read, root, skipMissingDescendants: true);
            observed.Validate();
            check(
                observed.Keys.Select(key => key.Path).SequenceEqual([string.Empty, "Sibling"])
                && observed.Keys[1].Values.Single().Data[0] == 7,
                $"Watch capture skips vanished child and retains sibling: {missing.GetType().Name}");
            Reject(
                () => RegistryBranchSnapshot.Capture(Read, root), missing, check,
                "Manual capture remains strict for disappearing descendants");
            Reject(
                () => RegistryBranchSnapshot.Capture(Read, root, allowMissingRoot: true), missing, check,
                "Journal capture policy remains strict for disappearing descendants");
            Reject(
                () => RegistryBranchSnapshot.Capture(_ => throw missing, root, skipMissingDescendants: true), missing, check,
                "Missing watch baseline root still fails");
            if (missing is not FileNotFoundException)
            {
                Reject(
                    () => RegistryBranchSnapshot.Capture(_ => throw missing, root, allowMissingRoot: true), missing, check,
                    "Journal capture propagates deletion during a root read");
            }

            var removed = RegistryBranchSnapshot.Capture(_ => throw missing, root, allowMissingRoot: true, skipMissingDescendants: true);
            check(removed.Keys.Length == 0, "Missing watch refresh root produces final empty observation");
        }

        foreach (var failure in new Exception[]
                 {
                     new UnauthorizedAccessException(), new SecurityException(), new Win32Exception(5),
                     new IOException("Access denied", unchecked((int)0x80070005)), new IOException("Unrelated I/O failure"),
                     new OperationCanceledException()
                 })
        {
            foreach (var failAtRoot in new[] { false, true })
            {
                RegistrySnapshot Read(RegistryLocation location)
                {
                    return location == root && !failAtRoot ? new RegistrySnapshot(["Child"], []) : throw failure;
                }

                Reject(
                    () => RegistryBranchSnapshot.Capture(Read, root, allowMissingRoot: true, skipMissingDescendants: true), failure, check,
                    "Watch capture propagates access, cancellation, and unrelated failures unchanged");
            }
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var reads = 0;
        try
        {
            RegistryBranchSnapshot.Capture(
                _ =>
                {
                    reads++;
                    return new([], []);
                }, root, canceled.Token, skipMissingDescendants: true);
            check(false, "Canceled observation must fail");
        }
        catch (OperationCanceledException)
        {
            check(reads == 0, "Watch capture checks cancellation before reading");
        }

        using var duringCapture = new CancellationTokenSource();
        try
        {
            RegistryBranchSnapshot.Capture(
                location =>
                {
                    if (location == root)
                    {
                        return new(["Child"], []);
                    }

                    duringCapture.Cancel();
                    throw new FileNotFoundException();
                }, root, duringCapture.Token, skipMissingDescendants: true);
            check(false, "Cancellation during disappearing-child capture must fail");
        }
        catch (OperationCanceledException)
        {
            check(true, "Skipping a vanished child does not swallow cancellation during capture");
        }

        if (registryTests)
        {
            await RunRegistryAsync(check);
            await RunRootIdentityAsync(check);
        }
    }

    private static async Task RunRegistryAsync(Action<bool, string> check)
    {
        var registry = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.BranchWatchTests." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(root.Parent!, root.Name);
        try
        {
            registry.CreateKey(root, "DeletedHandle");
            using (var deleted = registry.Open(root.Child("DeletedHandle")))
            {
                registry.DeleteKey(root.Child("DeletedHandle"));
                try
                {
                    deleted.GetSubKeyNames();
                    check(false, "Deleted handle enumeration must fail");
                }
                catch (IOException error)
                {
                    check(
                        error.HResult is 1018 or unchecked((int)0x800703FA),
                        $"Native deleted-handle enumeration reports ERROR_KEY_DELETED ({error.HResult:X8})");
                }

                try
                {
                    RegistryService.ReadValue(deleted, "value");
                    check(false, "Deleted handle query must fail");
                }
                catch (Win32Exception error)
                {
                    check(error.NativeErrorCode == 1018, "Native deleted-handle value query reports ERROR_KEY_DELETED as Win32Exception");
                }
            }

            foreach (var duringBaseline in new[] { true, false })
            {
                registry.CreateKey(root, "Gone");
                registry.CreateKey(root, "Sibling");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var remove = duringBaseline ? 1 : 0;
                var removedDuringRead = 0;

                RegistrySnapshot Read(RegistryLocation location)
                {
                    if (location == root.Child("Gone") && Interlocked.Exchange(ref remove, 0) == 1)
                    {
                        registry.DeleteKey(location);
                        Interlocked.Increment(ref removedDuringRead);
                    }

                    return registry.Read(location);
                }

                var watch = RegistryWatcher.RunAsync(
                    registry, root, comparison =>
                    {
                        if (comparison.Differences.Any(change => change.After?.Name == "value" && change.After.Data[0] == 1))
                        {
                            first.TrySetResult();
                        }

                        if (comparison.Differences.Any(change => change.After?.Name == "value" && change.After.Data[0] == 2))
                        {
                            second.TrySetResult();
                        }

                        if (comparison.After.Keys.Length == 0
                            && comparison.Differences.Any(change => change.Kind == RegistryChangeKind.KeyRemoved))
                        {
                            deleted.TrySetResult();
                        }

                        return Task.CompletedTask;
                    }, () => ready.TrySetResult(), cancellation.Token, null, Read);
                try
                {
                    await ready.Task.WaitAsync(cancellation.Token);
                    if (!duringBaseline)
                    {
                        Interlocked.Exchange(ref remove, 1);
                    }

                    registry.Write(root.Child("Sibling"), new RegistryValue("value", RegistryValueType.Binary, [1]), null);
                    await first.Task.WaitAsync(cancellation.Token);
                    check(
                        removedDuringRead == 1 && !watch.IsCompleted,
                        $"Branch watch survives child disappearance during {(duringBaseline ? "baseline" : "refresh")}");
                    var prior = registry.ReadValue(root.Child("Sibling"), "value")!;
                    registry.Write(root.Child("Sibling"), prior with { Data = [2] }, prior);
                    await second.Task.WaitAsync(cancellation.Token);
                    check(!watch.IsCompleted, "Surviving branch watch rearms and observes later sibling changes");
                    if (duringBaseline)
                    {
                        cancellation.Cancel();
                        try
                        {
                            await watch;
                            check(false, "Canceled watch must fail");
                        }
                        catch (OperationCanceledException)
                        {
                            check(true, "Branch watch preserves cancellation after skipped child");
                        }

                        registry.DeleteKey(root.Child("Sibling"));
                    }
                    else
                    {
                        registry.DeleteKey(root);
                        await deleted.Task.WaitAsync(cancellation.Token);
                        try
                        {
                            await watch.WaitAsync(cancellation.Token);
                            check(false, "Deleted watch root must stop");
                        }
                        catch (IOException error)
                        {
                            check(
                                error.Message.Contains("watched key was deleted"),
                                "Root deletion publishes final removals and stops the branch watch");
                        }
                    }
                }
                finally
                {
                    cancellation.Cancel();
                    try
                    {
                        await watch;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        finally
        {
            using var parent = registry.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
        }
    }

    private static async Task RunRootIdentityAsync(Action<bool, string> check)
    {
        var registry = new RegistryService();
        foreach (var duringBaseline in new[] { true, false })
        {
            var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.WatchIdentityTests." + Guid.NewGuid().ToString("N"));
            registry.CreateKey(root.Parent!, root.Name);
            registry.Write(root, new RegistryValue("value", RegistryValueType.Binary, [1]), null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TaskCompletionSource<RegistryComparison>(TaskCreationOptions.RunContinuationsAsynchronously);
            var final = new TaskCompletionSource<RegistryComparison>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replace = duringBaseline ? 1 : 0;
            var deliveries = 0;

            RegistrySnapshot Read(RegistryLocation location)
            {
                if (location == root && Interlocked.Exchange(ref replace, 0) == 1)
                {
                    registry.DeleteKey(root);
                    registry.CreateKey(root.Parent!, root.Name);
                    registry.Write(root, new RegistryValue("replacement", RegistryValueType.Binary, [99]), null);
                }

                return registry.Read(location);
            }

            var watch = RegistryWatcher.RunAsync(
                registry, root, comparison =>
                {
                    if (Interlocked.Increment(ref deliveries) == 1)
                    {
                        first.TrySetResult(comparison);
                    }
                    else
                    {
                        final.TrySetResult(comparison);
                    }

                    return Task.CompletedTask;
                }, () => ready.TrySetResult(), cancellation.Token, null, Read);
            try
            {
                if (!duringBaseline)
                {
                    await ready.Task.WaitAsync(cancellation.Token);
                    registry.Write(
                        root, new RegistryValue("value", RegistryValueType.Binary, [2]),
                        new RegistryValue("value", RegistryValueType.Binary, [1]));
                    var original = await first.Task.WaitAsync(cancellation.Token);
                    check(
                        original.After.Keys[0].Values.Single().Data[0] == 2,
                        "Watch identity fixture first delivers a legitimate observation from the retained key");
                    Interlocked.Exchange(ref replace, 1);
                    registry.Write(
                        root, new RegistryValue("value", RegistryValueType.Binary, [3]),
                        new RegistryValue("value", RegistryValueType.Binary, [2]));
                    var removed = await final.Task.WaitAsync(cancellation.Token);
                    check(
                        removed.Before.Keys[0].Values.Single().Data[0] == 2
                        && removed.After.Keys.Length == 0
                        && removed.Differences.All(change => change.After is null),
                        "Replacement during branch capture yields final old-key removals instead of replacement contents");
                }

                try
                {
                    await watch.WaitAsync(cancellation.Token);
                    check(false, "A replaced root must stop its original watch");
                }
                catch (IOException)
                {
                    check(
                        duringBaseline ? !ready.Task.IsCompleted && deliveries == 0 : deliveries == 2,
                        duringBaseline
                            ? "Root replacement during baseline fails before ready or observations"
                            : "Root replacement stops after the first legitimate and last deletion observations");
                }

                registry.Write(
                    root, new RegistryValue("replacement", RegistryValueType.Binary, [100]),
                    new RegistryValue("replacement", RegistryValueType.Binary, [99]));
                check(
                    watch.IsCompleted && deliveries == (duringBaseline ? 0 : 2),
                    "The stopped old-key session cannot observe later replacement writes");
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    await watch;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }

                using var parent = registry.Open(root.Parent!, true);
                parent.DeleteSubKeyTree(root.Name, false);
            }
        }
    }

    private static void Reject(Action action, Exception expected, Action<bool, string> check, string message)
    {
        try
        {
            action();
        }
        catch (Exception actual) when (ReferenceEquals(actual, expected))
        {
            check(true, message);
            return;
        }

        check(false, message);
    }
}
