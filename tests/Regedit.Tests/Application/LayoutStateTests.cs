// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class LayoutStateTests
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditLayout.");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(
                file,
                "{\"Tabs\":[{\"Address\":\"HKCU\"}],\"ToolPanePinned\":true,\"ToolPaneWidth\":420,\"SelectedTool\":\"Notes\"}");
            var legacy = AppSettings.Load(file);
            var restored = Shell(legacy);
            restored.Initialize(null);
            check(
                restored.IsPaneOpen && restored.IsPanePinned && restored.PaneWidth == 420 &&
                restored.SelectedTool == ToolKind.Notes && restored.Placement is null,
                "Legacy tool preferences initialize a window without requiring saved layout");
            var browser = ((BrowserTab)restored.Tabs[0]).Browser;
            check(
                browser.Layout == new BrowserLayout() && browser.SortColumn == RegistrySortColumn.Name &&
                !browser.SortDescending, "Legacy tabs retain the existing default widths and ascending Name sort");
            browser.ResizeColumn(RegistrySortColumn.Name, 250, 60);
            browser.ResizeColumn(RegistrySortColumn.Type, 170, -20);
            browser.ResizeColumn(RegistrySortColumn.Data, 350, 50);
            browser.SetTreeLayout(480, true);
            browser.SortCommand.Execute(RegistrySortColumn.Type);
            browser.SortCommand.Execute(RegistrySortColumn.Type);
            var measured = new WindowPlacement(
                new WindowBounds(-1720, 100, 1200, 800), new WindowBounds(-1920, 0, 1920, 1040), 1.5, true);
            restored.CapturePlacement = () => measured;
            restored.ResizePane(40);
            restored.IsPaneOpen = false;
            restored.IsPanePinned = false;
            restored.SelectedTool = ToolKind.Journal;
            restored.SaveLayout(file);
            var loaded = AppSettings.Load(file);
            check(
                loaded.LoadError is null &&
                loaded.RestoredLayout is
                    { PaneOpen: false, PanePinned: false, PaneWidth: 460, SelectedTool: "Journal" } &&
                loaded.RestoredLayout.Placement == measured,
                "Saving the window samples its current normal bounds, maximized state, work area and scale");
            check(
                loaded.ToolPanePinned && loaded.ToolPaneWidth == 420 && loaded.SelectedTool == "Notes",
                "Window layout does not replace another window's legacy tool defaults");
            var reopened = Shell(loaded);
            reopened.Initialize(null);
            var reopenedBrowser = ((BrowserTab)reopened.Tabs[0]).Browser;
            check(
                reopenedBrowser.Layout is
                    { NameWidth: 310, TypeWidth: 150, DataWidth: 400, TreeWidth: 480, TreeHidden: true } &&
                reopenedBrowser.SortColumn == RegistrySortColumn.Type && reopenedBrowser.SortDescending &&
                !reopened.IsPaneOpen && !reopened.IsPanePinned && reopened.SelectedTool == ToolKind.Journal,
                "A restored tab retains independent column widths, tree layout and typed sort state");
            var explicitLaunch = Shell(loaded);
            explicitLaunch.Initialize(RegistryLocation.Parse("HKLM"));
            check(
                explicitLaunch.Tabs.Count == 1 && explicitLaunch.IsPanePinned && explicitLaunch.PaneWidth == 420 &&
                explicitLaunch.SelectedTool == ToolKind.Notes && explicitLaunch.Placement is null,
                "An explicit launch opens its requested key with legacy defaults instead of consuming restored window layout");
            reopened.OpenBrowser(RegistryLocation.Parse("HKCR"));
            check(
                ((BrowserTab)reopened.Tabs[1]).Browser.Layout == new BrowserLayout(),
                "A new tab starts with its own column and tree preferences");
            reopened.ResizePane(20);
            reopened.IsPaneOpen = true;
            reopened.SaveLayout(file);
            loaded = AppSettings.Load(file);
            check(
                loaded.Tabs.Count == 2 && loaded.Tabs[0].Layout!.TreeHidden && !loaded.Tabs[1].Layout!.TreeHidden,
                "Saving a window preserves distinct layouts for every browser tab");
            var before = File.ReadAllBytes(file);
            var previousTabs = loaded.Tabs;
            try
            {
                loaded.SaveSession(
                    [new SavedTab("HKLM")], true, Path.Combine(file, "cannot-save.json"),
                    new WindowLayout(PaneWidth: 600));
                check(false, "The storage failure must be reported");
            }
            catch (IOException)
            {
                check(
                    ReferenceEquals(previousTabs, loaded.Tabs) && before.SequenceEqual(File.ReadAllBytes(file)),
                    "A failed layout save restores local tabs and leaves the existing session file intact");
            }

            var invalid = new BrowserLayout(-10, double.PositiveInfinity, 1e10, double.NaN, SortColumn: "unknown")
                .Normalize();
            check(
                invalid is { NameWidth: 0, TypeWidth: 170, DataWidth: 1600, TreeWidth: 290, SortColumn: "Name" },
                "Unusable browser layout values fall back or clamp without discarding valid saved tabs");
            check(
                new WindowLayout(
                        new WindowPlacement(new WindowBounds(0, 0, -1, 100), new WindowBounds(0, 0, 1920, 1040)),
                        PaneWidth: -1, SelectedTool: "unknown").Normalize() is
                    { Placement: null, PaneWidth: 300, SelectedTool: "Favorites" },
                "Invalid placement is ignored while bounded tool defaults remain usable");
            var normal = new WindowPlacement(new WindowBounds(100, 100, 1000, 700), new WindowBounds(0, 0, 1920, 1040));
            check(
                normal.Restore(new WindowBounds(0, 0, 2560, 1400), 1.5) == new WindowBounds(150, 150, 1500, 1050),
                "Placement scales both normal size and relative work-area offset on the restored display");
            check(
                normal.Restore(new WindowBounds(-1366, -768, 1366, 728), 1.5) ==
                new WindowBounds(-1366, -768, 1366, 728),
                "A removed monitor and smaller work area clamp the entire scaled window to visible negative coordinates");
            foreach (var firstClosesFirst in new[] { true, false })
            {
                var concurrentFile = Path.Combine(directory.FullName, firstClosesFirst + ".json");
                var first = AppSettings.Load(concurrentFile);
                var second = AppSettings.Load(concurrentFile);
                var firstLayout = new WindowLayout(normal, true, true, 400, "Notes");
                var secondLayout = new WindowLayout(normal with { Maximized = true }, false, false, 600, "Watches");

                void SaveFirst()
                {
                    first.SaveSession(
                        [new SavedTab("HKCU", Layout: new BrowserLayout(333))], false, concurrentFile, firstLayout);
                }

                void SaveSecond()
                {
                    second.SaveSession(
                        [new SavedTab("HKLM", Layout: new BrowserLayout(444))], false, concurrentFile, secondLayout);
                }

                if (firstClosesFirst)
                {
                    SaveFirst();
                    SaveSecond();
                }
                else
                {
                    SaveSecond();
                    SaveFirst();
                }

                SaveFirst();
                SaveSecond();
                using var json = JsonDocument.Parse(File.ReadAllText(concurrentFile));
                var sessions = json.RootElement.GetProperty("WindowSessions").EnumerateArray().ToArray();
                check(
                    sessions.Length == 2 &&
                    sessions.Single(item => item.GetProperty("Tabs")[0].GetProperty("Address").GetString() == "HKCU")
                        .GetProperty("Layout").GetProperty("PaneWidth").GetDouble() == 400 &&
                    sessions.Single(item => item.GetProperty("Tabs")[0].GetProperty("Address").GetString() == "HKLM")
                        .GetProperty("Layout").GetProperty("PaneWidth").GetDouble() == 600 &&
                    AppSettings.Load(concurrentFile).Tabs.Count == 2,
                    "Concurrent windows retain their own layout and tab session in closing order: " + firstClosesFirst);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static MainWindowViewModel Shell(AppSettings settings)
    {
        var registry = new RegistryService();
        return new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry,
            location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(settings));
    }
}
