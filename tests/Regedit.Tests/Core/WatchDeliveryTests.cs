// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Threading.Channels;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class WatchDeliveryTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\WatchDeliveryTests");
        var now = DateTimeOffset.UtcNow;

        RegistryBranchSnapshot Snapshot(byte value)
        {
            return new RegistryBranchSnapshot(
                1, root, now, now,
                [new SnapshotKey(string.Empty, [new RegistryValue("value", RegistryValueType.Binary, [value])])],
                Environment.MachineName);
        }

        var pending = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<RegistryComparison>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<RegistryComparison>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var arms = 0;
        var value = 0;
        var delivered = 0;
        var duringCapture = false;
        var task = RegistryWatcher.ObserveAsync(
            pending.Reader, () =>
            {
                arms++;
                return 0;
            }, allowMissing =>
            {
                check(arms == ++captures, "Each watch capture is armed first");
                var snapshot = Snapshot((byte)Volatile.Read(ref value));
                if (duringCapture)
                {
                    duringCapture = false;
                    Volatile.Write(ref value, 9);
                    pending.Writer.TryWrite(0);
                }

                return snapshot;
            }, comparison =>
            {
                if (Interlocked.Increment(ref delivered) == 1)
                {
                    first.TrySetResult(comparison);
                }
                else
                {
                    second.TrySetResult(comparison);
                }

                return Task.CompletedTask;
            }, () => baseline.TrySetResult(), cancellation.Token);
        try
        {
            await baseline.Task.WaitAsync(cancellation.Token);
            for (var i = 1; i <= 1000; i++)
            {
                Volatile.Write(ref value, 7);
                pending.Writer.TryWrite(0);
            }

            duringCapture = true;
            var comparison = await first.Task.WaitAsync(cancellation.Token);
            check(
                comparison.Before.Keys[0].Values[0].Data[0] == 0 && comparison.After.Keys[0].Values[0].Data[0] == 7,
                "A burst coalesces to exact observed baseline and final bytes");
            var followup = await second.Task.WaitAsync(cancellation.Token);
            check(
                followup.Before.Keys[0].Values[0].Data[0] == 7 && followup.After.Keys[0].Values[0].Data[0] == 9 &&
                captures == 3, "A notification during capture schedules one later final-state observation");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        check(captures == 3, "Canceled idle delivery does not perform a late capture");

        using var sustained = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var sustainedResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var baselineReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = RegistryWatcher.ObserveAsync(
            stream.Reader, () => 0, _ => Snapshot((byte)Volatile.Read(ref current)), _ =>
            {
                sustainedResult.TrySetResult();
                return Task.CompletedTask;
            }, () => baselineReady.TrySetResult(), sustained.Token);
        await baselineReady.Task.WaitAsync(sustained.Token);
        var producer = Task.Run(async () =>
        {
            while (!sustained.IsCancellationRequested)
            {
                Volatile.Write(ref current, 1);
                stream.Writer.TryWrite(0);
                await Task.Delay(10, sustained.Token);
            }
        });
        try
        {
            await sustainedResult.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(
                !producer.IsCompleted,
                "Fixed coalescing delivers during sustained notifications without waiting for quiet");
        }
        finally
        {
            sustained.Cancel();
            try
            {
                await producer;
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await consumer;
            }
            catch (OperationCanceledException)
            {
            }
        }

        using var deleting = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var deletionSignal = Channel.CreateBounded<byte>(1);
        deletionSignal.Writer.TryWrite(0);
        var armCount = 0;
        var readCount = 0;
        RegistryComparison? final = null;
        try
        {
            await RegistryWatcher.ObserveAsync(
                deletionSignal.Reader, () => ++armCount == 1 ? 0 : 1018, _ =>
                {
                    readCount++;
                    return Snapshot(3);
                }, comparison =>
                {
                    final = comparison;
                    return Task.CompletedTask;
                }, null, deleting.Token);
            check(false, "Deleted root must stop");
        }
        catch (IOException)
        {
            check(
                readCount == 1 && final is { After.Keys.Length: 0 } && final.Differences.Count == 2,
                "A deleted native handle delivers final removals without reading a recreated root");
        }

        using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stoppedSignal = Channel.CreateBounded<byte>(1);
        stoppedSignal.Writer.TryWrite(0);
        var reads = 0;
        var stoppedTask = RegistryWatcher.ObserveAsync(
            stoppedSignal.Reader, () => 0, _ =>
            {
                reads++;
                return Snapshot(0);
            }, _ => throw new InvalidOperationException("Late observation"), null, stopped.Token);
        stopped.Cancel();
        try
        {
            await stoppedTask;
        }
        catch (OperationCanceledException)
        {
        }

        check(reads == 1, "Stop during the coalescing window prevents another capture and delivery");

        var denied = new Win32Exception(5);
        try
        {
            await RegistryWatcher.ObserveAsync(
                Channel.CreateBounded<byte>(1).Reader, () => throw denied, _ => Snapshot(0), _ => Task.CompletedTask,
                null, CancellationToken.None);
            check(false, "Failed native registration must propagate");
        }
        catch (Win32Exception actual) when (ReferenceEquals(actual, denied))
        {
            check(true, "Native registration failures propagate unchanged");
        }
    }
}
