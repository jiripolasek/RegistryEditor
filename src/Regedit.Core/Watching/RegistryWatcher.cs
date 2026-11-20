// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Threading.Channels;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;

namespace JPSoftworks.Regedit.Core.Watching;

public static class RegistryWatcher
{
    public static Task RunAsync(
        RegistryService registry,
        RegistryLocation root,
        Func<RegistryComparison, Task> observed,
        Action? ready,
        CancellationToken token,
        string? valueName = null)
    {
        return RunAsync(registry, root, observed, ready, token, valueName, registry.Read);
    }

    internal static Task RunAsync(
        RegistryService registry,
        RegistryLocation root,
        Func<RegistryComparison, Task> observed,
        Action? ready,
        CancellationToken token,
        string? valueName,
        Func<RegistryLocation, RegistrySnapshot> read)
    {
        if (root.Machine is not null)
        {
            throw new NotSupportedException("Live watch supports local registry keys only.");
        }

        if (valueName is not null)
        {
            RegistryService.ValidateValueName(valueName);
        }

        return Task.Run(
            async () =>
            {
                token.ThrowIfCancellationRequested();
                using var signal = new AutoResetEvent(false);
                using var key = registry.Open(root);

                int Arm()
                {
                    return RegistryNative.NotifyChange(
                        key.Handle, valueName is null ? 1 : 0, valueName is null ? 0x10000005u : 0x10000004u, signal.SafeWaitHandle, 1);
                }

                RegistryBranchSnapshot Capture(bool allowMissingRoot = false)
                {
                    if (valueName is null)
                    {
                        var captured = RegistryBranchSnapshot.Capture(read, root, token, allowMissingRoot, true);
                        try
                        {
                            // Path reads can reach a replacement key after this session's native handle was deleted.
                            _ = key.SubKeyCount;
                        }
                        catch (IOException ex) when (allowMissingRoot
                                                     && ex.HResult is RegistryNative.ErrorKeyDeleted or unchecked((int)0x800703FA))
                        {
                            return captured with { Keys = [] };
                        }

                        return captured;
                    }

                    token.ThrowIfCancellationRequested();
                    var started = DateTimeOffset.UtcNow;
                    SnapshotKey[] keys;
                    try
                    {
                        var value = RegistryService.ReadValue(key, valueName);
                        keys = [new SnapshotKey(string.Empty, value is null ? [] : [value])];
                    }
                    catch (Win32Exception ex) when (allowMissingRoot && ex.NativeErrorCode == RegistryNative.ErrorKeyDeleted)
                    {
                        keys = [];
                    }

                    return new RegistryBranchSnapshot(
                        RegistryBranchSnapshot.CurrentVersion, root, started, DateTimeOffset.UtcNow, keys, Environment.MachineName);
                }

                var pending = Channel.CreateBounded<byte>(
                    new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
                var registration = ThreadPool.RegisterWaitForSingleObject(
                    signal, static (state, _) => ((ChannelWriter<byte>)state!).TryWrite(0), pending.Writer, Timeout.Infinite, false);
                try
                {
                    await ObserveAsync(pending.Reader, Arm, Capture, observed, ready, token).ConfigureAwait(false);
                }
                finally
                {
                    pending.Writer.TryComplete();
                    registration.Unregister(null);
                }
            }, token);
    }

    internal static async Task ObserveAsync(
        ChannelReader<byte> pending,
        Func<int> arm,
        Func<bool, RegistryBranchSnapshot> capture,
        Func<RegistryComparison, Task> observed,
        Action? ready,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var error = arm();
        if (error != 0)
        {
            throw new Win32Exception(error);
        }

        var before = capture(false);
        token.ThrowIfCancellationRequested();
        ready?.Invoke();
        while (true)
        {
            await pending.ReadAsync(token).ConfigureAwait(false);
            // A fixed window bounds coalescing even while writes continue.
            await Task.Delay(125, token).ConfigureAwait(false);
            while (pending.TryRead(out _))
            {
            }

            token.ThrowIfCancellationRequested();
            // Rearm before reading so writes during capture trigger another observation.
            error = arm();
            if (error != 0 && error != RegistryNative.ErrorKeyDeleted)
            {
                throw new Win32Exception(error);
            }

            var now = DateTimeOffset.UtcNow;
            var after = error == RegistryNative.ErrorKeyDeleted ? before with { Started = now, Completed = now, Keys = [] } : capture(true);
            var comparison = new RegistryComparison(before, after, token);
            token.ThrowIfCancellationRequested();
            if (comparison.Differences.Count > 0)
            {
                await observed(comparison).ConfigureAwait(false);
            }

            if (error == RegistryNative.ErrorKeyDeleted || after.Keys.Length == 0)
            {
                throw new IOException("The watched key was deleted. The watch has stopped; start a new watch if it is recreated.");
            }

            before = after;
        }
    }
}
