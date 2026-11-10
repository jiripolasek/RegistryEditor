// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalCaptureTests
{
    internal static void Run(Action<bool, string> check, bool registry)
    {
        using var queryKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        var budget = new JournalCaptureBudget(8);
        var reads = 0;
        var queries = 0;

        int Growing(byte[]? bytes, out uint type, ref uint size)
        {
            type = 3;
            if (bytes is null)
            {
                size = ++queries == 1 ? 4u : 8u;
                return 0;
            }

            reads++;
            if (reads == 1)
            {
                size = 8;
                return 234;
            }

            bytes.AsSpan().Fill(7);
            size = 8;
            return 0;
        }

        var value = RegistryService.ReadValue(queryKey, "grow", budget, Growing);
        check(
            value is { Data.Length: 8 } && value.Data.All(item => item == 7) && budget.Remaining == 0 && reads == 2,
            "Changing native values check each allocation and charge only their final retained bytes");

        reads = queries = 0;
        budget = new JournalCaptureBudget(7);
        Reject(() => RegistryService.ReadValue(queryKey, "grow", budget, Growing));
        check(
            reads == 1 && budget.Remaining == 7,
            "A growing native size is rejected before another allocation without charging the failed attempt");
        reads = 0;

        int Oversized(byte[]? bytes, out uint type, ref uint size)
        {
            type = 3;
            size = uint.MaxValue;
            if (bytes is not null)
            {
                reads++;
            }

            return 0;
        }

        Reject(() => RegistryService.ReadValue(queryKey, "huge", new JournalCaptureBudget(8), Oversized));
        check(reads == 0, "An oversized native size is refused before allocating or requesting its data");
        reads = queries = 0;

        int EmptyThenGrowing(byte[]? bytes, out uint type, ref uint size)
        {
            type = 3;
            if (bytes is null)
            {
                size = ++queries == 1 ? 0u : 8u;
                return 0;
            }

            reads++;
            size = 8;
            if (bytes.Length != 0)
            {
                bytes.AsSpan().Fill(9);
            }

            return 0;
        }

        budget = new JournalCaptureBudget(8);
        value = RegistryService.ReadValue(queryKey, "empty", budget, EmptyThenGrowing);
        check(
            value is { Data.Length: 8 } && value.Data.All(item => item == 9) && reads == 2 && budget.Remaining == 0,
            "Success with an increased size after an empty buffer retries instead of inventing zero-filled bytes");

        int Denied(byte[]? bytes, out uint type, ref uint size)
        {
            type = 3;
            return 5;
        }

        var denied = false;
        try
        {
            RegistryService.ReadValue(queryKey, "denied", new JournalCaptureBudget(8), Denied);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            denied = true;
        }

        check(denied, "Budgeted native reads preserve genuine access errors");

        using (var target = new MemoryStream())
        {
            using (var capped = new JournalWriteStream(target, 8))
            {
                capped.Write(new byte[4], 0, 4);
                capped.Write(new byte[4].AsSpan());
                Reject(() => capped.WriteByte(9));
                check(
                    target.Length == 8,
                    "Both write paths permit the exact file cap and reject excess before forwarding");
            }

            target.WriteByte(9);
            check(target.Length == 9, "The capped writer leaves the underlying durable stream open");
        }

        budget = new JournalCaptureBudget(1024);
        var root = RegistryLocation.Parse(@"HKCU\Software\Synthetic");
        var visited = 0;
        Reject(() => RegistryBranchSnapshot.Capture(
            location =>
            {
                visited++;
                return location == root ? new(["A", "B", "C", "D"], []) : new([], []);
            }, root, budget: budget));
        check(visited < 5, "Synthetic branch capture stops before retaining keys beyond its metadata budget");
        if (registry)
        {
            RunRegistry(check);
        }
    }

    private static void RunRegistry(Action<bool, string> check)
    {
        var folder = Directory.CreateTempSubdirectory("Regedit.JournalCapture.");
        var root = RegistryLocation.Parse($@"HKCU\Software\RegeditCapture-{Guid.NewGuid():N}");
        var raw = new RegistryService();
        raw.CreateKey(root.Parent!, root.Name);
        try
        {
            var single = Key("Single");
            var large = new RegistryValue("value", RegistryValueType.Binary, new byte[16 * 1024]);
            raw.Write(single, large, null);
            var journal = Journal("single");
            var ran = false;
            Reject(() => journal.Execute(
                "Delete key", [new JournalScope(single)], () =>
                {
                    ran = true;
                    raw.DeleteKey(single);
                }));
            check(
                !ran && raw.ReadValue(single, "value")!.SameData(large) && Empty("single"),
                "An oversized before-state leaves the registry and history untouched");
            check(
                raw.Read(single).Values.Single().Data.Length == large.Data.Length &&
                RegistryBranchSnapshot.Capture(raw, single).Keys.Single().Values.Single().Data.Length ==
                large.Data.Length,
                "Unjournaled reads and manual snapshots retain their full data without the journal budget");

            journal = Journal("incoming");
            Reject(() => new RegistryService { Journal = journal }.Write(root, large with { Name = "incoming" }, null));
            check(
                raw.ReadValue(root, "incoming") is null && Empty("incoming"),
                "A known oversized incoming value is refused before mutation or history");

            var combined = Key("Combined");
            var combinedBefore = new RegistryValue("value", RegistryValueType.Binary, new byte[4096]);
            raw.Write(combined, combinedBefore, null);
            journal = Journal("combined");
            Reject(() => new RegistryService { Journal = journal }.Write(
                combined, combinedBefore with { Data = Enumerable.Repeat((byte)1, 4096).ToArray() }, combinedBefore));
            check(
                raw.ReadValue(combined, "value")!.SameData(combinedBefore) && Empty("combined"),
                "A known replacement that cannot fit alongside its before-state is refused before mutation or history");

            var aggregate = Key("Aggregate");
            foreach (var name in new[] { "a", "b", "c", "d" })
            {
                raw.Write(aggregate, new RegistryValue(name, RegistryValueType.Binary, new byte[4096]), null);
            }

            journal = Journal("aggregate", 12 * 1024);
            Reject(() => journal.Execute("Delete key", [new JournalScope(aggregate)], () => raw.DeleteKey(aggregate)));
            check(
                raw.Read(aggregate).Values.Length == 4 && Empty("aggregate"),
                "Individually small values share one aggregate before-state budget");

            var first = Key("First");
            var second = Key("Second");
            raw.Write(first, new RegistryValue("value", RegistryValueType.Binary, new byte[4096]), null);
            raw.Write(second, new RegistryValue("value", RegistryValueType.Binary, new byte[4096]), null);
            journal = Journal("scopes");
            ran = false;
            Reject(() => journal.Execute(
                "Edit value", [new JournalScope(first, ["value"]), new JournalScope(second, ["value"])],
                () => ran = true));
            check(!ran && Empty("scopes"), "Multiple before-state scopes share one capture budget");

            var wide = Key("Wide");
            for (var i = 0; i < 20; i++)
            {
                raw.CreateKey(wide, "Child" + i);
            }

            Reject(() => raw.Read(wide, new JournalCaptureBudget(1024)));
            check(
                raw.SubKeys(wide).Length == 20,
                "A wide key is rejected using its minimum child metadata before enumeration");
            var names = Key("Names");
            for (var i = 0; i < 20; i++)
            {
                raw.Write(names, new RegistryValue(new string('n', 200) + i, RegistryValueType.Binary, []), null);
            }

            journal = Journal("names");
            Reject(() => journal.Execute("Delete key", [new JournalScope(names)], () => raw.DeleteKey(names)));
            check(
                raw.Read(names).Values.Length == 20 && Empty("names"),
                "Names and paths consume capture budget even when all value payloads are empty");

            var longNames = Key("LongNames");
            raw.Write(longNames, new RegistryValue(new string('a', 2000), RegistryValueType.Binary, []), null);
            raw.Write(longNames, new RegistryValue(new string('b', 2000), RegistryValueType.Binary, []), null);
            Reject(() => raw.Read(longNames, new JournalCaptureBudget(4096)));
            var ordered = raw.Read(longNames, new JournalCaptureBudget(16 * 1024));
            check(
                ordered.Values.Select(item => item.Name)
                    .SequenceEqual(raw.Read(longNames).Values.Select(item => item.Name)),
                "Incremental native name enumeration bounds long names and preserves ordinary ordering");
            journal = Journal("scope-names");
            ran = false;
            Reject(() => journal.Execute(
                "Edit value", [new JournalScope(root, [new string('n', 4096)])], () => ran = true));
            check(
                !ran && Empty("scope-names"),
                "Supplied scope names are bounded before normalization copies their arrays");

            var secured = Key("Security");
            using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, secured.View))
            using (var key = user.OpenSubKey(
                       secured.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!)
            {
                var security = key.GetAccessControl();
                for (var i = 1000; i < 1100; i++)
                {
                    security.AddAccessRule(
                        new RegistryAccessRule(
                            new SecurityIdentifier("S-1-5-21-1-2-3-" + i), RegistryRights.ReadKey,
                            AccessControlType.Allow));
                }

                key.SetAccessControl(security);
            }

            journal = Journal("security", 4096);
            Reject(() => journal.Execute("Delete key", [new JournalScope(secured)], () => raw.DeleteKey(secured)));
            check(
                raw.Read(secured).Values.Length == 0 && Empty("security"),
                "Security descriptor metadata is bounded before mutation and history");

            FinalFailure("capture-final", 8192, 256 * 1024 * 1024, 16 * 1024, false);
            FinalFailure("failed-mutation", 8192, 256 * 1024 * 1024, 16 * 1024, true);
            FinalFailure("serialized-final", 64 * 1024, 8192, 16 * 1024, false);

            var legacy = Key("LegacyRestore");
            raw.Write(legacy, large, null);
            var legacyFolder = Path.Combine(folder.FullName, "legacy-restore");
            var legacyWriter = new RegistryService { Journal = new RegistryJournal(legacyFolder) };
            legacyWriter.Write(legacy, large with { Data = [1] }, large);
            var legacyFile = Directory.GetFiles(legacyFolder, "*.json").Single();
            var legacyBytes = File.ReadAllBytes(legacyFile);
            journal = Journal("legacy-restore");
            Reject(() => journal.Revert(journal.ReadEntries().Single().Id));
            check(
                raw.ReadValue(legacy, "value")!.Data.SequenceEqual(new byte[] { 1 }) &&
                File.ReadAllBytes(legacyFile).SequenceEqual(legacyBytes) &&
                Directory.GetFiles(legacyFolder, "*.json").Length == 1,
                "Revert refuses a known oversized legacy restoration before mutation or new history");

            var duplicate = Key("DuplicateNames");
            raw.Write(duplicate, new RegistryValue("value", RegistryValueType.Binary, [7]), null);
            journal = Journal("duplicate-names");
            journal.Execute(
                "Edit value", [new JournalScope(duplicate, Enumerable.Repeat("value", 100).ToArray())], () => { });
            check(
                journal.ReadEntries().Single().Before.Single().Scope.ValueNames!.Length == 1,
                "Repeated supplied value names normalize once without charging duplicate targets");

            var afterFirst = Key("AfterFirst");
            var afterSecond = Key("AfterSecond");
            var small = new RegistryValue("value", RegistryValueType.Binary, [1]);
            raw.Write(afterFirst, small, null);
            raw.Write(afterSecond, small, null);
            journal = Journal("after-scopes");
            byte[]? beforeScopes = null;
            Reject(() => journal.Execute(
                "Edit value", [new JournalScope(afterFirst, ["value"]), new JournalScope(afterSecond, ["value"])], () =>
                {
                    beforeScopes = File.ReadAllBytes(
                        Directory.GetFiles(Path.Combine(folder.FullName, "after-scopes"), "*.json").Single());
                    raw.Write(afterFirst, small with { Data = new byte[4096] }, small);
                    raw.Write(afterSecond, small with { Data = new byte[4096] }, small);
                }));
            check(
                journal.ReadEntries().Single().Before.Length == 2 &&
                File.ReadAllBytes(Directory.GetFiles(Path.Combine(folder.FullName, "after-scopes"), "*.json").Single())
                    .SequenceEqual(beforeScopes!) && raw.ReadValue(afterFirst, "value")!.Data.Length == 4096 &&
                raw.ReadValue(afterSecond, "value")!.Data.Length == 4096,
                "All after-state scopes share the same budget already consumed by their before-states");

            var blocked = Key("BlockedReplace");
            raw.Write(blocked, small, null);
            journal = Journal("blocked-replace");
            FileStream? reader = null;
            byte[]? blockedBefore = null;
            try
            {
                Reject(() => journal.Execute(
                    "Edit value", [new JournalScope(blocked, ["value"])], () =>
                    {
                        var path = Directory.GetFiles(Path.Combine(folder.FullName, "blocked-replace"), "*.json")
                            .Single();
                        blockedBefore = File.ReadAllBytes(path);
                        reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                        raw.Write(blocked, small with { Data = [2] }, small);
                    }));
            }
            finally
            {
                reader?.Dispose();
            }

            check(
                File.ReadAllBytes(
                        Directory.GetFiles(Path.Combine(folder.FullName, "blocked-replace"), "*.json").Single())
                    .SequenceEqual(blockedBefore!) && journal.ReadEntries().Single().After is null &&
                Directory.GetFiles(Path.Combine(folder.FullName, "blocked-replace"), "*.tmp").Length == 0,
                "Failed atomic replacement preserves the durable pending file and removes the flushed temporary file");
            journal.RecordActivity("After replacement failure", blocked, "test", () => { });
            check(
                journal.ReadEntries().Length == 2,
                "An existing pending file reopens and the next writer runs after replacement failure");

            var initial = Key("InitialFile");
            journal = Journal("initial-file", 8192, 1024);
            ran = false;
            Reject(() => journal.Execute(new string('x', 4096), [new JournalScope(initial)], () => ran = true));
            check(
                !ran && Empty("initial-file"),
                "An oversized initial serialized record invokes no mutation and leaks no temporary file");

            var revert = Key("Revert");
            var original = new RegistryValue(
                "value", RegistryValueType.Binary, Enumerable.Repeat((byte)1, 2048).ToArray());
            var replacement = original with { Data = Enumerable.Repeat((byte)2, 2048).ToArray() };
            raw.Write(revert, original, null);
            journal = Journal("revert");
            new RegistryService { Journal = journal }.Write(revert, replacement, original);
            var edit = journal.ReadEntries().Single();
            journal.Revert(edit.Id);
            check(
                raw.ReadValue(revert, "value")!.SameData(original) && journal.ReadEntries().Length == 2,
                "Revert reuses its captured before-state budget and bounds temporary conflict captures separately");
            var existing = Directory.GetFiles(Path.Combine(folder.FullName, "revert"), "*.json")
                .ToDictionary(file => file, File.ReadAllBytes);
            var tiny = Journal("revert", 64);
            check(
                tiny.ReadEntries().Length == 2,
                "Small new capture limits do not restrict loading compatible historical entries");
            Reject(() => tiny.Revert(journal.ReadEntries().Last().Id));
            check(
                existing.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) &&
                Directory.GetFiles(Path.Combine(folder.FullName, "revert"), "*.json").Length == 2,
                "An oversized Revert preserves existing historical files byte for byte");

            RegistryLocation Key(string name)
            {
                raw.CreateKey(root, name);
                return root.Child(name);
            }

            RegistryJournal Journal(string name, long capture = 8192, long file = 256 * 1024 * 1024)
            {
                return new(Path.Combine(folder.FullName, name), false, capture, file);
            }

            bool Empty(string name)
            {
                return !Directory.Exists(Path.Combine(folder.FullName, name)) ||
                       Directory.GetFiles(Path.Combine(folder.FullName, name), "*.json").Length == 0 &&
                       Directory.GetFiles(Path.Combine(folder.FullName, name), "*.tmp").Length == 0;
            }

            void FinalFailure(string name, long capture, long file, int bytes, bool failMutation)
            {
                var target = Key(name);
                var before = new RegistryValue("value", (RegistryValueType)99, [0, 255, 7]);
                raw.Write(target, before, null);
                var writer = Journal(name, capture, file);
                byte[]? pending = null;
                var failed = Reject(() => writer.Execute(
                    "Edit value", [new JournalScope(target)], () =>
                    {
                        pending = File.ReadAllBytes(
                            Directory.GetFiles(Path.Combine(folder.FullName, name), "*.json").Single());
                        raw.Write(target, before with { Data = new byte[bytes] }, before);
                        if (failMutation)
                        {
                            throw new InvalidOperationException("mutation fixture");
                        }
                    }));
                var files = Directory.GetFiles(Path.Combine(folder.FullName, name), "*.json");
                var saved = new RegistryJournal(Path.Combine(folder.FullName, name)).ReadEntries().Single();
                check(
                    pending is not null && File.ReadAllBytes(files.Single()).SequenceEqual(pending) &&
                    saved.After is null &&
                    saved.Before.Single().Snapshot.Keys.Single().Values.Single().SameData(before) &&
                    raw.ReadValue(target, "value")!.Data.Length == bytes &&
                    saved.RevertUnavailableReason(new HashSet<Guid>()) is not null &&
                    failed.Message.Contains("before-state is retained") &&
                    Directory.GetFiles(Path.Combine(folder.FullName, name), "*.tmp").Length == 0,
                    "Failed final capture or serialization retains exact reloadable pending bytes: " + name);
                if (failMutation)
                {
                    check(
                        failed.InnerException is AggregateException errors &&
                        errors.InnerExceptions.Any(error => error.Message == "mutation fixture"),
                        "Final capture failure preserves the original mutation failure");
                }

                writer.RecordActivity("After failure", target, "test", () => { });
                check(
                    writer.ReadEntries().Length == 2,
                    "Capture/persistence failure releases all files for the next writer: " + name);
            }
        }
        finally
        {
            raw.DeleteKey(root);
            folder.Delete(true);
        }
    }

    private static IOException Reject(Action action)
    {
        try
        {
            action();
        }
        catch (IOException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The journal limit did not reject the operation.");
    }
}
