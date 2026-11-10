// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalHistoryTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registry)
    {
        check(
            Marshal.SizeOf<JournalHistoryReader.FileBasicInfo>() == 40 &&
            Marshal.SizeOf<JournalHistoryReader.FileIdInfo>() == 24,
            "Journal file generations use the Windows SDK's complete basic and 128-bit identity layouts");
        var folder = Directory.CreateTempSubdirectory("Regedit.JournalHistory.");
        try
        {
            var seed = Entry("Seed000", 0);
            var path = Path.Combine(folder.FullName, seed.Id.ToString("N") + ".json");
            Write(path, seed);
            var journal = new RegistryJournal(folder.FullName);
            var reader = new JournalHistoryReader(journal);
            var first = reader.ReadHistory().Entries.Single();
            JournalHistoryReader.FileGeneration? generation;
            using (var stream = File.OpenRead(path))
            {
                generation = JournalHistoryReader.ReadGeneration(stream);
            }

            var warm = reader.ReadHistory().Entries.Single();
            check(
                generation is null ? !ReferenceEquals(first, warm) : ReferenceEquals(first, warm),
                "Display reads reuse supported handle generations and otherwise parse again");
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                check(exclusive.Length > 0, "Presentation caching retains no open file between refreshes");
            }

            var strict = journal.ReadEntries().Single();
            strict.After![0].Snapshot.Keys[0].Values[0].Data[0] = 99;
            check(
                journal.ReadHistory().Entries.Single().After![0].Snapshot.Keys[0].Values[0].Data[0] == 1 &&
                reader.ReadHistory().Entries.Single().After![0].Snapshot.Keys[0].Values[0].Data[0] == 1,
                "Public reader mutations cannot contaminate fresh history or the presentation cache");

            using (var writer = await HistoryWriter.StartAsync(folder.FullName, path))
            {
                var length = new FileInfo(path).Length;
                var written = File.GetLastWriteTimeUtc(path);
                await writer.CommandAsync("in-place");
                var changed = reader.ReadHistory().Entries.Single();
                check(
                    changed.Operation == "Seed111" && !ReferenceEquals(changed, warm) &&
                    new FileInfo(path).Length == length && File.GetLastWriteTimeUtc(path) == written,
                    "A different process invalidates same-size in-place edits even when last-write time is restored");
                await writer.CommandAsync("replace");
                var replaced = reader.ReadHistory().Entries.Single();
                check(
                    replaced.Operation == "Seed222" && !ReferenceEquals(changed, replaced) &&
                    new FileInfo(path).Length == length && File.GetLastWriteTimeUtc(path) == written,
                    "A different process invalidates same-size atomic replacement with the original last-write time");
                await writer.CommandAsync("add");
                check(
                    reader.ReadHistory().Entries.Length == 2,
                    "Display history discovers a second instance's new records");
                await writer.CommandAsync("remove-added");
                check(
                    reader.ReadHistory().Entries.Length == 1 && reader.CachedFileCount <= 1,
                    "Display history evicts files removed by another instance");
                await writer.CommandAsync("corrupt");
                var damaged = reader.ReadHistory();
                check(
                    damaged.Entries.Length == 0 && damaged.DamagedFiles.SequenceEqual([Path.GetFileName(path)]) &&
                    reader.CachedFileCount == 0, "A formerly cached record cannot hide another process's corruption");
                await writer.CommandAsync("repair");
                check(
                    reader.ReadHistory().Entries.Single().Operation == "Seed000",
                    "Repaired history is parsed and displayed again");
                await writer.CommandAsync("duplicate");
                damaged = reader.ReadHistory();
                check(
                    damaged.Entries.Length == 1 && damaged.DamagedFiles.Length == 1 && reader.CachedFileCount <= 1,
                    "Cache reuse preserves the shared reader's duplicate-ID integrity check");
                await writer.CommandAsync("remove-duplicate");
                await writer.CommandAsync("remove");
                check(
                    reader.ReadHistory().Entries.Length == 0 && reader.CachedFileCount == 0,
                    "Presentation storage remains bounded to current validated files");
                await writer.CommandAsync("repair");
            }

            CheckDifferences(seed, check);
            CheckConflicts(check);
            CheckDescriptorValidation(Path.Combine(folder.FullName, "descriptors"), check);
            await CheckEligibilityAsync(Path.Combine(folder.FullName, "eligibility"), check);
            Measure(Path.Combine(folder.FullName, "measure"), check);
            if (registry)
            {
                await CheckStrictRevertAsync(Path.Combine(folder.FullName, "strict"), check);
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }

    private static void CheckDifferences(JournalEntry entry, Action<bool, string> check)
    {
        var cache = new JournalDifferenceCache();
        var other = Entry("Other", 1);
        cache.SetEntries([entry, other]);
        var changes = cache.Get(entry);
        check(
            ReferenceEquals(changes, cache.Get(entry)) && changes.Count == 1,
            "Inspector and script consumers share one selected entry's computed differences");
        cache.Get(other);
        cache.SetEntries([entry]);
        cache.Get(other);
        check(cache.Count == 1, "Unselected entries cannot keep their computed differences alive");
        cache.SetEntries([]);
        cache.SetEntries([entry]);
        check(
            !ReferenceEquals(changes, cache.Get(entry)),
            "Returning to a released inspection computes a new difference list");
        var updated = entry with { After = [State(2, 0)] };
        cache.SetEntries([updated]);
        check(
            cache.Get(updated).Single().After!.Data[0] == 2 && cache.Count == 1,
            "An updated record with the same ID replaces its selected difference cache");

        var descriptors = Enumerable.Range(0, 2000)
            .Select(index => new JournalSecurity("Path" + index, "O:SYG:SYD:(A;;KA;;;SY)")).ToArray();
        var before = State(0, 0) with { Security = descriptors };
        var after = before with
        {
            Security = descriptors.Reverse().Select(item => item with { Path = item.Path.ToUpperInvariant() })
                .ToArray()
        };
        check(
            RegistryJournal.Same(before, after),
            "Linear descriptor matching preserves case-insensitive paths and arbitrary ordering");
        after.Security[0] = after.Security[0] with { Descriptor = "O:SYG:SYD:(A;;KR;;;SY)" };
        check(!RegistryJournal.Same(before, after), "Descriptor matching preserves exact ACL string semantics");
        var duplicate = before with
        {
            Security = [descriptors[0], descriptors[0] with { Path = descriptors[0].Path.ToUpperInvariant() }]
        };
        var distinct = before with { Security = [descriptors[0], descriptors[1]] };
        check(
            !RegistryJournal.Same(duplicate, distinct),
            "Duplicate descriptor paths cannot satisfy a different child's access rules");
    }

    private static void CheckDescriptorValidation(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var entry = Entry("Edit value", 0);
        var descriptor = new JournalSecurity("Child", "O:SYG:SYD:(A;;KA;;;SY)");
        var invalid = entry with
        {
            Id = Guid.NewGuid(),
            Before = [entry.Before[0] with { Security = [descriptor, descriptor with { Path = "CHILD" }] }]
        };
        var file = Path.Combine(folder, invalid.Id.ToString("N") + ".json");
        Write(file, invalid);
        Write(Path.Combine(folder, entry.Id.ToString("N") + ".json"), entry with { Kind = null });
        var history = new RegistryJournal(folder).ReadHistory();
        check(
            history.DamagedFiles.SequenceEqual([Path.GetFileName(file)]) &&
            history.Entries.Single().Before[0].Security.Length == 0,
            "Journal validation rejects case-insensitive duplicate descriptor paths and accepts descriptor-free legacy states");
    }

    private static void CheckConflicts(Action<bool, string> check)
    {
        var first = Entry("Original", 0);
        var unrelated = Entry("Unrelated", 1);
        var noChange = Entry("No change", 0) with
        {
            Time = first.Time.AddSeconds(2), Before = [State(1, 0)], After = [State(1, 0)]
        };
        var later = Entry("Blocking edit", 0) with
        {
            Time = first.Time.AddSeconds(3), Before = [State(1, 0)], After = [State(2, 0)]
        };
        JournalEntry[] entries = [first, unrelated, noChange, later];
        var conflict = RegistryJournal.FindLaterConflict(entries, first, JournalEntry.GetRevertedIds(entries));
        check(
            conflict?.Entry.Id == later.Id && conflict.Reason.Contains(later.Operation) &&
            conflict.Reason.Contains(later.Path) && conflict.Reason.Contains(later.Time.ToLocalTime().ToString("G")),
            "The first actual blocking later edit supplies its operation target and time");
        var revert = Entry("Revert", 0) with
        {
            Time = first.Time.AddSeconds(4),
            Kind = JournalOperation.Revert,
            Reverts = later.Id,
            Before = [State(2, 0)],
            After = [State(1, 0)]
        };
        entries = [first, unrelated, noChange, later, revert];
        check(
            RegistryJournal.FindLaterConflict(entries, first, JournalEntry.GetRevertedIds(entries)) is null,
            "An eligible later Revert and its reverted edit do not become false blockers");
        foreach (var barrier in new[]
                 {
                     noChange with { Kind = JournalOperation.Unknown }, noChange with { After = null },
                     noChange with
                     {
                         Kind = JournalOperation.Activity,
                         Activity = "External",
                         ActivityLocation = first.Before[0].Scope.Location,
                         Before = [],
                         After = []
                     }
                 })
        {
            entries = [first, unrelated, barrier];
            check(
                RegistryJournal.FindLaterConflict(entries, first, JournalEntry.GetRevertedIds(entries))?.Entry.Id ==
                barrier.Id,
                "Unknown incomplete and external activity barriers remain blocking: " + barrier.OperationKind);
        }
    }

    private static async Task CheckEligibilityAsync(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var original = Entry("Selected", 0);
        Write(Path.Combine(folder, original.Id.ToString("N") + ".json"), original);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var model = new JournalViewModel(
            new RegistryJournal(folder), NullLogger<JournalViewModel>.Instance, (entries, entry, reverted) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(15)))
                    {
                        throw new TimeoutException("Eligibility fixture was not released.");
                    }
                }

                return RegistryJournal.FindLaterConflict(entries, entry, reverted);
            });
        await model.RefreshAsync();
        var panel = new object();
        var tab = new object();
        var oldCheck = model.SetSelectionAsync(panel, original);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Eligibility fixture did not start.");
            }

            var hint = await Task.Run(() => model.RevertUnavailableReason(original)).WaitAsync(TimeSpan.FromSeconds(2));
            check(
                !oldCheck.IsCompleted && hint!.Contains("Checking") && !model.RevertCommand.CanExecute(original),
                "Selection and CanExecute remain available while large eligibility work is blocked on its worker");
            var later = Entry("New blocker", 0) with
            {
                Time = original.Time.AddSeconds(1), Before = [State(1, 0)], After = [State(2, 0)]
            };
            Write(Path.Combine(folder, later.Id.ToString("N") + ".json"), later);
            await model.RefreshAsync();
            await model.SetSelectionAsync(panel, original);
            await model.SetSelectionAsync(tab, later);
            check(
                model.BlockingEdit(original)?.Entry.Id == later.Id && !model.RevertCommand.CanExecute(original) &&
                model.RevertCommand.CanExecute(later),
                "Panel and tab selections keep independent current-history eligibility results");
            var current = model.Entries.Single(entry => entry.Id == original.Id);
            var completedCalls = calls;
            await model.RefreshAsync();
            check(
                !ReferenceEquals(current, model.Entries.Single(entry => entry.Id == original.Id)) ||
                calls == completedCalls,
                "Unchanged presentation generations preserve memoized selected-entry eligibility");
            release.Set();
            await oldCheck.WaitAsync(TimeSpan.FromSeconds(5));
            await model.SetSelectionAsync(panel, original);
            check(
                model.BlockingEdit(original)?.Entry.Id == later.Id && !model.RevertCommand.CanExecute(original),
                "An old successful eligibility computation cannot publish into newer blocking history");
        }
        finally
        {
            release.Set();
            await oldCheck.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Measure(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        for (var index = 0; index < 1000; index++)
        {
            var entry = Entry("Measure", index);
            Write(Path.Combine(folder, entry.Id.ToString("N") + ".json"), entry);
        }

        var reader = new JournalHistoryReader(new RegistryJournal(folder));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var cold = reader.ReadHistory();
        var coldTime = watch.Elapsed;
        var coldBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        var warm = reader.ReadHistory();
        var warmTime = watch.Elapsed;
        var warmBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(
            cold.Entries.Length == 1000 && warm.Entries.Length == 1000 && warm.DamagedFiles.Length == 0 &&
            reader.CachedFileCount <= 1000, "Cold and warm presentation reads preserve all 1000 fixture records");
        Console.WriteLine(
            $"MEASURE journal-history 1000 files: cold {coldTime.TotalMilliseconds:F1} ms / {coldBytes:N0} bytes; " +
            $"warm {warmTime.TotalMilliseconds:F1} ms / {warmBytes:N0} bytes; cached {reader.CachedFileCount}.");
        var before = State(0, 0);
        var after = State(1, 0);
        var beforeValues = Enumerable.Range(0, 1000)
            .Select(index => new RegistryValue("value" + index, RegistryValueType.Binary, [0])).ToArray();
        var afterValues = beforeValues.Select(value => value with { Data = [1] }).ToArray();
        var scope = before.Scope with { ValueNames = beforeValues.Select(value => value.Name).ToArray() };
        before = before with
        {
            Scope = scope, Snapshot = before.Snapshot with { Keys = [new SnapshotKey(string.Empty, beforeValues)] }
        };
        after = after with
        {
            Scope = scope, Snapshot = after.Snapshot with { Keys = [new SnapshotKey(string.Empty, afterValues)] }
        };
        var selected = Entry("Differences", 0) with { Before = [before], After = [after] };
        var differences = new JournalDifferenceCache();
        differences.SetEntries([selected]);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        var computed = differences.Get(selected);
        coldTime = watch.Elapsed;
        coldBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        var reused = differences.Get(selected);
        warmTime = watch.Elapsed;
        warmBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(
            computed.Count == 1000 && ReferenceEquals(computed, reused),
            "Cold and warm selected inspection retain all 1000 differences without recomputing");
        Console.WriteLine(
            $"MEASURE journal differences 1000 changes: cold {coldTime.TotalMilliseconds:F1} ms / {coldBytes:N0} bytes; " +
            $"warm {warmTime.TotalMilliseconds:F1} ms / {warmBytes:N0} bytes.");
    }

    private static async Task CheckStrictRevertAsync(string folder, Action<bool, string> check)
    {
        var root = RegistryLocation.Parse($@"HKCU\Software\RegeditHistory-{Guid.NewGuid():N}");
        var raw = new RegistryService();
        raw.CreateKey(root.Parent!, root.Name);
        try
        {
            var journal = new RegistryJournal(folder);
            var writer = new RegistryService { Journal = journal };
            var one = new RegistryValue("value", RegistryValueType.Binary, [1]);
            var two = one with { Data = [2] };
            writer.Write(root, one, null);
            writer.Write(root, two, one);
            writer.Write(root, one, two);
            var entries = journal.ReadEntries();
            var original = entries[0];
            var model = new JournalViewModel(journal, NullLogger<JournalViewModel>.Instance);
            await model.RefreshAsync();
            await model.SetSelectionAsync(model, original);
            check(
                model.BlockingEdit(original)?.Entry.Id == entries[1].Id && !model.RevertCommand.CanExecute(original),
                "Display eligibility identifies the same first blocking edit as strict Revert");
            foreach (var read in new[]
                     {
                         entries, journal.ReadHistory().Entries,
                         new JournalHistoryReader(journal).ReadHistory().Entries
                     })
            {
                foreach (var later in read.Skip(1))
                {
                    later.Before[0] = later.After![0];
                }

                var rejected = false;
                try
                {
                    journal.Revert(original.Id);
                }
                catch (IOException ex) when (ex.Message.Contains("later journal entry"))
                {
                    rejected = true;
                }

                check(
                    rejected && raw.ReadValue(root, "value")!.SameData(one) && journal.ReadEntries().Length == 3,
                    "Mutating returned history arrays cannot fool fresh strict Revert or create pending history");
            }
        }
        finally
        {
            raw.DeleteKey(root);
        }
    }

    internal static JournalEntry Entry(string operation, int index)
    {
        return new JournalEntry(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(index), operation, [State(0, index)], [State(1, index)],
            Kind: JournalOperation.EditValue);
    }

    internal static JournalState State(byte value, int index)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\JournalHistoryFixture");
        var scope = new JournalScope(root, ["value" + index]) { KeyPaths = [@"\REGISTRY\USER\JournalHistoryFixture"] };
        return new JournalState(
            scope, new RegistryBranchSnapshot(
                1, root, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [
                    new SnapshotKey(
                        string.Empty, [new RegistryValue("value" + index, RegistryValueType.Binary, [value])])
                ], Environment.MachineName), []);
    }

    private static void Write(string file, JournalEntry entry)
    {
        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(entry, JournalJsonContext.Default.JournalEntry));
    }

    internal static void RunWriter(string folder, string path)
    {
        var original = File.ReadAllBytes(path);
        Console.WriteLine("ready");
        while (Console.ReadLine() is { } command)
        {
            try
            {
                switch (command)
                {
                    case "in-place": Change("Seed111", false); break;
                    case "replace": Change("Seed222", true); break;
                    case "add":
                        new RegistryJournal(folder).RecordActivity(
                            "Added", RegistryLocation.Parse("HKCU"), "writer fixture", () => { }); break;
                    case "remove-added":
                        var added = new RegistryJournal(folder).ReadEntries()
                            .Single(entry => entry.Operation == "Added");
                        File.Delete(Path.Combine(folder, added.Id.ToString("N") + ".json"));
                        break;
                    case "corrupt": File.WriteAllText(path, "{"); break;
                    case "repair": File.WriteAllBytes(path, original); break;
                    case "duplicate": File.Copy(path, Path.Combine(folder, "duplicate.json")); break;
                    case "remove-duplicate": File.Delete(Path.Combine(folder, "duplicate.json")); break;
                    case "remove": File.Delete(path); break;
                    default: throw new InvalidOperationException("Unknown journal fixture command.");
                }

                Console.WriteLine("ok");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Console.WriteLine("failed");
                Environment.ExitCode = 1;
                return;
            }
        }

        void Change(string operation, bool replace)
        {
            var written = File.GetLastWriteTimeUtc(path);
            var entry = JsonSerializer.Deserialize(File.ReadAllBytes(path), JournalJsonContext.Default.JournalEntry)!;
            var target = replace ? path + ".writer.tmp" : path;
            Write(target, entry with { Operation = operation });
            File.SetLastWriteTimeUtc(target, written);
            if (replace)
            {
                File.Move(target, path, true);
            }
        }
    }

    private sealed class HistoryWriter : IDisposable
    {
        private readonly Process _process;

        private HistoryWriter(Process process)
        {
            this._process = process;
        }

        internal static async Task<HistoryWriter> StartAsync(string folder, string path)
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
                start.ArgumentList.Add(typeof(JournalHistoryTests).Assembly.Location);
            }

            foreach (var argument in new[] { "--journal-history-writer", folder, path })
            {
                start.ArgumentList.Add(argument);
            }

            var writer = new HistoryWriter(Process.Start(start)!);
            try
            {
                if (await writer._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "ready")
                {
                    throw new IOException("Journal history writer did not start.");
                }

                return writer;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        internal async Task CommandAsync(string command)
        {
            await this._process.StandardInput.WriteLineAsync(command);
            await this._process.StandardInput.FlushAsync();
            if (await this._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "ok")
            {
                throw new IOException(
                    "Journal fixture writer failed: " + await this._process.StandardError.ReadToEndAsync());
            }
        }

        public void Dispose()
        {
            if (!this._process.HasExited)
            {
                this._process.Kill(true);
                this._process.WaitForExit();
            }

            this._process.Dispose();
        }
    }
}
