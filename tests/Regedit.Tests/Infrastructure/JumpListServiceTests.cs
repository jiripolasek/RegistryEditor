// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class JumpListServiceTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditJumpListService.");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(file);
            var favorite = new Favorite("Saved", "HKCU\\Software");
            settings.Favorites.Add(favorite);
            settings.Save(file);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callerThread = Environment.CurrentManagedThreadId;
            var backendThread = 0;
            var reads = 0;
            var calls = 0;
            var persistedBeforePublish = false;
            JumpListEntry[]? secondEntries = null;
            var backend = new TestJumpListBackend(async (entries, saveRemoved) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    backendThread = Environment.CurrentManagedThreadId;
                    entered.SetResult();
                    await release.Task;
                    saveRemoved(new HashSet<string>([entries.Single(entry => entry.FavoriteId is not null).Arguments]));
                    persistedBeforePublish = AppSettings.Load(file).HiddenJumpListFavorites.Contains(favorite.Id);
                }
                else
                {
                    secondEntries = entries;
                }
            });

            AppSettings Load()
            {
                Interlocked.Increment(ref reads);
                return AppSettings.Load(file);
            }

            var first = new JumpListService(Load, backend, file, NullLogger<JumpListService>.Instance);
            var second = new JumpListService(Load, backend, file, NullLogger<JumpListService>.Instance);
            var firstUpdate = first.UpdateAsync();
            Task? secondUpdate = null;
            try
            {
                // Keep the calling thread occupied so a synchronous backend cannot hide behind an async continuation.
                check(
                    entered.Task.Wait(TimeSpan.FromSeconds(10)) && backendThread != callerThread && !firstUpdate.IsCompleted,
                    "Jump List work runs off the caller thread and UpdateAsync returns while the backend is busy");
                secondUpdate = second.UpdateAsync();
                await Task.Delay(100);
                check(reads == 1 && calls == 1, "Another coordinator waits before reading settings or invoking the backend");
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(firstUpdate, secondUpdate ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            }

            check(
                persistedBeforePublish && secondEntries is not null && secondEntries.All(entry => entry.FavoriteId is null),
                "Removals persist before publication and a waiting coordinator reloads them before rebuilding");

            await CheckOtherProcessAsync(file, check);

            var attempts = 0;
            var recovery = new JumpListService(
                Load, new TestJumpListBackend((_, _) =>
                {
                    if (++attempts == 1)
                    {
                        throw new IOException("Backend failure");
                    }

                    return Task.CompletedTask;
                }), file, NullLogger<JumpListService>.Instance);
            await recovery.UpdateAsync();
            await recovery.UpdateAsync();
            check(attempts == 2, "A failed backend update releases the lease and allows the next update");

            var brokenFile = Path.Combine(directory.FullName, "broken.json");
            File.WriteAllText(brokenFile, "invalid settings");
            var published = false;
            var invalid = new JumpListService(
                () => AppSettings.Load(brokenFile), new TestJumpListBackend((_, _) =>
                {
                    published = true;
                    return Task.CompletedTask;
                }), brokenFile, NullLogger<JumpListService>.Instance);
            await invalid.UpdateAsync();
            check(
                !published && File.ReadAllText(brokenFile) == "invalid settings",
                "An unreadable injected profile is preserved without publishing or falling back to the default profile");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task CheckOtherProcessAsync(string file, Action<bool, string> check)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--jump-list-lock-holder");
        start.ArgumentList.Add(file + ".jumplist.lock");
        using var process = Process.Start(start) ?? throw new IOException("Couldn't start the Jump List lock holder.");
        Task? update = null;
        try
        {
            check(
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "locked",
                "A second process holds the Jump List lease");
            var reads = 0;
            var called = false;
            var service = new JumpListService(
                () =>
                {
                    Interlocked.Increment(ref reads);
                    return AppSettings.Load(file);
                }, new TestJumpListBackend((_, _) =>
                {
                    called = true;
                    return Task.CompletedTask;
                }), file, NullLogger<JumpListService>.Instance);
            update = service.UpdateAsync();
            await Task.Delay(100);
            check(reads == 0 && !called, "An external process's lease also prevents stale settings reads and publication");
            await process.StandardInput.WriteLineAsync("release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            check(process.ExitCode == 0 && reads == 1 && called, "Updating resumes after the other process releases its lease");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }

            if (update is not null)
            {
                await update.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    internal static void HoldLease(string path)
    {
        using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("locked");
        Console.ReadLine();
    }
}

internal sealed class TestJumpListBackend(Func<JumpListEntry[], Action<IReadOnlySet<string>>, Task> update) : IJumpListBackend
{
    public Task UpdateAsync(JumpListEntry[] entries, Action<IReadOnlySet<string>> saveRemovedArguments)
    {
        return update(entries, saveRemovedArguments);
    }
}
