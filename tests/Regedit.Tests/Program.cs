// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using JPSoftworks.Regedit.Controls.Omnibox;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Security;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Tests.Application;
using JPSoftworks.Regedit.Tests.Core;
using JPSoftworks.Regedit.Tests.Infrastructure;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;
using Registry = Microsoft.Win32.Registry;

if (args.FirstOrDefault() == "--settings-writer")
{
    await SettingsConcurrencyTests.RunWriterAsync(args);
    return;
}

if (args.FirstOrDefault() == "--journal-security")
{
    await JournalSecurityTests.ProbeAsync(args[1]);
    return;
}

if (args.FirstOrDefault() == "--journal-security-worker")
{
    await JournalSecurityTests.WorkerAsync(args[1]);
    return;
}

if (args.FirstOrDefault() == "--journal-lock-holder")
{
    await JournalLockTests.HoldAsync(args[1]);
    return;
}

if (args.FirstOrDefault() == "--jump-list-lock-holder")
{
    JumpListServiceTests.HoldLease(args[1]);
    return;
}

if (args.FirstOrDefault() == "--journal-history-writer")
{
    JournalHistoryTests.RunWriter(args[1], args[2]);
    return;
}

var passed = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAILED: " + name);
    }

    passed++;
    Console.WriteLine("PASS: " + name);
}

void Throws<T>(Action action, string name)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        passed++;
        Console.WriteLine("PASS: " + name);
        return;
    }

    throw new InvalidOperationException("FAILED: " + name);
}

var suites = new List<TestSuite>();

void Add(string name, Action run)
{
    suites.Add(
        new TestSuite(
            name, () =>
            {
                run();
                return Task.CompletedTask;
            }));
}

void AddAsync(string name, Func<Task> run)
{
    suites.Add(new TestSuite(name, run));
}

AddAsync("ux-mvp", () => UxMvpTests.RunAsync(Check));
Add("logging", () => LoggingTests.Run(Check));
AddAsync("app-services", () => AppServicesTests.RunAsync(Check));
AddAsync("main-window-view-model", () => MainWindowViewModelTests.RunAsync(Check));
AddAsync("bottom-panel", () => BottomPanelTests.RunAsync(Check));
AddAsync("search-result-navigation", () => SearchResultNavigationTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("investigation-view-model", () => InvestigationViewModelTests.RunAsync(Check));
AddAsync("branch-workflow", () => BranchWorkflowTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("large-value", () => LargeValueTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("registry-export", () => RegistryExportTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("clipboard-routing", () => ClipboardRoutingTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("notes-view-model", () => NotesViewModelTests.RunAsync(Check));
AddAsync("notes-store", () => NotesStoreTests.RunAsync(Check));
AddAsync("favorites-view-model", () => FavoritesViewModelTests.RunAsync(Check));
AddAsync("code-behind-moves", () => CodeBehindMoveTests.RunAsync(Check));
AddAsync("network-registry-connection", () => NetworkRegistryConnectionTests.RunAsync(Check));
AddAsync("network-registry-lifetime", () => NetworkRegistryLifetimeTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("network-registry-native", () => NetworkRegistryNativeTests.RunAsync(Check));
Add("layout-state", () => LayoutStateTests.Run(Check));
Add("caption-layout", () => CaptionLayoutTests.Run(Check));
Add("column-layout", () => ColumnLayoutTests.Run(Check));
Add("column-layout-settings", () => ColumnLayoutSettingsTests.Run(Check));
Add("range-observable-collection", () => RangeObservableCollectionTests.Run(Check, Throws<InvalidOperationException>));
Add("registry-print-pagination", () => RegistryPrintPaginationTests.Run(Check));

Add(
    "omnibox-layout", () =>
    {
        Check(OmniboxLayout.FirstVisible([], 100, 32) == 0, "Empty address has no overflow");
        Check(OmniboxLayout.FirstVisible([80, 70, 90], 240, 32) == 0, "Breadcrumbs fit at the exact available width");
        Check(OmniboxLayout.FirstVisible([80, 70, 90], 200, 32) == 1, "Overflow retains the longest fitting suffix");
        Check(
            OmniboxLayout.FirstVisible([80, 70, 90], 180, 32) == 2, "Overflow reserves space for the ancestor button");
        Check(OmniboxLayout.FirstVisible([80, 70, 900], 100, 32) == 2, "An oversized current location stays visible");
        Check(OmniboxLayout.FirstVisible([900], 100, 32) == 0, "One long location does not show empty overflow");
        Check(
            OmniboxLayout.FirstVisible([80, 70, 90], 0, 32) == 2, "A collapsed viewport retains the current location");
    });

Add(
    "registry-location", () =>
    {
        Check(
            RegistryLocation.Parse("Computer\\hkcu\\Software\\").Address == "HKEY_CURRENT_USER\\Software",
            "Path aliases and Computer prefix");
        Check(RegistryLocation.Parse("\\\\SERVER\\hklm\\Software").Machine == "SERVER", "Remote address");
        Check(
            RegistryLocation.Parse("\\\\SERVER\\hklm\\Software").ToString() ==
            "\\\\SERVER\\HKEY_LOCAL_MACHINE\\Software", "Accessible registry location description");
        Check(RegistryLocation.Parse("HKCU\\Software").Parent!.SubKey == string.Empty, "Parent navigation");
        Check(RegistryLocation.Parse("HKCU").Parent is null, "Hive has no registry-key parent");
        Throws<ArgumentException>(() => RegistryLocation.Parse("UNKNOWN\\Key"), "Reject unknown hives");
        Throws<ArgumentException>(() => RegistryLocation.Parse("\\\\server\\HKCU"), "Reject unsupported remote hive");
        Throws<ArgumentException>(() => RegistryLocation.Parse("HKCU\\Bad\0Key"), "Reject null path");
        Throws<ArgumentException>(() => RegistryLocation.Parse("HKCU").Child("a\\b"), "Reject nested key name");
    });

Add("registry-path", () => RegistryPathTests.Run(Check, Throws<ArgumentException>));
Add("launch", () => LaunchTests.Run(Check));
Add("jump-list", () => JumpListTests.Run(Check));
AddAsync("jump-list-service", () => JumpListServiceTests.RunAsync(Check));
Add("jump-list-native", () => JumpListNativeTests.Run(Check));
Add("middle-click", () => MiddleClickTests.Run(Check));
Add("registry-entry-icon", () => RegistryEntryIconTests.Run(Check));
AddAsync("registry-link", () => RegistryLinkTests.RunAsync(Check, args.Contains("--registry")));
Add(
    "browser-titles", () =>
    {
        Throws<ArgumentException>(() => RegistryService.ValidateValueName("bad\0value"), "Reject null value name");
        Check(
            new BrowserViewModel(new RegistryService(), RegistryLocation.Parse("HKLM\\Software")).Title == "Software",
            "Local tab titles use the key name");
        Check(
            new BrowserViewModel(new RegistryService(), RegistryLocation.Parse("\\\\SERVER01\\HKLM\\Software")).Title ==
            "SERVER01 · Software", "Remote tab titles identify the computer before loading");
        Check(
            new BrowserViewModel(new RegistryService(), RegistryLocation.Parse("\\\\SERVER02\\HKLM\\Software")).Title ==
            "SERVER02 · Software", "Identical keys on different computers have distinct titles");
        Check(
            new BrowserViewModel(new RegistryService(), RegistryLocation.Parse("\\\\SERVER01\\HKU")).Title ==
            "SERVER01 · HKEY_USERS", "Remote hive titles identify the computer");
    });

Add(
    "favorite-identity", () =>
    {
        var favorite = new Favorite("Friendly name", "hkcu\\software\\Example");
        Check(
            favorite.Matches(RegistryLocation.Parse("HKEY_CURRENT_USER\\SOFTWARE\\example")),
            "Favorites match canonical paths independent of case and display name");
        Check(
            !favorite.Matches(RegistryLocation.Parse("HKCU\\Software\\Example", RegistryView.Registry32)),
            "Favorites distinguish registry views");
        Check(
            !favorite.Matches(RegistryLocation.Parse("HKCU\\Software\\Example\\Child")),
            "Favorite identity excludes descendants");
        Check(
            !favorite.Matches(RegistryLocation.Parse("HKLM\\Software\\Example")),
            "Favorites distinguish registry hives");
        var remoteFavorite = new Favorite("Remote", "\\\\SERVER\\HKLM\\Software", true);
        Check(
            remoteFavorite.Matches(
                RegistryLocation.Parse("\\\\server\\HKEY_LOCAL_MACHINE\\software", RegistryView.Registry32)),
            "Remote favorites match case-insensitively");
        Check(
            !remoteFavorite.Matches(RegistryLocation.Parse("HKLM\\Software", RegistryView.Registry32)),
            "Favorites distinguish local and remote keys");
        Check(
            !remoteFavorite.Matches(RegistryLocation.Parse("\\\\OTHER\\HKLM\\Software", RegistryView.Registry32)),
            "Favorites distinguish remote computers");
    });

Add("favorite-folder", () => FavoriteFolderTests.Run(Check, Throws<ArgumentException>));
Add("tool-presentation", () => ToolPresentationTests.Run(Check, Throws<ArgumentException>));
AddAsync("settings-view-model", () => SettingsViewModelTests.RunAsync(Check));
Add("monospace-font", () => MonospaceFontTests.Run(Check));
Add("web-search", () => WebSearchTests.Run(Check));
Add("journal-persistence", () => JournalPersistenceTests.Run(Check, args.Contains("--registry")));
AddAsync("journal-lock", () => JournalLockTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("journal-history", () => JournalHistoryTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("journal-archive", () => JournalArchiveTests.RunAsync(Check, args.Contains("--registry")));
if (args.Contains("--registry"))
{
    AddAsync("journal-revert", () => JournalRevertTests.RunAsync(Check));
}

Add("settings", () => SettingsTests.Run(Check));
AddAsync("settings-concurrency", () => SettingsConcurrencyTests.RunAsync(Check));
Add("journal-security", () => JournalSecurityTests.Run(Check));
Add(
    "history", () =>
    {
        var home = RegistryLocation.Parse("HKCU");
        var history = new NavigationHistory(home);
        history.Navigate(home.Child("A"));
        history.Navigate(home.Child("B"));
        Check(history.Back().Name == "A" && history.CanGoForward, "Back navigation");
        history.Navigate(home.Child("C"));
        Check(!history.CanGoForward, "New navigation removes forward history");
        history.Navigate(home.Child("C"));
        Check(history.Back().Name == "A", "Duplicate navigation creates no history entry");
        Check(
            history.ForwardTarget.Name == "C" && history.Current.Name == "A",
            "Peeking history does not move the current entry");
    });

Add(
    "value-codec", () =>
    {
        foreach (var type in Enum.GetValues<RegistryValueType>().Append((RegistryValueType)99)
                     .Append((RegistryValueType)uint.MaxValue))
        {
            var text = type switch
            {
                RegistryValueType.String or RegistryValueType.ExpandString => "Příliš žluťoučký 🐈 %TEMP%",
                RegistryValueType.MultiString => "first\r\nčeský",
                RegistryValueType.DWord or RegistryValueType.DWordBigEndian => "FFFFFFFF",
                RegistryValueType.QWord => "FFFFFFFFFFFFFFFF",
                _ => "00 12 ab ff"
            };
            var bytes = RegistryValue.ParseData(type, text);
            var value = new RegistryValue("name", type, bytes);
            Check(
                bytes.AsSpan().SequenceEqual(RegistryValue.ParseData(type, value.EditText(true))),
                $"Codec round trip for type {type}");
        }

        Check(
            new RegistryValue("Unknown", (RegistryValueType)uint.MaxValue, []).TypeName == "REG_TYPE_4294967295",
            "Unknown type names retain the complete unsigned native code");
        Check(
            new RegistryValue(
                string.Empty, RegistryValueType.DWord,
                RegistryValue.ParseData(RegistryValueType.DWord, "4294967295", false)).EditText(true) == "FFFFFFFF",
            "Unsigned DWORD max");
        Check(
            new RegistryValue(
                string.Empty, RegistryValueType.QWord,
                RegistryValue.ParseData(RegistryValueType.QWord, "18446744073709551615", false)).EditText(true) ==
            "FFFFFFFFFFFFFFFF", "Unsigned QWORD max");
        Throws<OverflowException>(
            () => RegistryValue.ParseData(RegistryValueType.DWord, "100000000"), "Reject DWORD overflow");
        Throws<FormatException>(
            () => RegistryValue.ParseData(RegistryValueType.Binary, "0G"), "Reject invalid hexadecimal bytes");
        Throws<FormatException>(
            () => RegistryValue.ParseData(RegistryValueType.Binary, "ABC"), "Reject odd byte count");
        Check(
            RegistryValue.ParseData(RegistryValueType.MultiString, "one\rtwo").SequenceEqual(
                RegistryValue.ParseData(RegistryValueType.MultiString, "one\r\ntwo")) &&
            RegistryValue.ParseData(RegistryValueType.MultiString, "one\rtwo")
                .SequenceEqual(System.Text.Encoding.Unicode.GetBytes("one\0two\0\0")),
            "WinUI carriage returns and CRLF encode separate REG_MULTI_SZ elements");
        Throws<FormatException>(
            () => RegistryValue.ParseData(RegistryValueType.MultiString, "one\n\ntwo"),
            "Reject embedded empty multi-string lines");
        Check(
            RegistryValue.ParseData(RegistryValueType.MultiString, string.Empty)
                .SequenceEqual(new byte[] { 0, 0, 0, 0 }), "Empty multi-string terminator");
        Check(Marshal.SizeOf<SecurityObjectInfo>() == (IntPtr.Size == 8 ? 56 : 36), "ACLUI structure layout");
    });

if (args.Contains("--registry"))
{
    AddAsync(
        "registry-integration", async () =>
        {
            var home = RegistryLocation.Parse("HKCU");
            var service = new RegistryService();
            var fixture = home.Child("Software").Child("RegeditModern.Tests." + Guid.NewGuid().ToString("N"));
            var parent = fixture.Parent!;
            var export = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".reg");
            Console.WriteLine("Temporary fixture: " + fixture.Address);
            try
            {
                service.CreateKey(parent, fixture.Name);
                RegistryKeyDetailsTests.Run(service, fixture, Check);
                foreach (var type in new RegistryValueType[]
                         {
                             RegistryValueType.None, RegistryValueType.String, RegistryValueType.ExpandString,
                             RegistryValueType.Binary, RegistryValueType.DWord, RegistryValueType.DWordBigEndian,
                             RegistryValueType.Link, RegistryValueType.MultiString, RegistryValueType.ResourceList,
                             RegistryValueType.FullResourceDescriptor, RegistryValueType.ResourceRequirementsList,
                             RegistryValueType.QWord, (RegistryValueType)99
                         })
                {
                    var text = type switch
                    {
                        RegistryValueType.String or RegistryValueType.ExpandString => "Test 🐈 %PATH%",
                        RegistryValueType.MultiString => "one\r\ntwo",
                        RegistryValueType.DWord or RegistryValueType.DWordBigEndian
                            or RegistryValueType.QWord => "FFFFFFFF",
                        _ => "0012ABFF"
                    };
                    var value = new RegistryValue("value" + type, type, RegistryValue.ParseData(type, text));
                    service.Write(fixture, value, null);
                    Check(
                        value.SameData(service.ReadValue(fixture, value.Name)),
                        $"Native byte and type round trip {type}");
                }

                SearchContinuationTests.Run(service, fixture, Check);
                var original = new RegistryValue(
                    "quoted\\\"name", RegistryValueType.String,
                    RegistryValue.ParseData(RegistryValueType.String, "Unicode český 🐈"));
                service.Write(fixture, original, null);
                var unchanged = service.ReadValue(fixture, original.Name)!;
                Throws<IOException>(
                    () => service.Write(fixture, original, null), "Creating existing value does not overwrite");
                var changed = original with { Data = RegistryValue.ParseData(RegistryValueType.String, "changed") };
                service.Write(fixture, changed, original);
                Throws<IOException>(() => service.Write(fixture, original, original), "Stale write rejected");
                Throws<IOException>(() => service.DeleteValue(fixture, unchanged), "Stale delete rejected");
                service.RenameValue(fixture, changed, "Renamed");
                Check(
                    service.ReadValue(fixture, original.Name) is null &&
                    changed.SameData(service.ReadValue(fixture, "Renamed")), "Rename preserves bytes");
                Throws<IOException>(
                    () => service.RenameValue(
                        fixture, service.ReadValue(fixture, "Renamed")!, $"value{RegistryValueType.String}"),
                    "Rename collision rejected");
                service.CreateKey(fixture, "Child");
                service.Write(
                    fixture.Child("Child"), new RegistryValue("needle", RegistryValueType.Binary, [1, 2, 3]), null);
                Throws<IOException>(() => service.CreateKey(fixture, "Child"), "Existing key creation rejected");
                service.RenameKey(fixture.Child("Child"), "RenamedChild");
                Check(service.SubKeys(fixture).Contains("RenamedChild"), "Native key rename");
                var hit = service.FindNext(fixture, new SearchOptions("needle"), null, default).Hit;
                Check(hit?.ValueName == "needle" && hit.Location.Name == "RenamedChild", "Recursive search");
                Check(
                    service.FindNext(fixture, new SearchOptions("needle"), hit, default).Hit is null,
                    "Find next skips previous match");
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                Throws<OperationCanceledException>(
                    () => service.FindNext(fixture, new SearchOptions("not found"), null, canceled.Token),
                    "Cancelable search");
                service.CopyKey(fixture.Child("RenamedChild"), fixture, "Copy");
                Check(
                    service.ReadValue(fixture.Child("Copy"), "needle")!.Data.SequenceEqual(new byte[] { 1, 2, 3 }),
                    "Recursive copy");
                Throws<InvalidOperationException>(
                    () => service.CopyKey(fixture, fixture, "Loop"), "Reject copying key into itself");
                Throws<InvalidOperationException>(
                    () => service.CopyKey(home, fixture, "RootLoop"), "Reject copying a root hive into itself");
                Throws<InvalidOperationException>(
                    () => service.CopyKey(fixture, fixture with { View = RegistryView.Registry32 }, "ViewLoop"),
                    "Reject copies across overlapping registry views");
                var before = service.Read(fixture).Values;
                service.Write(
                    fixture,
                    new RegistryValue(
                        "quoted\\\"name", RegistryValueType.String,
                        RegistryValue.ParseData(RegistryValueType.String, "first\nsecond 🐈")), null);
                service.Write(
                    fixture,
                    new RegistryValue(
                        "long binary", RegistryValueType.Binary,
                        Enumerable.Range(0, 400).Select(i => (byte)i).ToArray()), null);
                service.Write(fixture, new RegistryValue("nonterminated", RegistryValueType.String, [65, 0, 66]), null);
                service.Write(fixture, new RegistryValue("malformed number", RegistryValueType.DWord, [1, 2]), null);
                service.Write(
                    fixture,
                    new RegistryValue(
                        string.Empty, RegistryValueType.String,
                        RegistryValue.ParseData(RegistryValueType.String, "Default 🐈")), null);
                var wrapped = Enumerable.Range(0, 49).Select(i => (byte)i).ToArray();
                service.Write(fixture, new RegistryValue("wrapped export", RegistryValueType.Binary, wrapped), null);
                service.Write(fixture, new RegistryValue("empty export", RegistryValueType.Binary, []), null);
                before = service.Read(fixture).Values;
                Check(
                    service.FindNext(fixture, new SearchOptions("0102", false, false, true, Whole: true), null, default)
                        .Hit?.ValueName == "malformed number", "Search preserves malformed numeric data");
                await RegistryExport.ExportAsync(service, [fixture], export);
                var exportedText = File.ReadAllText(export);
                Check(exportedText.Contains("Windows Registry Editor Version 5.00"), "Export header");
                var expectedLines = string.Join(
                    ",\\\r\n  ",
                    new[] { wrapped[..19], wrapped[19..44], wrapped[44..] }
                        .Select(chunk => string.Join(",", chunk.Select(b => b.ToString("x2")))));
                Check(
                    exportedText.Contains("\"wrapped export\"=hex:" + expectedLines + "\r\n") &&
                    exportedText.Contains("\"empty export\"=hex:\r\n") &&
                    File.ReadAllBytes(export).AsSpan().StartsWith(System.Text.Encoding.Unicode.GetPreamble()),
                    "Export preserves the UTF-16 BOM, empty data, lowercase bytes, Regedit-compatible wrapping and CRLF");
                service.DeleteKey(fixture);
                await RegistryService.RunRegAsync("import", export, "/reg:64");
                Check(
                    before.All(value => value.SameData(service.ReadValue(fixture, value.Name))),
                    "Native import preserves every exported type and byte");
                Check(service.ReadValue(fixture.Child("Copy"), "needle") is not null, "Export preserves subkeys");
                var report = RegistryReport.Create(service, [fixture], true);
                Check(
                    report.Contains(fixture.Child("Copy").Address) && report.Contains("needle") &&
                    report.Contains("REG_QWORD"), "Printable branch report includes nested values and types");
                Check(
                    !RegistryReport.Create(service, [fixture], false).Contains("needle"),
                    "Current-key print range excludes children");
                using var security = new RegistrySecurityInformation(fixture);
                var unknown = new StrategyBasedComWrappers().GetOrCreateComInterfaceForObject(
                    security, CreateComInterfaceFlags.None);
                try
                {
                    var iid = typeof(ISecurityInformation).GUID;
                    var hr = Marshal.QueryInterface(unknown, in iid, out var pointer);
                    Check(hr == 0 && pointer != 0, "Generated permissions callback exposes the native ACLUI interface");
                    if (pointer != 0)
                    {
                        Marshal.Release(pointer);
                    }
                }
                finally
                {
                    Marshal.Release(unknown);
                }

                Check(
                    security.GetSecurity(7, out var descriptor, 0) == 0 && descriptor != 0, "Read security descriptor");
                if (descriptor != 0)
                {
                    Check(
                        security.SetSecurity(4, descriptor) == 0,
                        "Round trip fixture permissions without changing them");
                    LocalFree(descriptor);
                }

                service.DeleteValue(fixture, service.ReadValue(fixture, "Renamed")!);
                Check(service.ReadValue(fixture, "Renamed") is null, "Delete value");
                File.WriteAllText(export, "existing backup");
                try
                {
                    await RegistryExport.ExportAsync(service, [fixture.Child("Missing")], export);
                }
                catch (IOException)
                {
                }

                Check(File.ReadAllText(export) == "existing backup", "Failed export preserves existing destination");
                service.Write(
                    fixture,
                    new RegistryValue(
                        "line\nbreak", RegistryValueType.String,
                        RegistryValue.ParseData(RegistryValueType.String, "data")), null);
                try
                {
                    await RegistryExport.ExportAsync(service, [fixture], export);
                }
                catch (IOException)
                {
                }

                Check(File.ReadAllText(export) == "existing backup", "Unrepresentable names do not overwrite backup");
                service.CreateKey(fixture, "HistoryA");
                service.CreateKey(fixture, "HistoryB");
                var viewModel = new BrowserViewModel(service, fixture);
                await viewModel.NavigateAsync(fixture.Child("HistoryA"));
                Check(!viewModel.HasSubKeys, "An empty key has no child navigation");
                await viewModel.NavigateAsync(fixture.Child("HistoryB"));
                service.DeleteKey(fixture.Child("HistoryA"));
                await viewModel.BackCommand.ExecuteAsync(null);
                Check(
                    viewModel.Location.Name == "HistoryB" && viewModel.Error is not null && !viewModel.CanGoForward,
                    "Failed navigation leaves history and location intact");
                service.CreateKey(fixture, "HistoryA");
                await viewModel.BackCommand.ExecuteAsync(null);
                Check(
                    viewModel.Location.Name == "HistoryA" && viewModel.CanGoForward,
                    "Back works after failed target is restored");
                await viewModel.ForwardCommand.ExecuteAsync(null);
                Check(viewModel.Location.Name == "HistoryB", "Forward command updates the tab");
                var firstNavigation = viewModel.NavigateAsync(fixture);
                var lastNavigation = viewModel.NavigateAsync(fixture.Child("Copy"));
                await Task.WhenAll(firstNavigation, lastNavigation);
                Check(viewModel.Location.Name == "Copy", "Latest navigation wins");
                viewModel.Filter = "needle";
                await viewModel.PendingFilter;
                Check(viewModel.Entries.Count == 1 && viewModel.Entries[0].Name == "needle", "Live filtering");
                viewModel.SelectedEntry = viewModel.Entries[0];
                viewModel.Filter = "no match";
                await viewModel.PendingFilter;
                Check(
                    viewModel.Entries.Count == 0 && viewModel.SelectedEntry is null,
                    "Filtering clears hidden selection");
                await viewModel.NavigateAsync(fixture);
                Check(
                    viewModel.Filter.Length == 0 && viewModel.HasSubKeys && viewModel.Entries.All(item => !item.IsKey),
                    "Navigation clears filtering and values-only preserves child navigation");
                viewModel.ShowKeysInList = true;
                Check(viewModel.Entries.Any(item => item.IsKey), "Combined list shows child keys");
                viewModel.SelectedEntry = viewModel.Entries.First(item => item.IsKey);
                viewModel.ShowKeysInList = false;
                Check(
                    viewModel.SelectedEntry is null && viewModel.Entries.All(item => !item.IsKey),
                    "Hiding keys clears key selection");
                viewModel.Filter = "value";
                await viewModel.RefreshCommand.ExecuteAsync(null);
                Check(viewModel.Filter == "value", "Refreshing the current key preserves its filter");
                await viewModel.NavigateAsync(fixture.Child("HistoryB"));
                service.CreateKey(viewModel.Location, "Child");
                await viewModel.RefreshCommand.ExecuteAsync(null);
                Check(viewModel.HasSubKeys, "Refresh detects children added to a leaf");
                service.DeleteKey(viewModel.Location.Child("Child"));
                await viewModel.RefreshCommand.ExecuteAsync(null);
                Check(!viewModel.HasSubKeys, "Refresh detects the last child being removed");
            }
            finally
            {
                try
                {
                    using var cleanup = Registry.CurrentUser.OpenSubKey("Software", true);
                    cleanup!.DeleteSubKeyTree(fixture.Name, false);
                }
                finally
                {
                    if (File.Exists(export))
                    {
                        File.Delete(export);
                    }
                }
            }
        });
}

if (args.Contains("--registry"))
{
    AddAsync("registry-path-registry", () => RegistryPathTests.RunRegistryAsync(Check));
}

AddAsync("browser-edit", () => BrowserEditTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("duplicate-value", () => DuplicateValueTests.RunAsync(Check, args.Contains("--registry")));
Add("value-editor-input", () => ValueEditorInputTests.Run(Check));
AddAsync("undo-redo", () => UndoRedoTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("history-broadcaster", () => RegistryHistoryBroadcasterTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("inline-rename", () => InlineRenameTests.RunAsync(Check, args.Contains("--registry")));

AddAsync("computer-root", () => ComputerRootTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("hive-switch", () => HiveSwitchTests.RunAsync(Check, args.Contains("--registry")));
AddAsync(
    "navigation-history",
    () => NavigationHistoryTests.RunAsync(Check, Throws<ArgumentOutOfRangeException>, args.Contains("--registry")));
AddAsync("parent-key-row", () => ParentKeyRowTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("registry-list-columns", () => RegistryListColumnsTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("watch-surface", () => WatchSurfaceTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("tool-page-design", () => ToolPageDesignTests.RunAsync(Check));
AddAsync("watch-delivery", () => WatchDeliveryTests.RunAsync(Check));
AddAsync("branch-watch", () => BranchWatchTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("snapshot", () => SnapshotTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("search-lifecycle", () => SearchLifecycleTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("workspace-restore", () => WorkspaceRestoreTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("investigation", () => InvestigationTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("tool-state", () => ToolStateTests.RunAsync(Check, args.Contains("--registry")));
AddAsync("runner-self-test", () => TestSuiteRunnerTests.RunAsync(Check));

try
{
    var defaults = suites.Where(suite =>
        suite.Name is not "runner-self-test" and not "jump-list-native" and not "network-registry-native" &&
        (suite.Name != "browser-edit" || args.Contains("--registry")));
    if (args.Contains("--browser-edits"))
    {
        defaults = suites.Where(suite => suite.Name == "browser-edit");
    }
    else if (args.Contains("--settings"))
    {
        defaults = suites.Where(suite => suite.Name is "settings" or "settings-concurrency");
    }
    else if (args.Contains("--logging"))
    {
        defaults = suites.Where(suite => suite.Name == "logging");
    }

    if (args.Contains("--list-suites") && !args.Any(arg => arg is "--browser-edits" or "--settings" or "--logging"))
    {
        defaults = suites;
    }

    var selected = TestSuiteRunner.Select(suites, defaults, args);
    if (args.Contains("--list-suites"))
    {
        foreach (var suite in selected)
        {
            Console.WriteLine(suite.Name);
        }
    }
    else
    {
        Environment.ExitCode = await TestSuiteRunner.RunAsync(selected, Console.Out);
        Console.WriteLine($"{passed} checks passed.");
    }
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 2;
}

[DllImport("kernel32.dll", ExactSpelling = true)]
static extern nint LocalFree(nint memory);
