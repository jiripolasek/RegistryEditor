// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Inspection;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class InvestigationTests
{
    public static async Task RunAsync(Action<bool, string> check, bool integration)
    {
        var root = RegistryLocation.Parse("HKCU\\Software\\JournalTest");
        check(
            RegistryJournal.Overlaps(new JournalScope(root), new JournalScope(root.Child("Child"), ["v"])),
            "Journal branch scope includes descendants");
        check(
            !RegistryJournal.Overlaps(new JournalScope(root, ["a"]), new JournalScope(root, ["b"])),
            "Independent values can be reverted selectively");
        check(
            RegistryJournal.Overlaps(new JournalScope(root, ["A"]), new JournalScope(root, ["a"])),
            "Journal conflict identities ignore case");
        check(
            !RegistryJournal.Overlaps(
                new JournalScope(root), new JournalScope(root with { Machine = "other-computer" })),
            "Journal conflict scopes distinguish computers");
        var remote = RegistryLocation.Parse(@"\\journal-test\HKLM\SYSTEM\CurrentControlSet");
        var other = remote with { SubKey = @"SYSTEM\ControlSet001", View = RegistryView.Registry32 };
        check(
            RegistryJournal.Overlaps(new JournalScope(remote, ["Name"]), new JournalScope(other, ["name"])) &&
            RegistryJournal.Overlaps(new JournalScope(remote), new JournalScope(other.Child("Child"), ["v"])),
            "Unresolved backing keys on the same remote computer cannot bypass a later edit");
        check(
            !RegistryJournal.Overlaps(new JournalScope(remote, ["A"]), new JournalScope(other, ["B"])) &&
            !RegistryJournal.Overlaps(
                new JournalScope(remote), new JournalScope(other with { Machine = "other-server" })),
            "Remote safeguards preserve distinct value names and separate computers");
        check(
            RegistrySearch.ParseTypes("REG_SZ, 4, reg_qword")!.SequenceEqual(
                [RegistryValueType.String, RegistryValueType.DWord, RegistryValueType.QWord]),
            "Search type filters accept names and numeric codes");
        check(
            RegistrySearch.ParseTypes("REG_LINK, REG_DWORD_BIG_ENDIAN, 99, 4294967295, 99")!.SequenceEqual(
            [
                RegistryValueType.Link, RegistryValueType.DWordBigEndian, (RegistryValueType)99,
                (RegistryValueType)uint.MaxValue
            ]), "Search type filters preserve uncommon and unknown unsigned codes while removing duplicates");
        var numeric = RegistryValueInspector.Number(
            new RegistryValue("n", RegistryValueType.QWord, Enumerable.Repeat((byte)255, 8).ToArray()))!;
        check(
            numeric.Decimal == ulong.MaxValue.ToString() && numeric.Hexadecimal == "0xFFFFFFFFFFFFFFFF" &&
            numeric.Bits == new string('1', 64),
            "Inspector preserves unsigned QWORD decimal, hexadecimal, and all bits");
        check(
            RegistryValueInspector.Number(new RegistryValue("n", RegistryValueType.DWordBigEndian, [0, 0, 1, 0]))!
                .Decimal == "256", "Inspector honors big-endian DWORD type");
        check(
            RegistryValueInspector.Interpret([0, 65, 0, 66, 0], 1, 4, ByteInterpretation.Utf16LittleEndian) == "AB",
            "Inspector decodes only the selected byte range");
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        check(
            RegistryValueInspector.Interpret(guid.ToByteArray(), 0, 16, ByteInterpretation.WindowsGuid) ==
            guid.ToString(), "Inspector explicitly decodes Windows GUID byte order");
        check(
            RegistryValueInspector.Interpret(guid.ToByteArray(true), 0, 16, ByteInterpretation.Rfc4122Guid) ==
            guid.ToString(), "Inspector explicitly decodes network GUID byte order");
        check(
            RegistryValueInspector.Interpret(new byte[8], 0, 8, ByteInterpretation.UnixSecondsInt64LittleEndian)
                .StartsWith("1970-01-01T00:00:00"), "Inspector timestamp format uses UTC Unix epoch");
        var note = new KeyNote(root, "Investigation notes");
        check(
            note.Matches(root with { SubKey = root.SubKey.ToUpperInvariant() }) &&
            !note.Matches(root with { View = RegistryView.Registry32 }),
            "Notes identify a key case-insensitively while preserving its view");
        var workspace = new RegistryWorkspace(
            "Investigation", [root with { View = RegistryView.Registry32 }], [
                new SavedSearch(
                    "Query", [root],
                    new SearchOptions("needle", Types: [RegistryValueType.Binary, RegistryValueType.QWord]))
            ], [new WatchDefinition(root, string.Empty)]);
        var workspaceJson = System.Text.Json.JsonSerializer.Serialize(
            workspace, new System.Text.Json.JsonSerializerOptions { IgnoreReadOnlyProperties = true });
        using var workspaceDocument = System.Text.Json.JsonDocument.Parse(workspaceJson);
        check(
            workspaceDocument.RootElement.GetProperty("Searches")[0].GetProperty("Options").GetProperty("Types")
                .EnumerateArray().Select(type => type.GetUInt32()).SequenceEqual([3u, 11u]),
            "Workspace search filters retain their existing numeric JSON representation");
        var restored = System.Text.Json.JsonSerializer.Deserialize<RegistryWorkspace>(workspaceJson)!;
        restored.Validate();
        check(
            restored.Tabs[0].View == RegistryView.Registry32 &&
            restored.Searches[0].Options.Types!.SequenceEqual(
                new RegistryValueType[] { RegistryValueType.Binary, RegistryValueType.QWord }) &&
            restored.Watches[0].ValueName == string.Empty,
            "Workspace persistence retains tab views, search filters, and default-value watches");
        var sampleChange = new RegistryDifference(
            root with { View = RegistryView.Registry32 }, RegistryChangeKind.ValueAdded,
            After: new RegistryValue("a'\"&%name", (RegistryValueType)99, [0, 255, 1]));
        var script = RegistryScripts.PowerShell([sampleChange]);
        check(
            script.Contains("RegistryView]::Registry32") && script.Contains("[uint32]99") &&
            script.Contains("a''\"&%name") && script.Contains("AP8B"),
            "PowerShell scripts preserve raw types, bytes, quoted names, and registry view");
        check(
            RegistryScripts
                .PowerShell(
                    [sampleChange with { After = new RegistryValue("Known", RegistryValueType.DWord, [1, 0, 0, 0]) }])
                .Contains("[uint32]4, $bytes"),
            "PowerShell scripts write known enum members as native numeric type codes");
        try
        {
            RegistryScripts.RegExe(
                [sampleChange with { After = new RegistryValue("None", RegistryValueType.None, [0, 16, 255]) }]);
            throw new Exception("REG_NONE must use the raw PowerShell writer.");
        }
        catch (InvalidOperationException)
        {
            check(true, "reg.exe copying refuses REG_NONE bytes it would silently convert to text");
        }

        if (!integration)
        {
            return;
        }

        root = RegistryLocation.Parse("HKCU\\Software\\Regedit.InvestigationTests." + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(Path.GetTempPath(), "Regedit.InvestigationTests." + Guid.NewGuid().ToString("N"));
        var raw = new RegistryService();
        raw.CreateKey(root.Parent!, root.Name);
        try
        {
            var journal = new RegistryJournal(folder);
            var registry = new RegistryService { Journal = journal };
            var a = new RegistryValue("A", RegistryValueType.DWord, [1, 0, 0, 0]);
            registry.Write(root, a, null);
            var created = journal.ReadEntries().Last();
            check(
                created.After is not null && created.Differences.Single().Kind == RegistryChangeKind.ValueAdded,
                "Journal persists exact created value");
            registry.Write(root, new RegistryValue("B", RegistryValueType.Binary, [0, 255, 1]), null);
            journal.Revert(created.Id);
            check(
                raw.ReadValue(root, "A") is null && raw.ReadValue(root, "B") is not null,
                "Selective revert leaves independent edits intact");
            registry.Write(root, a, null);
            var first = journal.ReadEntries().Last();
            registry.Write(root, a with { Data = [2, 0, 0, 0] }, a);
            var second = journal.ReadEntries().Last();
            Throws(() => journal.Revert(first.Id), check, "Later overlapping journal edit blocks revert");
            journal.Revert(second.Id);
            journal.Revert(first.Id);
            check(raw.ReadValue(root, "A") is null, "Earlier edits can be reverted after later edits are reverted");
            registry.Write(root, a, null);
            var external = journal.ReadEntries().Last();
            raw.Write(root, a with { Data = [3, 0, 0, 0] }, a);
            Throws(() => journal.Revert(external.Id), check, "External change blocks revert without overwriting it");
            check(raw.ReadValue(root, "A")!.Data[0] == 3, "Conflicting external data survives refused revert");
            raw.CreateKey(root, "Branch");
            raw.CreateKey(root.Child("Branch"), "Empty");
            raw.Write(root.Child("Branch"), new RegistryValue(string.Empty, (RegistryValueType)99, [0, 1, 2]), null);
            registry.DeleteKey(root.Child("Branch"));
            var deleted = journal.ReadEntries().Last();
            journal = new RegistryJournal(folder);
            journal.Revert(deleted.Id);
            check(
                raw.ReadValue(root.Child("Branch"), string.Empty) is { Type: (RegistryValueType)99 } &&
                raw.SubKeys(root.Child("Branch")).Contains("Empty"),
                "Persisted journal restores deleted branch, empty keys, and raw values");
            registry.RenameKey(root.Child("Branch"), "Renamed");
            journal.Revert(journal.ReadEntries().Last().Id);
            check(
                raw.SubKeys(root).Contains("Branch") && !raw.SubKeys(root).Contains("Renamed"),
                "Key rename revert restores original key");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed
                = new TaskCompletionSource<RegistryComparison>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = RegistryWatcher.RunAsync(
                raw, root, comparison =>
                {
                    observed.TrySetResult(comparison);
                    return Task.CompletedTask;
                }, () => ready.SetResult(), cancellation.Token);
            await ready.Task.WaitAsync(cancellation.Token);
            raw.Write(
                root.Child("Branch"),
                new RegistryValue(
                    "Watched", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "changed")),
                null);
            var notification = await observed.Task.WaitAsync(cancellation.Token);
            check(
                notification.Differences.Any(change =>
                    change.After?.Name == "Watched" && change.Location.Name == "Branch"),
                "Native watch observes subkey value changes with after bytes");
            cancellation.Cancel();
            try
            {
                await watch;
            }
            catch (OperationCanceledException)
            {
            }

            check(watch.IsCompleted, "Native watch cancellation releases the pending wait");
            var query = new SavedSearch(
                "Test", [root], new SearchOptions("changed", false, false, true, Types: [RegistryValueType.String]));
            var found = RegistrySearch.FindAll(raw, query, default);
            var progressive = new List<RegistrySearchMatch>();
            var streamed = RegistrySearch.FindAll(raw, query, default, progress: batch => progressive.AddRange(batch));
            check(
                progressive.SequenceEqual(streamed.Matches) && progressive.Count == found.Matches.Length,
                "Progressive search publishes the same matches once and flushes its final partial batch");
            check(
                found.Matches.Length == 1 && found.Matches[0].ValueName == "Watched" &&
                found.Matches[0].MatchedText == "changed", "Find all retains matched data and surrounding key path");
            check(
                RegistrySearch.FindAll(
                        raw, query with { Options = query.Options with { Types = [RegistryValueType.DWord] } }, default)
                    .Matches.Length == 0, "Find all applies value type filters");
            check(
                RegistrySearch.FindAll(raw, new SavedSearch("All", [root], new SearchOptions("a")), default, 1)
                    .Truncated, "Find all reports result truncation explicitly");
            var scriptRoot = root.Child("Scripts");
            var values = new[]
            {
                new RegistryValue("quote'\" & name", (RegistryValueType)99, [0, 255, 1]),
                new RegistryValue(string.Empty, RegistryValueType.String, [65, 0, 0]),
                new RegistryValue("Qword", RegistryValueType.QWord, Enumerable.Repeat((byte)255, 8).ToArray()),
                new RegistryValue("None", RegistryValueType.None, [0, 16, 255])
            };
            var changes = values.Select(value => new RegistryDifference(
                scriptRoot, RegistryChangeKind.ValueAdded, After: value)).ToArray();
            var psFile = Path.Combine(folder, "script.ps1");
            File.WriteAllText(psFile, RegistryScripts.PowerShell(changes));
            await Run("pwsh", ["-NoProfile", "-File", psFile]);
            check(
                values.All(value => value.SameData(raw.ReadValue(scriptRoot, value.Name))),
                "Generated PowerShell runs and preserves unknown types, malformed strings, and unsigned QWORDs");
            var quotedChanges = new[] { '\'', '\u2018', '\u2019', '\u201a', '\u201b' }.Select(quote =>
                    new RegistryDifference(
                        scriptRoot.Child($"Key{(int)quote}{quote}); $global:RegeditScriptInjection = $true; #"),
                        RegistryChangeKind.ValueAdded,
                        After: new RegistryValue(
                            $"Value{quote}, 0, 3, [byte[]]@(), 0); $global:RegeditScriptInjection = $true; #",
                            RegistryValueType.String,
                            RegistryValue.ParseData(
                                RegistryValueType.String,
                                $"Literal {quote} \"\u201c\u201d\u201e\u201f $($global:RegeditScriptInjection = $true) `\r\ntext"))))
                .ToArray();
            foreach (var shell in new[]
                     {
                         "pwsh",
                         Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                     })
            {
                async Task ApplyQuotedChanges(IEnumerable<RegistryDifference> selected)
                {
                    var generated = "$global:RegeditScriptInjection = $false\r\n" +
                                    RegistryScripts.PowerShell(selected) +
                                    "\r\nif ($global:RegeditScriptInjection) { throw 'Registry content executed as PowerShell.' }\r\n";
                    File.WriteAllText(psFile, generated, new System.Text.UTF8Encoding(true));
                    await Run(shell, ["-NoProfile", "-NonInteractive", "-File", psFile]);
                }

                await ApplyQuotedChanges(quotedChanges);
                check(
                    quotedChanges.All(change =>
                        change.After!.SameData(raw.ReadValue(change.Location, change.After.Name))),
                    "PowerShell preserves ASCII/smart quotes in key names, value names, and data without executing registry content: " +
                    shell);
                await ApplyQuotedChanges(
                    quotedChanges.Select(change => change with
                    {
                        Kind = RegistryChangeKind.ValueRemoved, Before = change.After, After = null
                    }));
                check(
                    quotedChanges.All(change => raw.ReadValue(change.Location, change.After!.Name) is null),
                    "PowerShell deletes the exact quoted value names: " + shell);
                await ApplyQuotedChanges(
                    quotedChanges.Select(change => new RegistryDifference(
                        change.Location, RegistryChangeKind.KeyRemoved)));
                check(
                    quotedChanges.All(change => !raw.SubKeys(scriptRoot).Contains(change.Location.Name)),
                    "PowerShell deletes the exact quoted key names: " + shell);
            }

            var batchValues = new[]
            {
                new RegistryValue(
                    "Percent%Name! 🐈", RegistryValueType.ExpandString,
                    RegistryValue.ParseData(
                        RegistryValueType.ExpandString, "%SystemRoot%\\literal & (test)! Žluťoučký")),
                new RegistryValue(
                    "Multi", RegistryValueType.MultiString,
                    RegistryValue.ParseData(RegistryValueType.MultiString, "one\ntwo")),
                new RegistryValue("Binary", RegistryValueType.Binary, [0, 255, 1]),
                new RegistryValue("BigEndian", RegistryValueType.DWordBigEndian, [1, 2, 3, 4]),
                new RegistryValue("Qword2", RegistryValueType.QWord, Enumerable.Repeat((byte)255, 8).ToArray())
            };
            var cmdFile = Path.Combine(folder, "script.cmd");
            File.WriteAllText(
                cmdFile,
                RegistryScripts.RegExe(
                    batchValues.Select(value => new RegistryDifference(
                        scriptRoot, RegistryChangeKind.ValueAdded, After: value))));
            await Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", cmdFile]);
            foreach (var value in batchValues)
            {
                var actual = raw.ReadValue(scriptRoot, value.Name);
                check(
                    value.SameData(actual),
                    $"Generated reg.exe preserves {value.TypeName}: expected {Convert.ToHexString(value.Data)}, actual {Convert.ToHexString(actual?.Data ?? [])}");
            }

            registry.Write(root, new RegistryValue("ActivityCheck", RegistryValueType.DWord, [1, 0, 0, 0]), null);
            var beforeActivity = journal.ReadEntries().Last();
            journal.RecordActivity(
                "Import registry file", root, "Test activity",
                () => raw.Write(root, new RegistryValue("ExternalImport", RegistryValueType.Binary, [1]), null));
            check(
                journal.ReadEntries().Last().Activity == "Test activity",
                "External registry operations have durable activity records");
            Throws(
                () => journal.Revert(beforeActivity.Id), check,
                "Later external activity prevents unsafe selective revert");
        }
        finally
        {
            raw.DeleteKey(root);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    private static async Task Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Generated script failed: " + await output + await error);
        }
    }

    private static void Throws(Action action, Action<bool, string> check, string name)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            check(true, name);
            return;
        }

        throw new InvalidOperationException("Expected conflict: " + name);
    }
}
