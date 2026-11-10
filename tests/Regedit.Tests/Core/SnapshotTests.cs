// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class SnapshotTests
{
    public static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var root = RegistryLocation.Parse("HKCU\\Software\\Snapshot tests", RegistryView.Registry32);
        var time = DateTimeOffset.UtcNow;
        foreach (var captureRoot in new[]
                 {
                     root with { SubKey = "Software\\Snapshot tests " },
                     new RegistryLocation(
                         RegistryHive.LocalMachine, Machine: "CaptureHost", View: RegistryView.Registry32)
                 })
        {
            var visited = new List<RegistryLocation>();
            var capture = RegistryBranchSnapshot.Capture(
                location =>
                {
                    visited.Add(location);
                    return new RegistrySnapshot(location == captureRoot ? [" ", "Alpha "] : [], []);
                }, captureRoot);
            check(
                visited.SequenceEqual(new[] { captureRoot, captureRoot.Child(" "), captureRoot.Child("Alpha ") }) &&
                capture.Keys.Select(key => key.Path).SequenceEqual(new[] { string.Empty, " ", "Alpha " }) &&
                capture.Root == captureRoot && capture.Computer == (captureRoot.Machine ?? Environment.MachineName),
                "Capture preserves empty relative roots, whitespace children, computer and view at key and hive roots");
        }

        RegistryBranchSnapshot Snapshot(params SnapshotKey[] keys)
        {
            return new RegistryBranchSnapshot(1, root, time, time, keys, Environment.MachineName);
        }

        var original = new RegistryValue("name", RegistryValueType.Binary, [0, 255, 17]);
        var before = Snapshot(
            new SnapshotKey(
                string.Empty, [original, new RegistryValue(string.Empty, RegistryValueType.String, [65, 0, 0, 0])]),
            new SnapshotKey("Removed", [new RegistryValue("unknown", (RegistryValueType)99, [1, 2])]),
            new SnapshotKey("Removed\\Child", []));
        var after = Snapshot(
            new SnapshotKey(
                string.Empty, [
                    original with { Type = (RegistryValueType)99 },
                    new RegistryValue("Added", RegistryValueType.DWord, [1, 0, 0, 0])
                ]), new SnapshotKey("Empty", []));
        var comparison = new RegistryComparison(before, after);
        check(comparison.Differences.Count == 7, "Snapshots detect key, type, value, and default-value changes");
        check(
            comparison.Differences.Single(change => change.Name == "name").Change == "Type changed",
            "Type-only changes are retained");
        check(
            new RegistryComparison(before, before).Differences.Count == 0,
            "Identical raw snapshots have no differences");
        var caseOnly = before with
        {
            Keys = before.Keys.Select(key => key with
            {
                Path = key.Path.ToUpperInvariant(),
                Values = key.Values.Select(value => value with { Name = value.Name.ToUpperInvariant() })
                    .ToArray()
            }).ToArray()
        };
        check(
            new RegistryComparison(before, caseOnly).Differences.Count == 0,
            "Registry identities compare case-insensitively");
        var rawChange = Snapshot(new SnapshotKey(string.Empty, [original with { Data = [0, 255, 17, 0] }]));
        check(
            new RegistryComparison(Snapshot(new SnapshotKey(string.Empty, [original])), rawChange).Differences.Single()
                .Change == "Data changed", "Trailing raw bytes are compared without normalization");
        await RejectAsync<InvalidOperationException>(
            () => Task.FromResult(
                new RegistryComparison(before, after with { Root = root with { View = RegistryView.Registry64 } })),
            check, "Cross-view comparison is rejected");
        await RejectAsync<InvalidOperationException>(
            () => Task.FromResult(new RegistryComparison(before, after with { Computer = "OtherComputer" })), check,
            "Snapshots from different computers are not silently compared");
        await RejectAsync<InvalidDataException>(
            () => Task.Run(() => (before with { Version = 9 }).Validate()), check,
            "Unknown snapshot versions are rejected");
        await RejectAsync<InvalidDataException>(
            () => Task.Run(() => Snapshot(
                    new SnapshotKey(string.Empty, []), new SnapshotKey("child", []), new SnapshotKey("CHILD", []))
                .Validate()), check, "Duplicate snapshot keys are rejected");
        await RejectAsync<InvalidDataException>(
            () => Task.Run(() =>
                Snapshot(new SnapshotKey(string.Empty, [original, original with { Name = "NAME" }])).Validate()), check,
            "Duplicate snapshot values are rejected");
        await RejectAsync<InvalidDataException>(
            () => Task.Run(() =>
                Snapshot(new SnapshotKey(string.Empty, []), new SnapshotKey("missing\\child", [])).Validate()), check,
            "Incomplete snapshot hierarchies are rejected");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await RejectAsync<OperationCanceledException>(
            () => Task.FromResult(RegistryBranchSnapshot.Capture(new RegistryService(), root, canceled.Token)), check,
            "Snapshot capture observes cancellation before reading keys");
        await RejectAsync<OperationCanceledException>(
            () => Task.FromResult(new RegistryComparison(before, after, canceled.Token)), check,
            "Comparison observes cancellation");
        var folder = Path.Combine(Path.GetTempPath(), "Regedit.SnapshotTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "capture.regsnap");
            var export = Path.Combine(folder, "changes.reg");
            await before.SaveAsync(file);
            var loaded = await RegistryBranchSnapshot.LoadAsync(file);
            check(
                loaded.Root == before.Root && new RegistryComparison(before, loaded).Differences.Count == 0,
                "Snapshot file preserves registry view, empty keys, unknown types, and bytes");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            check(
                document.RootElement.GetProperty("Keys")[0].GetProperty("Values")[0].GetProperty("Type").GetUInt32() ==
                3 && document.RootElement.GetProperty("Keys")[1].GetProperty("Values")[0].GetProperty("Type")
                    .GetUInt32() == 99,
                "Snapshot values retain numeric JSON type codes for both known and unknown types");
            check(
                !File.ReadAllText(file).Contains("\"Parent\""),
                "Snapshot serialization excludes computed path ancestry");
            await RejectAsync<OperationCanceledException>(
                () => after.SaveAsync(file, canceled.Token), check, "Snapshot save observes cancellation");
            check(
                new RegistryComparison(before, await RegistryBranchSnapshot.LoadAsync(file)).Differences.Count == 0,
                "Canceled snapshot save preserves the previous capture");
            var deep = before with
            {
                Root = root with { SubKey = string.Join('\\', Enumerable.Repeat("Nested", 100)) }
            };
            await deep.SaveAsync(file);
            check(
                (await RegistryBranchSnapshot.LoadAsync(file)).Root.SubKey == deep.Root.SubKey,
                "Deep registry paths round trip without nested JSON ancestry");
            var spaced = before with
            {
                Root = root.Child("Root "),
                Keys =
                [
                    new SnapshotKey(string.Empty, [original]), new SnapshotKey(" ", []),
                    new SnapshotKey(" \\Tail \t", [])
                ]
            };
            await spaced.SaveAsync(file);
            var restoredSpaces = await RegistryBranchSnapshot.LoadAsync(file);
            check(
                restoredSpaces.Root == spaced.Root && restoredSpaces.Keys.Select(key => key.Path)
                    .SequenceEqual(spaced.Keys.Select(key => key.Path)),
                "Snapshot validation and persistence preserve whitespace in root and descendant key names");
            await comparison.ExportAsync(comparison.Differences, export);
            var text = File.ReadAllText(export);
            check(
                text.Contains("[-HKEY_CURRENT_USER\\Software\\Snapshot tests\\Removed]") && text.Contains("@=-") &&
                text.Contains("hex(63):00,ff,11") && text.Contains("\"Added\"=hex(4):01,00,00,00") &&
                text.Contains("/reg:32"),
                "Patch exports deletions, raw types, and explicit registry-view instructions");
            var changedValue = comparison.Differences.Single(change => change.Name == "name");
            await comparison.ExportAsync([changedValue], export);
            text = File.ReadAllText(export);
            check(
                text.Contains("\"name\"=") && !text.Contains("@=-") && !text.Contains("[-"),
                "Partial export includes only selected changes");
            var deletedKey = comparison.Differences.First(change => change.Kind == RegistryChangeKind.KeyRemoved);
            await RejectAsync<InvalidOperationException>(
                () => comparison.ExportAsync([deletedKey], export), check,
                "Partial subtree deletion export is rejected");
            check(File.ReadAllText(export) == text, "Rejected export preserves an existing file");
            await RejectAsync<OperationCanceledException>(
                () => comparison.ExportAsync(comparison.Differences, export, canceled.Token), check,
                "Patch export observes cancellation");
            check(File.ReadAllText(export) == text, "Canceled export preserves an existing file");
            var badName = new RegistryComparison(
                Snapshot(new SnapshotKey(string.Empty, [])),
                Snapshot(
                    new SnapshotKey(string.Empty, [new RegistryValue("line\nbreak", RegistryValueType.String, [])])));
            await RejectAsync<IOException>(
                () => badName.ExportAsync(badName.Differences, export), check,
                "Unrepresentable patch names are rejected");
            check(
                File.ReadAllText(export) == text && !Directory.EnumerateFiles(folder, "*.tmp").Any(),
                "Failed export cleans temporary files and preserves destination");
            await File.WriteAllTextAsync(file, "{\"Version\":1,\"unexpected\":true}");
            await RejectAsync<JsonException>(
                () => RegistryBranchSnapshot.LoadAsync(file), check, "Malformed snapshot files fail closed");
            if (registryTests)
            {
                await RunRegistryAsync(folder, check);
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.Delete(file);
            }

            Directory.Delete(folder);
        }
    }

    private static async Task RunRegistryAsync(string folder, Action<bool, string> check)
    {
        var service = new RegistryService();
        var root = RegistryLocation.Parse("HKCU\\Software\\Regedit.SnapshotTests." + Guid.NewGuid().ToString("N"));
        service.CreateKey(root.Parent!, root.Name);
        try
        {
            var original = new RegistryValue(
                "number", RegistryValueType.DWord, RegistryValue.ParseData(RegistryValueType.DWord, "FFFFFFFF"));
            service.Write(root, original, null);
            service.Write(root, new RegistryValue(string.Empty, RegistryValueType.String, [65, 0, 0, 0]), null);
            service.CreateKey(root, "Removed");
            service.CreateKey(root.Child("Removed"), "Child");
            service.Write(root.Child("Removed"), new RegistryValue("payload", (RegistryValueType)99, [1, 2, 3]), null);
            var before = RegistryBranchSnapshot.Capture(service, root);
            var backup = Path.Combine(folder, "before.reg");
            await RegistryExport.ExportAsync(service, [root], backup);
            service.Write(root, original with { Type = RegistryValueType.Binary, Data = [255, 0, 255] }, original);
            service.DeleteValue(root, service.ReadValue(root, string.Empty)!);
            service.DeleteKey(root.Child("Removed"));
            service.CreateKey(root, "Empty");
            service.Write(root, new RegistryValue("odd\\\"name", (RegistryValueType)99, [0, 255, 17]), null);
            service.Write(root, new RegistryValue("malformed", RegistryValueType.String, [65]), null);
            var after = RegistryBranchSnapshot.Capture(service, root);
            var comparison = new RegistryComparison(before, after);
            var patch = Path.Combine(folder, "patch.reg");
            await comparison.ExportAsync(comparison.Differences, patch);
            service.DeleteKey(root);
            await RegistryService.RunRegAsync("import", backup, "/reg:64");
            await RegistryService.RunRegAsync("import", patch, "/reg:64");
            check(
                new RegistryComparison(after, RegistryBranchSnapshot.Capture(service, root)).Differences.Count == 0,
                "Windows imports selected patch with exact keys, types, and raw bytes");
            service.DeleteKey(root);
            var missing = RegistryBranchSnapshot.Capture(service, root, allowMissingRoot: true);
            check(
                missing.Keys.Length == 0 &&
                new RegistryComparison(after, missing).Differences.Any(change =>
                    change.Kind == RegistryChangeKind.KeyRemoved),
                "Deleted snapshot root produces removal differences");
            await RejectAsync<FileNotFoundException>(
                () => Task.FromResult(RegistryBranchSnapshot.Capture(service, root)), check,
                "Capturing a nonexistent branch fails instead of saving an empty backup");
        }
        finally
        {
            using var parent = service.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
        }
    }

    private static async Task RejectAsync<T>(Func<Task> action, Action<bool, string> check, string name)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            check(true, name);
            return;
        }

        check(false, name);
    }
}
