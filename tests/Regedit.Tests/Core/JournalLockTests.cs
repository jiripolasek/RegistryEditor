// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalLockTests
{
    internal static async Task HoldAsync(string folder)
    {
        using var lease = new FileStream(
            Path.Combine(folder, "write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("locked");
        await Console.Out.FlushAsync();
        await Console.In.ReadLineAsync();
    }

    internal static async Task RunAsync(Action<bool, string> check, bool registry, bool administratorOnly = false)
    {
        var folder = Path.Combine(
            administratorOnly
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : Path.GetTempPath(), "RegeditJournalLock." + Guid.NewGuid().ToString("N"));
        var journal = new RegistryJournal(folder, administratorOnly);
        var location = RegistryLocation.Parse("HKCU");
        try
        {
            journal.RecordActivity("Fixture", location, "Writer-lock fixture", () => { });
            using (var holder = await HoldLockAsync(folder))
            {
                var ran = false;
                var pending = Task.Run(() => journal.RecordActivity("Wait", location, "test", () => ran = true));
                await StillWaitingAsync(pending);
                check(
                    !ran && Directory.GetFiles(folder, "*.json").Length == 1,
                    "Competing process blocks activity before mutation or pending history");
                await ReleaseAsync(holder);
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                check(
                    ran && journal.ReadEntries().Length == 2,
                    "Journal writer succeeds when the competing process releases before timeout");
            }

            using (var holder = await HoldLockAsync(folder))
            {
                using var cancellation = new CancellationTokenSource();
                var changed = false;
                var pending = Task.Run(() => journal.RecordActivity(
                    "Canceled", location, "test", () => changed = true, cancellation.Token));
                await StillWaitingAsync(pending);
                cancellation.Cancel();
                await CanceledAsync(pending, check, "Activity writer cancellation interrupts contention");
                check(
                    !changed && Directory.GetFiles(folder, "*.json").Length == 2,
                    "Canceled activity creates no history and invokes no mutation");
                await BusyAsync(() => journal.RecordActivity("Expired", location, "test", () => changed = true), check);
                check(
                    !changed && Directory.GetFiles(folder, "*.json").Length == 2,
                    "Expired activity creates no history and invokes no mutation");
                await ReleaseAsync(holder);
            }

            using (var cancellation = new CancellationTokenSource())
            {
                journal.RecordActivity("Committed", location, "test", cancellation.Cancel, cancellation.Token);
                check(
                    journal.ReadEntries().Last().After is not null,
                    "Cancellation after activity starts still saves its completed history");
            }

            var failed = false;
            try
            {
                journal.RecordActivity("Failure", location, "test", () => throw new IOException("fixture failure"));
            }
            catch (IOException ex) when (ex.Message == "fixture failure")
            {
                failed = true;
            }

            journal.RecordActivity("After failure", location, "test", () => { });
            check(
                failed && journal.ReadEntries().Any(entry => entry.Error == "fixture failure"),
                "Failed actions retain their final record and release the writer lock");

            await CheckGateAsync(journal, location, check);
            var badFolder = Path.Combine(folder, "invalid-lock");
            Directory.CreateDirectory(Path.Combine(badFolder, "write.lock"));
            var watch = Stopwatch.StartNew();
            var rejected = false;
            try
            {
                new RegistryJournal(badFolder).RecordActivity("Rejected", location, "test", () => { });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejected = !ex.Message.Contains("Another Registry Editor operation");
            }

            check(
                rejected && watch.Elapsed < TimeSpan.FromSeconds(2),
                "A genuine lock storage error is surfaced without contention retries");

            if (registry)
            {
                await CheckRegistryAsync(journal, folder, check);
            }

            check(
                true,
                administratorOnly
                    ? "Contention uses the secured administrator journal directory and lock"
                    : "Contention uses the ordinary journal directory and lock");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    private static async Task CheckGateAsync(
        RegistryJournal journal,
        RegistryLocation location,
        Action<bool, string> check)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = Task.Run(() => journal.RecordActivity(
            "Gate holder", location, "test", () =>
            {
                check(journal.ReadEntries().Length > 0, "A journal action can reenter its same-process reader gate");
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("Gate fixture was not released.");
                }
            }));
        Task<JournalHistory>? reading = null;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Gate fixture did not enter its action.");
            }

            reading = Task.Run(journal.ReadHistory);
            using var cancellation = new CancellationTokenSource();
            var pending = Task.Run(() => journal.RecordActivity(
                "Gate canceled", location, "test", () => throw new InvalidOperationException("Canceled action ran."),
                cancellation.Token));
            await StillWaitingAsync(pending);
            cancellation.Cancel();
            await CanceledAsync(pending, check, "Cancellation also interrupts the same-process gate");
            await BusyAsync(() => journal.RecordActivity("Gate expired", location, "test", () => { }), check);
            await StillWaitingAsync(reading);
        }
        finally
        {
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var history = await reading!.WaitAsync(TimeSpan.FromSeconds(5));
        check(
            history.DamagedFiles.Length == 0 &&
            history.Entries.Single(entry => entry.Operation == "Gate holder").After is not null,
            "A background history reader waits through writer timeout and displays its final record after release");
        check(
            !journal.ReadEntries().Any(entry => entry.Operation is "Gate canceled" or "Gate expired"),
            "Same-process cancellation and timeout leave no pending records");
    }

    private static async Task CheckRegistryAsync(RegistryJournal journal, string folder, Action<bool, string> check)
    {
        var root = RegistryLocation.Parse($@"HKCU\Software\RegeditJournalLock-{Guid.NewGuid():N}");
        using var user = RegistryKey.OpenBaseKey(root.Hive!.Value, root.View);
        user.CreateSubKey(root.SubKey).Dispose();
        var raw = new RegistryService();
        var writer = new RegistryService { Journal = journal };
        var original = new RegistryValue("Value", RegistryValueType.Binary, [1]);
        var replacement = original with { Data = [2] };
        try
        {
            raw.Write(root, original, null);
            var count = journal.ReadEntries().Length;
            using (var holder = await HoldLockAsync(folder))
            {
                using var cancellation = new CancellationTokenSource();
                var pending = Task.Run(() => writer.Write(root, replacement, original, cancellation.Token));
                await StillWaitingAsync(pending);
                cancellation.Cancel();
                await CanceledAsync(pending, check, "Registry Write cancels before mutation under process contention");
                await BusyAsync(() => writer.Write(root, replacement, original), check);
                check(
                    raw.ReadValue(root, "Value")!.Data.SequenceEqual(original.Data) &&
                    Directory.GetFiles(folder, "*.json").Length == count,
                    "Canceled and expired registry edits leave the value and history untouched");
                await ReleaseAsync(holder);
            }

            writer.Write(root, replacement, original);
            var edit = journal.ReadEntries().Single(entry => entry.Before.Any(state => state.Scope.Location == root));
            count++;
            var model = new JournalViewModel(journal, NullLogger<JournalViewModel>.Instance);
            model.ConfirmRevert = _ => Task.FromResult(true);
            var notices = 0;
            model.Notice += (_, _, _) => notices++;
            await model.RefreshAsync();
            await model.SetSelectionAsync(model, edit);
            using (var holder = await HoldLockAsync(folder))
            {
                var pending = model.RevertCommand.ExecuteAsync(edit);
                await StillWaitingAsync(pending);
                model.RevertCommand.Cancel();
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                await BusyAsync(() => journal.Revert(edit.Id), check);
                check(
                    notices == 0 && raw.ReadValue(root, "Value")!.Data.SequenceEqual(replacement.Data) &&
                    Directory.GetFiles(folder, "*.json").Length == count,
                    "Revert command cancellation and Core timeout preserve the value and history");
                await ReleaseAsync(holder);
            }

            var clipboard
                = new RegistryEntryClipboard { Entry = new RegistryEntry(null, original with { Name = "Paste" }) };
            var browser = new BrowserViewModel(writer, root, clipboard);
            browser.EditPromptRequested += _ => Task.FromResult(true);
            var failures = 0;
            browser.EditFailed += _ => failures++;
            using (var holder = await HoldLockAsync(folder))
            {
                var pending = browser.PasteCommand.ExecuteAsync(null);
                await StillWaitingAsync(pending);
                browser.CancelPending();
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                check(
                    failures == 0 && raw.ReadValue(root, "Paste") is null &&
                    Directory.GetFiles(folder, "*.json").Length == count,
                    "Closing a browser tab cancels its contended paste without creating value or history");
                await ReleaseAsync(holder);
            }

            using (var cancellation = new CancellationTokenSource())
            {
                journal.Execute(
                    "Edit value", [new JournalScope(root, ["Value"])], () =>
                    {
                        raw.Write(root, original, replacement);
                        cancellation.Cancel();
                    }, cancellationToken: cancellation.Token);
                var committed = journal.ReadEntries().Last();
                check(
                    committed.After is not null && committed.Error is null && committed.After[0].Snapshot.Keys[0].Values
                        .Single().Data.SequenceEqual(original.Data),
                    "Cancellation after mutation commits still captures and saves the final registry state");
            }
        }
        finally
        {
            user.DeleteSubKeyTree(root.SubKey, false);
        }
    }

    private sealed class LockHolder : IDisposable
    {
        internal Process Process { get; }

        internal LockHolder(Process process)
        {
            this.Process = process;
        }

        public void Dispose()
        {
            if (!this.Process.HasExited)
            {
                this.Process.Kill(true);
                this.Process.WaitForExit();
            }

            this.Process.Dispose();
        }
    }

    private static async Task<LockHolder> HoldLockAsync(string folder)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(JournalLockTests).Assembly.Location);
        }

        start.ArgumentList.Add("--journal-lock-holder");
        start.ArgumentList.Add(folder);
        var holder = Process.Start(start)!;
        try
        {
            if (await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "locked")
            {
                throw new IOException("Lock holder failed: " + await holder.StandardError.ReadToEndAsync());
            }

            return new LockHolder(holder);
        }
        catch
        {
            if (!holder.HasExited)
            {
                holder.Kill(true);
            }

            holder.Dispose();
            throw;
        }
    }

    private static async Task ReleaseAsync(LockHolder lease)
    {
        var holder = lease.Process;
        await holder.StandardInput.WriteLineAsync("release");
        await holder.StandardInput.FlushAsync();
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (holder.ExitCode != 0)
        {
            throw new IOException("Lock holder failed: " + await holder.StandardError.ReadToEndAsync());
        }
    }

    private static async Task StillWaitingAsync(Task pending)
    {
        if (await Task.WhenAny(pending, Task.Delay(150)) == pending)
        {
            await pending;
            throw new InvalidOperationException("A contended journal operation did not wait.");
        }
    }

    private static async Task CanceledAsync(Task pending, Action<bool, string> check, string message)
    {
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            check(true, message);
            return;
        }

        throw new InvalidOperationException("Canceled journal operation succeeded.");
    }

    private static async Task BusyAsync(Action action, Action<bool, string> check)
    {
        var started = Stopwatch.StartNew();
        try
        {
            await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (IOException ex) when (ex.Message.Contains("Another Registry Editor operation"))
        {
            check(
                started.Elapsed >= TimeSpan.FromSeconds(2.8) && started.Elapsed < TimeSpan.FromSeconds(8) &&
                ex.Message.Contains("close the other Registry Editor windows"),
                "Writer timeout is bounded and explains how to recover");
            return;
        }

        throw new InvalidOperationException("Contended journal operation did not time out.");
    }
}
