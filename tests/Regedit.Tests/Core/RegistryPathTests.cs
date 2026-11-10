// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;
using Registry = Microsoft.Win32.Registry;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class RegistryPathTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> rejects)
    {
        foreach (var root in RegistryLocation.Roots)
        {
            var location = new RegistryLocation(root.Hive, @"Software\My App", View: RegistryView.Registry32);
            foreach (var address in new[]
                     {
                         root.Alias.ToLowerInvariant() + @":\Software\My App", root.Name + @":\Software\My App\",
                         "registry::" + location.Path, @"Microsoft.PowerShell.Core\Registry::" + location.Path,
                         "[" + location.Path + "]", "  '" + root.Alias + @":\Software\My App'  ",
                         "\"[" + location.Path + "]\""
                     })
            {
                check(
                    RegistryLocation.Parse(address, location.View) == location,
                    "Flexible input retains hive, key and view: " + address);
            }

            foreach (var subKey in new[]
                     {
                         string.Empty, location.SubKey, "A/B:Registry::[name]'\"", "Tail ", " ",
                         "Nested\\ \\Tail \t"
                     })
            {
                var key = location with { SubKey = subKey };
                foreach (var format in Enum.GetValues<RegistryPathFormat>())
                {
                    var path = key.FormatPath(format);
                    var hasDrive = root.Hive is RegistryHive.CurrentUser or RegistryHive.LocalMachine;
                    if (format == RegistryPathFormat.PowerShellDrive && !hasDrive)
                    {
                        check(path is null, "No undefined PowerShell drive for " + root.Alias);
                    }
                    else
                    {
                        var expected = format switch
                        {
                            RegistryPathFormat.PowerShellDrive => root.Alias + ":\\" + subKey,
                            RegistryPathFormat.PowerShellProvider => "Registry::" + key.Path,
                            _ => "[" + key.Path + "]"
                        };
                        check(path == expected, "Exact copy format: " + format + " " + key.Path);
                        check(
                            RegistryLocation.Parse(path!, key.View) == key,
                            "Copy format round-trips: " + format + " " + key.Path);
                    }
                }
            }

            check(
                RegistryLocation.Parse(root.Alias + ":").Hive == root.Hive,
                "Drive root without a separator: " + root.Alias);
        }

        foreach (var address in new[] { "Computer", @"\\SERVER", @"\\SERVER\HKLM\Software", @"\\SERVER\HKU" })
        {
            var location = RegistryLocation.Parse(address);
            check(
                RegistryLocation.Parse(location.Address, location.View) == location,
                "Standard address remains unchanged: " + address);
            foreach (var format in Enum.GetValues<RegistryPathFormat>())
            {
                check(
                    location.FormatPath(format) is null,
                    "Copy format cannot lose a computer identity: " + format + " " + address);
            }
        }

        check(
            RegistryLocation.Parse(@"[HKCU\Software\Name]]").SubKey == @"Software\Name]",
            "Header removes only its outer brackets");
        check(RegistryLocation.Parse("'HKCU:\\Tail '").SubKey == "Tail ", "Quotes protect trailing key whitespace");
        check(
            RegistryLocation.Parse("  '[HKCU\\Tail ]'  ").SubKey == "Tail ",
            "Padding outside quoted headers does not change the key name");
        foreach (var location in new[]
                 {
                     new RegistryLocation(RegistryHive.CurrentUser, "Tail "),
                     new RegistryLocation(RegistryHive.CurrentUser, " ", View: RegistryView.Registry32),
                     new RegistryLocation(
                         RegistryHive.LocalMachine, "Nested\\ \t\\Tail\u2003", "SERVER", RegistryView.Registry32),
                     new RegistryLocation(RegistryHive.Users, "Line\r\n", "SERVER")
                 })
        {
            RegistryWorkspace.ValidateLocation(location);
            check(
                RegistryLocation.Parse(location.Address, location.View) == location,
                "Standard addresses retain significant whitespace and computer/view identity");
            check(
                location.Parent!.Child(location.Name) == location,
                "Enumerated whitespace names can be joined without normalization");
        }

        var valid = new RegistryLocation(RegistryHive.LocalMachine, "Software");
        foreach (var invalid in new[]
                 {
                     null!, valid with { SubKey = null! }, valid with { SubKey = "\\Software" },
                     valid with { SubKey = "Software\\" }, valid with { SubKey = "A\\\\B" },
                     valid with { SubKey = "A\0B" }, valid with { SubKey = new string('x', 256) },
                     valid with { Hive = (RegistryHive)123 }, valid with { View = RegistryView.Default },
                     valid with { Machine = string.Empty }, valid with { Machine = " " },
                     valid with { Machine = "SERVER\\HKLM" }, valid with { Machine = "SERVER/Name" },
                     valid with { Machine = "SERVER\0" },
                     valid with { Machine = "SERVER", Hive = RegistryHive.CurrentUser },
                     new RegistryLocation(SubKey: "Hidden")
                 })
        {
            rejects(
                () => RegistryWorkspace.ValidateLocation(invalid, true),
                "Structural location validation rejects malformed saved identities");
        }

        check(
            new RegistryLocation(RegistryHive.CurrentUser, "Line\r\nBreak").FormatPath(RegistryPathFormat.RegFileHeader)
                is null, "A key containing a line break cannot form a registry file header");
        foreach (var address in new[]
                 {
                     string.Empty, "[]", "[HKCU", "[-HKEY_CURRENT_USER\\Software]", "Registry::",
                     "Registry::UNKNOWN\\Key", @"HKLM:Software", @"HKLM::\Software", @"HKLM:\Software\\Key",
                     "Registry::HKCU\\Bad\0Key", @"C:\Software", @"FileSystem::HKCU\Software",
                     @"\\SERVER\HKCU:\Software", "'HKCU:\\Software\""
                 })
        {
            rejects(
                () => RegistryLocation.Parse(address),
                "Reject malformed registry input: " + address.Replace("\0", "\\0"));
        }
    }

    public static async Task RunRegistryAsync(Action<bool, string> check)
    {
        var registry = new RegistryService();
        var root = new RegistryLocation(
            RegistryHive.CurrentUser, "Software\\Regedit.PathTests." + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(Path.GetTempPath(), "Regedit.PathTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            registry.CreateKey(root.Parent!, root.Name);
            var nested = root.Child("alpha ");
            var locations = new[]
            {
                root.Child("Target"), root.Child("Target "), root.Child(" "), root.Child("Zulu"), nested,
                nested.Child("tail "), nested.Child(" Beta")
            };
            var ordered = new[]
            {
                root, locations[2], nested, locations[6], locations[5], locations[0], locations[1], locations[3]
            };
            for (var index = 0; index < locations.Length; index++)
            {
                registry.CreateKey(locations[index].Parent!, locations[index].Name);
                registry.Write(
                    locations[index], new RegistryValue("Marker", RegistryValueType.Binary, [(byte)index]), null);
            }

            check(
                registry.SubKeys(root).SequenceEqual(new[] { " ", "alpha ", "Target", "Target ", "Zulu" }) &&
                registry.Read(root).SubKeys.SequenceEqual(registry.SubKeys(root)),
                "Native child reads sort ordinally without trimming significant whitespace");
            var browser = new BrowserViewModel(registry, root) { ShowKeysInList = true };
            await browser.NavigateAsync(root);
            check(
                browser.Error is null && locations.Where(location => location.Parent == root)
                    .All(location => browser.Entries.Any(entry => entry.Key == location)),
                "A whitespace-only child does not prevent browsing its parent");
            for (var index = 0; index < locations.Length; index++)
            {
                await browser.NavigateAsync(RegistryLocation.Parse(locations[index].Address));
                check(
                    browser.Error is null && browser.Location == locations[index] &&
                    browser.Entries.Single(entry => entry.Value?.Name == "Marker").Value!.Data[0] == index,
                    "Navigation opens the exact whitespace name rather than a trimmed neighboring key");
            }

            var found = RegistrySearch.FindAll(
                registry,
                new SavedSearch("Markers", [root], new SearchOptions("Marker", false, Data: false, Whole: true)),
                default);
            check(
                found.SkippedKeys == 0 && found.Matches.Length == locations.Length &&
                locations.All(location => found.Matches.Any(match => match.Location == location)),
                "Find all traverses whitespace-only and trailing-space keys");
            check(
                found.Matches.Select(match => match.Location).SequenceEqual(ordered.Skip(1)),
                "Find all visits exact whitespace paths in sorted depth-first order");
            check(
                registry.FindNext(
                        root, new SearchOptions("Target ", Values: false, Data: false, Whole: true), null, default).Hit
                    ?.Location == locations[1], "Find next retains a trailing-space key identity");
            var report = RegistryReport.Create(registry, [root], true);
            check(
                locations.All(location => report.Contains(location.Address + Environment.NewLine)),
                "Reports retain every exact whitespace key path");
            check(
                report.Split(Environment.NewLine).Where(line => line.StartsWith(root.Address, StringComparison.Ordinal))
                    .SequenceEqual(ordered.Select(location => location.Address)),
                "Recursive reports use sorted depth-first paths without trimming whitespace");
            var singlePage = RegistryReport.Create(registry, [root], false);
            check(
                singlePage.Split(Environment.NewLine)
                    .Where(line => line.StartsWith(root.Address, StringComparison.Ordinal))
                    .SequenceEqual(new[] { root.Address }), "A current-key report does not traverse children");

            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var observed
                    = new TaskCompletionSource<RegistryComparison>(TaskCreationOptions.RunContinuationsAsynchronously);
                var watch = RegistryWatcher.RunAsync(
                    registry, locations[2], changes =>
                    {
                        observed.TrySetResult(changes);
                        return Task.CompletedTask;
                    }, () => ready.TrySetResult(), cancellation.Token);
                try
                {
                    await ready.Task.WaitAsync(cancellation.Token);
                    registry.Write(locations[2], new RegistryValue("Watched", RegistryValueType.Binary, [42]), null);
                    var notification = await observed.Task.WaitAsync(cancellation.Token);
                    check(
                        notification.Differences.Any(change =>
                            change.Location == locations[2] && change.After?.Name == "Watched"),
                        "A native branch watch observes changes at a whitespace-only key");
                }
                finally
                {
                    cancellation.Cancel();
                    try
                    {
                        await watch;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            var journal = new RegistryJournal(folder);
            new RegistryService { Journal = journal }.DeleteKey(locations[2]);
            var deleted = new RegistryJournal(folder).ReadEntries().Single();
            journal.Revert(deleted.Id);
            check(
                registry.ReadValue(locations[2], "Marker")!.Data[0] == 2 &&
                registry.ReadValue(locations[2], "Watched")!.Data[0] == 42 &&
                registry.ReadValue(locations[0], "Marker")!.Data[0] == 0,
                "Journal reload and revert restore a whitespace-only key without changing its neighbors");

            var capture = RegistryBranchSnapshot.Capture(registry, root);
            check(
                capture.Keys.Select(key => key.Path).SequenceEqual(
                    ordered.Select(location =>
                        location == root ? string.Empty : location.SubKey[(root.SubKey.Length + 1)..])),
                "Snapshots keep an empty relative root and sorted exact descendant suffixes");
            var snapshotFile = Path.Combine(folder, "paths.regsnap");
            await capture.SaveAsync(snapshotFile);
            var restored = await RegistryBranchSnapshot.LoadAsync(snapshotFile);
            check(
                new RegistryComparison(capture, restored).Differences.Count == 0 && locations.All(location =>
                    restored.Keys.Any(key => key.Path == location.SubKey[(root.SubKey.Length + 1)..])),
                "Native snapshots preserve exact names through validation and disk persistence");
            var exportFile = Path.Combine(folder, "paths.reg");
            await RegistryExport.ExportAsync(registry, [root], exportFile);
            check(
                File.ReadAllLines(exportFile).Where(line => line.StartsWith('['))
                    .SequenceEqual(ordered.Select(location => "[" + location.Path + "]")),
                "Exports write sorted depth-first headers with exact whitespace paths");
            registry.DeleteKey(root);
            await RegistryService.RunRegAsync("import", exportFile, "/reg:64");
            check(
                new RegistryComparison(capture, RegistryBranchSnapshot.Capture(registry, root)).Differences.Count == 0,
                "Windows imports exported whitespace keys without merging their trimmed neighbors");
        }
        finally
        {
            using var parent = Registry.CurrentUser.OpenSubKey("Software", true);
            parent!.DeleteSubKeyTree(root.Name, false);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.Delete(file);
            }

            Directory.Delete(folder);
        }
    }
}
