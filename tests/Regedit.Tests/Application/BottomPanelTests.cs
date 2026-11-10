// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class BottomPanelTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var settings = new AppSettings();
        var store = new InvestigationStore(settings, () => { });
        var registry = new RegistryService();
        var shell = new MainWindowViewModel(
            settings, store, registry, location => new BrowserViewModel(registry, location),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        var origin = shell.OpenBrowser(RegistryLocation.Parse("HKCU\\Software"));
        var query = new SavedSearch("First", [origin.Browser.Location], new SearchOptions("First"));

        SearchResultsViewModel Create(SavedSearch search)
        {
            return new(
                search, store, async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return new RegistrySearchResults([], 0, false);
                }, (_, _) => { });
        }

        var first = Create(query);
        var second = Create(query with { Name = "Second", Options = new SearchOptions("Second") });
        shell.SelectBottomTab(first);
        var firstRun = first.RunCommand.ExecuteAsync(null);
        shell.SelectBottomTab(second);
        var secondRun = second.RunCommand.ExecuteAsync(null);
        shell.OpenToolCommand.Execute(ToolKind.Journal);
        shell.OpenToolCommand.Execute(ToolKind.Watches);
        shell.OpenToolCommand.Execute(ToolKind.Journal);
        check(
            shell.BottomTabs.Count == 4 && shell.BottomTabs.OfType<ToolTab>().Count() == 2 && shell.ActiveTab == origin,
            "Search result sets and singleton Journal/Watches tabs share one bottom panel above the same browser");
        shell.SelectBottomTab(first);
        shell.OpenBrowser(RegistryLocation.Parse("HKLM"));
        shell.IsBottomPaneOpen = false;
        check(
            shell.ActiveBottomTab == first && first.IsBusy && second.IsBusy && shell.BottomTabs.Count == 4,
            "Changing registry tabs and hiding the shared panel preserve every search and scan");
        shell.ShowBottomPane();
        check(
            shell.IsBottomPaneOpen && shell.ActiveBottomTab == first && first.IsBusy && second.IsBusy &&
            shell.BottomTabs.Count == 4,
            "The toolbar reopens the existing bottom panel without changing its selection or restarting scans");
        shell.SelectBottomTab(first);
        shell.PromoteBottomTab();
        check(
            shell.ActiveTab == first && shell.Tabs.Contains(first) && !shell.BottomTabs.Contains(first) && first.IsBusy,
            "Promoting a search transfers its live instance without cancellation");
        shell.DockTab(first);
        check(
            shell.ActiveBottomTab == first && !shell.Tabs.Contains(first) && shell.VisibleBrowser is not null &&
            first.IsBusy, "Docking restores the same search instance beside a visible browser");
        check(
            shell.CaptureWorkspace("Both").Searches.Length == 2,
            "Workspace capture includes every bottom search result set");
        shell.CloseBottomTab(first);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(3));
        check(
            first.RunCommand.IsCancellationRequested && second.IsBusy && shell.IsBottomPaneOpen,
            "Closing one bottom search cancels only its own scan");
        shell.CloseBottomTab(second);
        await secondRun.WaitAsync(TimeSpan.FromSeconds(3));
        foreach (var tab in shell.BottomTabs.ToArray())
        {
            shell.CloseBottomTab(tab);
        }

        check(
            shell.ActiveBottomTab is null && !shell.IsBottomPaneOpen && shell.VisibleBrowser is not null,
            "Closing the final panel tab hides the panel and keeps registry tabs open");
        shell.ShowBottomPane();
        check(
            shell.IsBottomPaneOpen && shell.ActiveBottomTab is ToolTab { Tool: ToolKind.Journal } &&
            shell.BottomTabs.Count == 1, "Showing an empty bottom panel opens its default journal tab");
        shell.CloseBottomTab(shell.ActiveBottomTab!);
        shell.ResizeBottomPane(-10000, 900);
        check(shell.BottomPaneHeight == 180, "Bottom panel height has a usable lower bound");
        shell.ResizeBottomPane(10000, 900);
        check(shell.BottomPaneHeight == 600, "Bottom panel height has a bounded maximum");
        shell.ResizeBottomPane(200, 700);
        check(shell.BottomPaneHeight == 460, "Dragging beyond the window limit stores the visible panel height");
        shell.ResizeBottomPane(-20, 700);
        check(shell.BottomPaneHeight == 440, "Reversing a clamped resize responds immediately");
        shell.ResizeBottomPane(-20, 540);
        check(
            shell.BottomPaneHeight == 280,
            "Resizing clamps the stored height before applying a delta after the window shrinks");
        shell.ResizeBottomPane(1000, 320);
        check(shell.BottomPaneHeight == 100, "Small windows clamp the panel without exceeding the available height");
        shell.ResizeBottomPane(40, 900);
        shell.ResizeBottomPane(20, 900);
        check(shell.BottomPaneHeight == 240, "Successive bottom-panel moves accumulate without a layout pass");
        var previewWidth = 285d;
        previewWidth = PaneResize.Apply(previewWidth, 24, 220, 800);
        previewWidth = PaneResize.Apply(previewWidth, 24, 220, 800);
        check(previewWidth == 333, "Repeated preview resize keys accumulate from the stored width");
        check(
            PaneResize.Apply(previewWidth, -24, 220, 300) == 276,
            "Preview resizing clamps to the current available width before applying a move");
        var previewHeight = PaneResize.Apply(240, 30, 100, 500);
        check(
            PaneResize.Apply(previewHeight, 40, 100, 500) == 310,
            "Preview drag deltas accumulate from the stored height");
        check(
            PaneResize.Apply(600, -20, 180, 460) == 440,
            "A resize reverses immediately after the available space shrinks");
        check(
            new WindowLayout(BottomPaneHeight: double.NaN, BottomTool: "invalid").Normalize() is
                { BottomPaneHeight: 240, BottomTool: null },
            "Invalid persisted bottom panel settings fall back safely");
        var directory = Directory.CreateTempSubdirectory("RegeditBottomPanel.");
        try
        {
            shell.ResizeBottomPane(10, 700);
            shell.OpenBottomTool(ToolKind.Journal);
            var path = Path.Combine(directory.FullName, "settings.json");
            shell.SaveLayout(path);
            var loaded = AppSettings.Load(path);
            var reopened = new MainWindowViewModel(
                loaded, new InvestigationStore(loaded, () => { }), registry,
                location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
                NotesStoreTests.CreateStore(loaded));
            reopened.Initialize(null);
            check(
                reopened.BottomPaneHeight == 250 && reopened.IsBottomPaneOpen &&
                reopened.ActiveBottomTab is ToolTab { Tool: ToolKind.Journal },
                "Window restore retains bottom panel height, visibility and selected singleton tool");
            reopened.WindowClosed();
        }
        finally
        {
            directory.Delete(true);
        }

        shell.WindowClosed();
    }
}
