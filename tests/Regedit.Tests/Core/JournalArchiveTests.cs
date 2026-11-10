// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalArchiveTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registry)
    {
        var folder = Directory.CreateTempSubdirectory("Regedit.JournalArchive.");
        try
        {
            var first = JournalHistoryTests.Entry("Original", 0) with { Kind = JournalOperation.CreateValue };
            var later = JournalHistoryTests.Entry("Later", 0) with
            {
                Time = first.Time.AddSeconds(1),
                Before = [JournalHistoryTests.State(1, 0)],
                After = [JournalHistoryTests.State(2, 0)]
            };
            var unknown = later with
            {
                Id = Guid.NewGuid(),
                Time = first.Time.AddSeconds(2),
                Kind = (JournalOperation)999,
                Operation = "Future operation",
                Before = later.After
            };
            var revert = later with
            {
                Id = Guid.NewGuid(),
                Time = first.Time.AddSeconds(3),
                Kind = JournalOperation.Revert,
                Operation = "Revert later",
                Reverts = later.Id,
                Before = later.After!,
                After = later.Before
            };
            var legacy = JournalHistoryTests.Entry("Create value", 2) with
            {
                Time = first.Time.AddSeconds(4), Kind = null
            };
            var unnamed = JournalHistoryTests.Entry("Unnamed", 3);
            unnamed = unnamed with
            {
                Before = unnamed.Before.Select(Unnamed).ToArray(), After = unnamed.After!.Select(Unnamed).ToArray()
            };
            foreach (var entry in new[] { first, later, unknown, revert, legacy, unnamed })
            {
                Write(folder.FullName, entry);
            }

            var saved = Directory.GetFiles(folder.FullName, "*.json").ToDictionary(
                path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
            var journal = new RegistryJournal(folder.FullName);
            var model = new JournalViewModel(journal, NullLogger<JournalViewModel>.Instance);
            var secondWindow = new JournalViewModel(
                new RegistryJournal(folder.FullName), NullLogger<JournalViewModel>.Instance);
            check(!model.ArchiveAllCommand.CanExecute(null), "Archive all is disabled for empty history");
            await model.RefreshAsync();
            await secondWindow.RefreshAsync();
            var panel = new JournalSurfaceViewModel(model, () => first.Before[0].Scope.Location);
            var tab = new JournalSurfaceViewModel(model, () => first.Before[0].Scope.Location);
            panel.Activate();
            tab.Activate();
            try
            {
                panel.Query = ToolPresentation.JournalTarget(first);
                check(
                    panel.VisibleEntries.Any(entry => entry.Id == first.Id),
                    "Journal search preserves the exact joined visible target");
                panel.Query = ToolPresentation.JournalTarget(unnamed);
                check(
                    panel.VisibleEntries.Single().Id == unnamed.Id,
                    "Journal search preserves the joined unnamed-value target");
                panel.Query = "Completed · Reverted";
                check(
                    panel.VisibleEntries.Single().Id == later.Id, "Journal search preserves the full displayed status");
                panel.Query = string.Empty;
                panel.OperationFilter
                    = panel.OperationChoices.Single(choice => choice.Operation == JournalOperation.Unknown);
                check(
                    panel.VisibleEntries.Single().Id == unknown.Id,
                    "Typed operation filtering groups unknown future identifiers without depending on their labels");
                panel.OperationFilter
                    = panel.OperationChoices.Single(choice => choice.Operation == JournalOperation.CreateValue);
                check(
                    panel.VisibleEntries.Any(entry => entry.Id == legacy.Id) && new InvestigationRow(
                            "Changed label", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                            legacy)
                        .Glyph == ToolPresentation.JournalGlyph(JournalOperation.CreateValue),
                    "Legacy operations retain typed filtering and icons independently from display labels");

                tab.SelectedEntry = unknown;
                await tab.ToggleArchiveCommand.ExecuteAsync(null);
                check(
                    journal.ReadArchiveState().ArchivedIds.Contains(unknown.Id) &&
                    new RegistryJournal(folder.FullName).ReadArchiveState().ArchivedIds.Contains(unknown.Id) &&
                    new FileInfo(Path.Combine(folder.FullName, unknown.Id.ToString("N") + ".archive")).Length == 1,
                    "Archive commands persist one-byte per-entry metadata across reopening");
                check(
                    !secondWindow.ArchivedIds.Contains(unknown.Id),
                    "Another window keeps its current presentation until refresh");
                await secondWindow.RefreshAsync();
                check(secondWindow.ArchivedIds.Contains(unknown.Id), "The next window refresh observes archived state");
                panel.OperationFilter = panel.OperationChoices[0];
                check(
                    !panel.VisibleEntries.Any(entry => entry.Id == unknown.Id),
                    "Archived entries are hidden by default");
                tab.ArchiveFilter = tab.ArchiveChoices.Single(choice => choice.View == JournalArchiveView.Archived);
                check(
                    tab.VisibleEntries.Single().Id == unknown.Id && panel.VisibleEntries.Count() == 5,
                    "Panel and tab retain independent archive filters");

                panel.SelectedEntry = first;
                await model.SetSelectionAsync(panel, first);
                var blocker = panel.Links.Single(link => link.Target.Id == unknown.Id);
                panel.Query = "Original";
                panel.OperationFilter
                    = panel.OperationChoices.Single(choice => choice.Operation == JournalOperation.CreateValue);
                JournalEntry? requested = null;
                panel.EntryRequested += entry =>
                {
                    requested = entry;
                    panel.SelectedEntry = entry;
                };
                blocker.Command.Execute(blocker.EntryId);
                check(
                    requested?.Id == unknown.Id && panel.VisibleEntries.Any(entry => entry.Id == unknown.Id) &&
                    panel.ArchiveFilter!.View == JournalArchiveView.All && panel.OperationFilter!.Operation is null &&
                    tab.ArchiveFilter!.View == JournalArchiveView.Archived,
                    "An explicit blocker link reveals its archived filtered target in only the requesting surface");
                var refused = false;
                try
                {
                    journal.Revert(first.Id);
                }
                catch (IOException ex) when (ex.Message.Contains("later journal entry"))
                {
                    refused = true;
                }

                check(
                    refused && model.Entries.Length == saved.Count,
                    "Archiving an unknown blocking edit never changes the strict Revert graph");

                panel.SelectedEntry = later;
                tab.SelectedEntry = revert;
                check(
                    panel.Links.Any(link => link.Target.Id == revert.Id && link.Label.StartsWith("Revert edit")) &&
                    tab.Links.Any(link => link.Target.Id == later.Id && link.Label.StartsWith("Original edit")),
                    "Original edits and their Revert entries expose links in both directions");
                panel.SelectedEntry = unknown;
                await panel.ToggleArchiveCommand.ExecuteAsync(null);
                check(
                    !journal.ReadArchiveState().ArchivedIds.Contains(unknown.Id) &&
                    panel.ArchiveAction == "Archive edit",
                    "Unarchive durably restores an entry without deleting its history");
                check(
                    saved.All(pair =>
                        File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value.Bytes) &&
                        File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Written) &&
                    Directory.GetFiles(folder.FullName, "*.tmp").Length == 0,
                    "Archive and unarchive never rewrite journal JSON or leak temporary files");
                panel.SelectedEntry = null;
                check(!panel.ToggleArchiveCommand.CanExecute(null), "Archive requires one inspected entry");

                var marker = Path.Combine(folder.FullName, unknown.Id.ToString("N") + ".archive");
                File.WriteAllBytes(marker, [2]);
                await model.RefreshAsync();
                check(
                    model.Error is null && model.ArchiveError is not null && model.Entries.Length == saved.Count &&
                    panel.VisibleEntries.Any(entry => entry.Id == unknown.Id),
                    "Malformed archive display metadata leaves readable history visible and separate from integrity errors");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    var canceled = false;
                    try
                    {
                        journal.SetArchived(first.Id, true, cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                    }

                    check(
                        canceled && !File.Exists(Path.Combine(folder.FullName, first.Id.ToString("N") + ".archive")),
                        "Canceled archive writes leave presentation metadata and journal history untouched");
                }

                panel.Query = "No matching edit";
                panel.ScopeIndex = 1;
                panel.OperationFilter
                    = panel.OperationChoices.Single(choice => choice.Operation == JournalOperation.Unknown);
                check(model.ArchiveAllCommand.CanExecute(null), "Archive all requires no selection or matching rows");
                await model.ArchiveAllCommand.ExecuteAsync(null);
                check(
                    model.ArchivedIds.Count == saved.Count &&
                    new RegistryJournal(folder.FullName).ReadArchiveState().ArchivedIds.Count == saved.Count &&
                    model.ArchiveError is null && !model.ArchiveAllCommand.CanExecute(null),
                    "Archive all persists every entry regardless of filters and repairs unreadable archive markers");
                panel.Query = string.Empty;
                panel.ScopeIndex = 0;
                panel.OperationFilter = panel.OperationChoices[0];
                panel.ArchiveFilter = panel.ArchiveChoices[0];
                check(
                    !panel.VisibleEntries.Any() && tab.VisibleEntries.Count() == saved.Count,
                    "Archive all clears active history while the archived surface retains every entry");
                check(
                    saved.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value.Bytes) &&
                                      File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Written),
                    "Archive all preserves every journal record and its timestamp");

                var next = first with { Id = Guid.NewGuid(), Time = first.Time.AddDays(1) };
                Write(folder.FullName, next);
                await model.RefreshAsync();
                check(
                    panel.VisibleEntries.Single().Id == next.Id && model.ArchiveAllCommand.CanExecute(null),
                    "New edits remain active after archiving and re-enable Archive all");
                await model.ArchiveAllCommand.ExecuteAsync(null);

                journal.SetArchived(first.Id, false);
                journal.SetArchived(later.Id, false);
                var blockedMarker = Path.Combine(folder.FullName, later.Id.ToString("N") + ".archive");
                File.Delete(blockedMarker);
                Directory.CreateDirectory(blockedMarker);
                await model.RefreshAsync();
                string? failure = null;
                model.Notice += (title, _, kind) =>
                {
                    if (kind == NoticeKind.Error)
                    {
                        failure = title;
                    }
                };
                await model.ArchiveAllCommand.ExecuteAsync(null);
                check(
                    failure == "Could not archive all edits" && model.ArchivedIds.Contains(first.Id) &&
                    !model.ArchivedIds.Contains(later.Id) && panel.VisibleEntries.Single().Id == later.Id &&
                    model.ArchiveAllCommand.CanExecute(null),
                    "A partial archive failure reports the error and refreshes completed writes for retry");
            }
            finally
            {
                panel.Deactivate();
                tab.Deactivate();
            }

            foreach (var barrier in new[]
                     {
                         unknown, later with { After = null },
                         later with
                         {
                             Kind = JournalOperation.Activity,
                             Activity = "External",
                             Before = [],
                             After = [],
                             ActivityLocation = first.Before[0].Scope.Location
                         }
                     })
            {
                var barrierFolder = Path.Combine(folder.FullName, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(barrierFolder);
                Write(barrierFolder, first);
                Write(barrierFolder, barrier);
                var barriers = new RegistryJournal(barrierFolder);
                barriers.SetArchived(barrier.Id, true);
                var rejected = false;
                try
                {
                    barriers.Revert(first.Id);
                }
                catch (IOException ex) when (ex.Message.Contains("later journal entry"))
                {
                    rejected = true;
                }

                check(
                    rejected && barriers.ReadEntries().Length == 2,
                    "Archived unknown incomplete and external barriers remain authoritative: " + barrier.OperationKind);
                File.WriteAllText(Path.Combine(barrierFolder, "damaged.json"), "{");
                rejected = false;
                try
                {
                    barriers.Revert(first.Id);
                }
                catch (JsonException)
                {
                    rejected = true;
                }

                check(
                    rejected && barriers.ReadHistory().DamagedFiles.Length == 1,
                    "Archive markers cannot hide damaged JSON from strict Revert");
            }

            if (registry)
            {
                RunRegistry(Path.Combine(folder.FullName, "registry"), check);
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }

    private static void RunRegistry(string folder, Action<bool, string> check)
    {
        var root = RegistryLocation.Parse($@"HKCU\Software\RegeditArchive-{Guid.NewGuid():N}");
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
            journal.SetArchived(entries[1].Id, true);
            journal.SetArchived(entries[2].Id, true);
            var refused = false;
            try
            {
                journal.Revert(entries[0].Id);
            }
            catch (IOException ex) when (ex.Message.Contains("later journal entry"))
            {
                refused = true;
            }

            check(
                refused && raw.ReadValue(root, "value")!.SameData(one) && journal.ReadEntries().Length == 3,
                "Actual archived later writes still protect a registry value restored to its earlier after-state");
            writer.Write(root, one with { Name = "independent" }, null);
            var independent = journal.ReadEntries().Last();
            journal.SetArchived(independent.Id, true);
            journal.Revert(independent.Id);
            check(
                raw.ReadValue(root, "independent") is null &&
                journal.ReadArchiveState().ArchivedIds.Contains(independent.Id) &&
                journal.ReadEntries().Last().Reverts == independent.Id,
                "Archiving is reversible presentation and does not prevent an otherwise safe explicit Revert");
        }
        finally
        {
            raw.DeleteKey(root);
        }
    }

    private static JournalState Unnamed(JournalState state)
    {
        return state with
        {
            Scope = state.Scope with { ValueNames = [string.Empty] },
            Snapshot = state.Snapshot with
            {
                Keys =
                [
                    new SnapshotKey(string.Empty, [state.Snapshot.Keys[0].Values[0] with { Name = string.Empty }])
                ]
            }
        };
    }

    private static void Write(string folder, JournalEntry entry)
    {
        File.WriteAllBytes(
            Path.Combine(folder, entry.Id.ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(entry, JournalJsonContext.Default.JournalEntry));
    }
}
