// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Tests.Application;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalSecurityTests
{
    internal static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RegeditJournalPolicy." + Guid.NewGuid().ToString("N"));
        var journal = new RegistryJournal(folder, true);
        var changed = false;
        Reject(
            () => journal.RecordActivity("Rejected", RegistryLocation.Parse("HKCU"), "test", () => changed = true), check,
            "Administrator history refuses a user-profile destination before invoking the edit");
        check(!changed && !Directory.Exists(folder), "Rejected administrator history creates no ordinary-profile data");
    }

    internal static async Task ProbeAsync(string folder)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Start this probe without elevation so both security contexts are tested.");
        }

        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(JournalSecurityTests).Assembly.Location);
        }

        foreach (var arg in new[] { "--journal-security-worker", folder })
        {
            start.ArgumentList.Add(arg);
        }

        using var worker = Process.Start(start)!;
        var checks = new List<string>();

        void Check(bool passed, string name)
        {
            if (!passed)
            {
                throw new InvalidOperationException("FAILED: " + name);
            }

            checks.Add("PASS: " + name);
            File.WriteAllLines(Path.Combine(folder, "medium-results.log"), checks);
            Console.WriteLine(checks[^1]);
        }

        await WaitFor(Path.Combine(folder, "ready"), worker, folder);
        using (var loggers = AppLogging.Open(folder))
        {
            loggers.CreateLogger("SharedProfile").LogInformation("Standard session log entry");
        }

        var root = File.ReadAllText(Path.Combine(folder, "ready"));
        var entry = File.ReadAllText(Path.Combine(folder, "entry"));
        var archive = File.ReadAllText(Path.Combine(folder, "archive"));
        Reject(() => File.ReadAllBytes(archive), Check, "An unelevated process cannot read protected archive metadata");
        Reject(() => File.WriteAllBytes(archive, [0]), Check, "An unelevated process cannot overwrite protected archive metadata");
        Reject(() => File.Delete(archive), Check, "An unelevated process cannot delete protected archive metadata");
        Reject(() => File.ReadAllText(entry), Check, "An unelevated process of the same user cannot read administrator history");
        Reject(() => File.WriteAllText(entry, "overwrite"), Check, "An unelevated process cannot overwrite administrator history");
        Reject(() => File.Delete(entry), Check, "An unelevated process cannot delete administrator history");
        Reject(
            () => File.WriteAllText(Path.Combine(root, "injected.json"), "injected"), Check,
            "An unelevated process cannot inject administrator history");
        Reject(() => Directory.Move(root, root + ".moved"), Check, "An unelevated process cannot replace the journal directory");
        var takeover = new DirectorySecurity();
        takeover.SetAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        Reject(() => new DirectoryInfo(root).SetAccessControl(takeover), Check, "An unelevated process cannot change journal access rules");
        Reject(() => new RegistryJournal(root, true).ReadEntries(), Check, "The journal reader rejects access without elevation");
        File.WriteAllText(Path.Combine(folder, "access-checked"), "checked");
        await WaitFor(Path.Combine(folder, "settings-ready"), worker, folder);
        await SettingsConcurrencyTests.CheckElevatedProcessesAsync(folder, worker, Check);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (worker.ExitCode != 0)
        {
            throw new IOException(File.ReadAllText(Path.Combine(folder, "worker-error.log")));
        }

        var logLines = Directory.GetFiles(Path.Combine(folder, "Logs"), "regedit-*.log").SelectMany(File.ReadAllLines).ToArray();
        Check(
            logLines.Any(line => line.EndsWith("Elevated session log entry", StringComparison.Ordinal))
            && logLines.Any(line => line.EndsWith("Standard session log entry", StringComparison.Ordinal)),
            "Standard and elevated sessions can both write to the profile's Logs folder");
        Check(File.Exists(Path.Combine(folder, "done")), "The elevated probe completed and removed its disposable protected directories");
    }

    internal static async Task WorkerAsync(string folder)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RegeditModern.JournalTests." + Guid.NewGuid().ToString("N"));
        var unsafeRoot = root + ".unsafe";
        var redirect = root + ".redirect";
        var checks = new List<string>();

        void Check(bool passed, string name)
        {
            if (!passed)
            {
                throw new InvalidOperationException("FAILED: " + name);
            }

            checks.Add("PASS: " + name);
            File.WriteAllLines(Path.Combine(folder, "administrator-results.log"), checks);
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            Check(
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "The worker has an enabled administrator token");
            using var loggers = AppLogging.Open(folder);
            loggers.CreateLogger("SharedProfile").LogInformation("Elevated session log entry");
            var journal = new RegistryJournal(root, true);
            Check(journal.ReadEntries().Length == 0, "A protected store can be created before recording any data");
            journal.RecordActivity(
                "Access test", RegistryLocation.Parse("HKCU"), "Disposable non-sensitive access fixture",
                () => Reject(() => Directory.Move(root, root + ".moved"), Check, "Directory handles prevent replacement during an edit"));
            var entry = Directory.GetFiles(root, "*.json").Single();
            var archivedEntry = journal.ReadEntries().Single().Id;
            journal.SetArchived(archivedEntry, true);
            Check(
                new RegistryJournal(root, true).ReadArchiveState().ArchivedIds.Contains(archivedEntry),
                "Protected archive state persists in the secured journal scope");
            var archive = Path.Combine(root, archivedEntry.ToString("N") + ".archive");
            File.WriteAllText(Path.Combine(folder, "archive"), archive);
            File.WriteAllText(Path.Combine(folder, "entry"), entry);
            File.WriteAllText(Path.Combine(folder, "ready"), root);
            await WaitFor(Path.Combine(folder, "access-checked"));
            journal.RecordActivity(
                "Persistence test", RegistryLocation.Parse("HKCU"), "Disposable history retained across journal instances", () => { });
            Check(
                new RegistryJournal(root, true).ReadEntries().Length == 2,
                "Administrator history survives closing and reopening the journal");

            var directorySecurity = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            new DirectoryInfo(unsafeRoot).Create(directorySecurity);
            var userOwned = new DirectorySecurity();
            userOwned.SetOwner(identity.User!);
            new DirectoryInfo(unsafeRoot).SetAccessControl(userOwned);
            var changed = false;
            Reject(
                () => new RegistryJournal(unsafeRoot, true).Execute("Rejected", [], () => changed = true), Check,
                "An administrator-only DACL with an ordinary-user owner is rejected");
            Check(
                !changed && Directory.GetFiles(unsafeRoot).Length == 0,
                "Unsafe ownership is rejected before capture, persistence, or mutation");
            directorySecurity.SetSecurityDescriptorBinaryForm(
                directorySecurity.GetSecurityDescriptorBinaryForm(), AccessControlSections.Owner | AccessControlSections.Access);
            new DirectoryInfo(unsafeRoot).SetAccessControl(directorySecurity);
            new RegistryJournal(unsafeRoot, true).RecordActivity("Repaired", RegistryLocation.Parse("HKCU"), "test", () => { });
            Check(true, "Failed validation releases directory handles for administrator repair");

            Directory.CreateSymbolicLink(redirect, root);
            Reject(() => new RegistryJournal(redirect, true).ReadEntries(), Check, "A redirected journal directory is rejected");
            var display = new JournalHistoryReader(journal);
            Check(display.ReadHistory().Entries.Length == 2, "Protected presentation history validates before warming its cache");
            var original = new FileInfo(entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var exposed = new FileInfo(entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            exposed.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(entry).SetAccessControl(exposed);
            Reject(() => journal.ReadEntries(), Check, "A journal record granting ordinary-user access is rejected");
            Reject(() => display.ReadHistory(), Check, "A warm presentation cache revalidates protected file access rules");
            RestoreFile(entry, original);
            var fileOwner = new FileSecurity();
            fileOwner.SetOwner(identity.User!);
            new FileInfo(entry).SetAccessControl(fileOwner);
            Reject(() => journal.ReadEntries(), Check, "A user-owned journal record is rejected even with an administrator-only DACL");
            Reject(() => display.ReadHistory(), Check, "Presentation refresh revalidates protected file ownership");
            RestoreFile(entry, original);
            var archiveSecurity = new FileInfo(archive).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            new FileInfo(archive).SetAccessControl(exposed);
            Reject(() => journal.ReadArchiveState(), Check, "Archive reads reject an exposed marker ACL");
            Reject(() => journal.SetArchived(archivedEntry, false), Check, "Archive writes reject an exposed existing marker ACL");
            RestoreFile(archive, archiveSecurity);
            journal.SetArchived(archivedEntry, false);
            Check(!journal.ReadArchiveState().ArchivedIds.Contains(archivedEntry), "Protected unarchive persists without removing history");
            var lockFile = Path.Combine(root, "write.lock");
            var originalLock = new FileInfo(lockFile).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            fileOwner.SetOwner(identity.User!);
            new FileInfo(lockFile).SetAccessControl(fileOwner);
            changed = false;
            Reject(
                () => journal.RecordActivity("Rejected", RegistryLocation.Parse("HKCU"), "test", () => changed = true), Check,
                "An unsafe writer lock is rejected before mutation");
            Check(!changed, "No edit runs when the writer lock cannot be secured");
            RestoreFile(lockFile, originalLock);
            Check(
                journal.ReadEntries().Length == 2 && Directory.GetFiles(root, "*.tmp").Length == 0,
                "Security failures preserve existing records without partial files");

            await JournalLockTests.RunAsync(Check, true, true);

            File.WriteAllText(Path.Combine(folder, "settings-ready"), "ready");
            foreach (var closesFirst in new[] { true, false })
            {
                var file = Path.Combine(folder, "processes-" + closesFirst + ".json");
                await WaitFor(file + ".fixture-ready");
                await SettingsConcurrencyTests.RunWriterAsync(["--settings-writer", file, "link", file + ".gate"]);
                File.WriteAllText(file + ".gate.link.closed", "closed");
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "worker-error.log"), ex.ToString());
            throw;
        }
        finally
        {
            // Only remove the three unique, flat fixture directories created by this worker.
            if (Directory.Exists(redirect))
            {
                Directory.Delete(redirect);
            }

            foreach (var path in new[] { root, unsafeRoot })
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.GetFiles(path))
                    {
                        File.Delete(file);
                    }

                    Directory.Delete(path);
                }
            }
        }

        File.WriteAllText(Path.Combine(folder, "done"), "done");
    }

    private static void RestoreFile(string file, FileSecurity security)
    {
        security.SetSecurityDescriptorBinaryForm(
            security.GetSecurityDescriptorBinaryForm(), AccessControlSections.Owner | AccessControlSections.Access);
        new FileInfo(file).SetAccessControl(security);
    }

    private static async Task WaitFor(string file, Process? process = null, string? folder = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (!File.Exists(file))
        {
            if (process?.HasExited == true)
            {
                throw new IOException("Elevated worker exited: " + File.ReadAllText(Path.Combine(folder!, "worker-error.log")));
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static void Reject(Action action, Action<bool, string> check, string message)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            check(true, message);
            return;
        }

        check(false, message);
    }
}
