// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class SearchResultNavigationTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var settings = new AppSettings();
        var store = new InvestigationStore(settings, () => { });
        var registry = new RegistryService();
        var root = RegistryLocation.Parse("HKCU");

        SearchResultsViewModel Create(string name)
        {
            return new(
                new SavedSearch(name, [root], new SearchOptions(name)), store,
                (_, _) => Task.FromResult(new RegistrySearchResults([], 0, false)), (_, _) => { });
        }

        var first = Create("First");
        var second = Create("Second");
        var rows = new[] { "Alpha", "Beta", "Gamma" }.Select(name => new InvestigationRow(
            new RegistrySearchMatch(
                root, name, new RegistryValue(name, RegistryValueType.String, []), "Value name", "match"))).ToArray();
        first.Rows.ReplaceAll(rows);
        var revealed = new List<InvestigationRow>();
        first.ResultSelected += revealed.Add;
        check(
            first.SelectNext()?.ValueName == "Alpha" && first.Selected is [var selected] && selected == rows[0] &&
            revealed.SequenceEqual([rows[0]]), "F3 selects the first result and requests that its row be revealed");
        check(first.SelectNext()?.ValueName == "Beta", "F3 advances from the selected result");
        first.Selected = [rows[0], rows[1]];
        check(first.SelectNext()?.ValueName == "Gamma", "F3 advances after the last selected row");
        check(
            first.SelectNext() is null && first.Selected.Single() == rows[2] && revealed.Count == 3,
            "F3 stops at the end without wrapping or clearing the result selection");
        first.Rows.Add(
            new InvestigationRow(
                new RegistrySearchMatch(
                    root, "Delta", new RegistryValue("Delta", RegistryValueType.String, []), "Value name", "match")));
        check(first.SelectNext()?.ValueName == "Delta", "F3 can advance into results appended by an ongoing scan");
        first.Filter = "Beta";
        check(
            first.SelectNext()?.ValueName == "Beta" && first.DisplayRows.Count == 1,
            "F3 follows the displayed result filter and starts again when selection is filtered out");
        first.Filter = "absent";
        check(first.SelectNext() is null, "F3 handles an empty filtered result set");
        first.Filter = string.Empty;
        first.Selected = [];
        first.Close();
        check(first.SelectNext() is null, "A closed result set cannot advance its selection");

        var shell = new MainWindowViewModel(
            settings, store, registry, target => new BrowserViewModel(registry, target),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        try
        {
            var browser = shell.OpenBrowser(root);
            var focusRequests = 0;
            shell.SearchFocusRequested += () => focusRequests++;
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                focusRequests == 1 && !shell.IsBottomPaneOpen,
                "Without a result set F3 requests search focus and keeps the bottom panel hidden");
            var active = Create("Active");
            shell.SelectBottomTab(active);
            shell.SelectBottomTab(second);
            shell.IsBottomPaneOpen = false;
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                browser.Browser.Status.StartsWith("Find: Second") && !shell.IsBottomPaneOpen,
                "F3 follows the selected result set without revealing a hidden panel");
            shell.OpenToolCommand.Execute(ToolKind.Journal);
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                browser.Browser.Status.StartsWith("Find: Second") && shell.ActiveBottomTab is ToolTab,
                "Selecting Journal retains the last active search without switching the panel tab");
            shell.SelectBottomTab(active);
            shell.PromoteBottomTab();
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                shell.ActiveTab == browser && browser.Browser.Status.StartsWith("Find: Active"),
                "Selecting a promoted result set makes it active for F3 and reuses the current browser");
            shell.CloseTab(active);
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                browser.Browser.Status.StartsWith("Find: Second"),
                "Closing the active result set makes F3 follow a remaining result set");
            shell.CloseBottomTab(second);
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(focusRequests == 2, "Closing the final result set makes F3 request search focus");
        }
        finally
        {
            shell.WindowClosed();
        }

        if (registryTests)
        {
            await RunRegistryAsync(check);
        }
    }

    private static async Task RunRegistryAsync(Action<bool, string> check)
    {
        var registry = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.F3Tests." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(root.Parent!, root.Name);
        registry.Write(root, new RegistryValue(string.Empty, RegistryValueType.Binary, [1]), null);
        registry.Write(root, new RegistryValue("Named", RegistryValueType.Binary, [2]), null);
        var settings = new AppSettings();
        var store = new InvestigationStore(settings, () => { });
        var shell = new MainWindowViewModel(
            settings, store, registry, target => new BrowserViewModel(registry, target),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        try
        {
            var original = shell.OpenBrowser(root.Parent!);
            var source = new SearchResultsViewModel(
                new SavedSearch("Fixture", [root], new SearchOptions("match")), store,
                (_, _) => throw new InvalidOperationException("F3 must not restart the registry scan"), (_, _) => { });
            var unnamed = new RegistrySearchMatch(
                root, string.Empty, new RegistryValue(string.Empty, RegistryValueType.Binary, [1]), "Data", "one");
            var named = new RegistrySearchMatch(
                root with { View = RegistryView.Registry32 }, "Named",
                new RegistryValue("Named", RegistryValueType.Binary, [2]), "Data", "two");
            source.Rows.ReplaceAll([new InvestigationRow(unnamed), new InvestigationRow(named)]);
            shell.SelectBottomTab(source);
            shell.IsBottomPaneOpen = false;
            var notices = 0;
            original.Browser.Search.Notice += (_, _, _) => notices++;
            var originalValues = new List<string>();
            original.Browser.ValueSelectionRequested += originalValues.Add;
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                shell.ActiveTab == original && original.Browser.Location == root && originalValues is [""] &&
                source.Selected.Single() == source.Rows[0] && !shell.IsBottomPaneOpen && notices == 0,
                "F3 navigates the current browser to the exact unnamed value and updates selection with the panel hidden");
            var other = shell.OpenBrowser(root.Parent!);
            var otherValues = new List<string>();
            other.Browser.ValueSelectionRequested += otherValues.Add;
            other.Browser.Search.Notice += (_, _, _) => notices++;
            var tabCount = shell.Tabs.Count;
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                shell.ActiveTab == other && shell.Tabs.Count == tabCount && other.Browser.Location == named.Location &&
                otherValues is ["Named"] && source.Selected.Single() == source.Rows[1] && !shell.IsBottomPaneOpen &&
                other.Browser.Status == "Find: match · 2/2" && notices == 0,
                "A new browser tab continues the active result set in its original registry view without opening another tab");
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                otherValues.Count == 1 && other.Browser.Status.EndsWith("No more results.") &&
                !shell.IsBottomPaneOpen && notices == 0,
                "F3 at the end keeps the browser and selection stable and reports completion only in status");
            shell.SelectBottomTab(source);
            source.Selected = [];
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                shell.IsBottomPaneOpen && shell.ActiveBottomTab == source && otherValues[^1] == string.Empty,
                "F3 also keeps an already visible panel visible");
            shell.TryBeginDialog();
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(source.Selected.Single() == source.Rows[0], "F3 does not navigate behind an open dialog");
            shell.EndDialog();
            var missing = named with { Location = root.Child("Missing") };
            source.Rows.ReplaceAll([new InvestigationRow(missing)]);
            source.Selected = [];
            await shell.FindNextResultCommand.ExecuteAsync(null);
            check(
                notices == 1 && ReferenceEquals(source.Selected.Single().Source, missing),
                "A stale result retains its selected row and reports a navigation error without starting a scan");
        }
        finally
        {
            shell.WindowClosed();
            using var parent = registry.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
        }
    }
}
