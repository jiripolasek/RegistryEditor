// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class UxMvpTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var location = RegistryLocation.Parse("HKCU");

        static RegistryValue Text(string path, RegistryValueType type = RegistryValueType.String)
        {
            return new("Path", type, Encoding.Unicode.GetBytes(path + "\0"));
        }

        var value = Text(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        check(
            RegistryFileReveal.Resolve(location, Text(@"%SystemRoot%\explorer.exe", RegistryValueType.ExpandString))
            == Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "explorer.exe"), "Reveal expands REG_EXPAND_SZ paths");
        foreach (var rejected in new[]
                 {
                     Text(@"relative\file"), Text(@"\\server\share\file"), Text("\"C:\\app.exe --argument"), Text("\"\" --argument"),
                     Text("C:\\file\0other"), new RegistryValue("Number", RegistryValueType.DWord, [1, 0, 0, 0]),
                     new RegistryValue("Broken", RegistryValueType.String, [0, 216])
                 })
        {
            check(RegistryFileReveal.Resolve(location, rejected) is null, "Reveal rejects unsupported or malformed path: " + rejected.Name);
            check(!RegistryFileReveal.CanResolve(location, rejected), "Reveal availability rejects unsupported or malformed paths");
        }

        check(
            RegistryFileReveal.Resolve(RegistryLocation.Parse(@"\\server\HKLM"), value) is null,
            "Remote values cannot reveal a local file");
        check(
            !RegistryFileReveal.CanResolve(RegistryLocation.Parse(@"\\server\HKLM"), value),
            "Remote values do not offer local file reveal");
        var file = Path.Combine(Path.GetTempPath(), "Regedit-reveal-" + Guid.NewGuid().ToString("N") + ", space.exe");
        var shorter = file[..file.LastIndexOf(' ')];
        try
        {
            await File.WriteAllTextAsync(file, "fixture");
            await File.WriteAllTextAsync(shorter, "shorter fixture");
            check(RegistryFileReveal.Resolve(location, Text(file)) == file, "Reveal preserves spaces and commas in an existing path");
            foreach (var arguments in new[] { string.Empty, " --argument", " \"%1\"" })
            {
                var command = Text("\"" + file + "\"" + arguments);
                check(
                    RegistryFileReveal.Resolve(location, command) == file,
                    "Reveal accepts a quoted existing path followed by arguments: " + arguments);
                check(RegistryFileReveal.CanResolve(location, command), "Reveal availability accepts a quoted command path: " + arguments);
            }

            check(
                RegistryFileReveal.Resolve(location, Text(file + " --argument other text")) == file,
                "Reveal chooses the longest existing unquoted path prefix");
            check(
                RegistryFileReveal.Resolve(location, Text("\"" + file + " missing\"")) is null,
                "A missing quoted path cannot fall back to an unquoted prefix");
            check(
                RegistryFileReveal.Resolve(location, Text("\"" + file + " missing\" \"%1\"")) is null,
                "A missing quoted command path cannot fall back to an existing prefix");
            var rundll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
            check(
                RegistryFileReveal.Resolve(location, Text(rundll + " shell32.dll,Control_RunDLL")) == rundll,
                "Reveal recognizes an unquoted rundll32 command without executing it");
            var start = RegistryFileReveal.CreateStartInfo(file);
            check(
                Path.GetFileName(start.FileName) == "explorer.exe" && start.Arguments == "/select,\"" + file + "\"",
                "Reveal selects a file in Explorer without executing it");
            var directory = Path.GetDirectoryName(file)!;
            check(RegistryFileReveal.CreateStartInfo(directory).Arguments == "\"" + directory + "\"", "Reveal opens directories directly");
            check(RegistryFileReveal.Resolve(location, Text(directory)) == directory, "Reveal accepts existing directories");
        }
        finally
        {
            File.Delete(file);
            File.Delete(shorter);
        }

        check(RegistryFileReveal.Resolve(location, Text(file)) is null, "Missing paths do not resolve to a reveal target");
        check(RegistryFileReveal.CanResolve(location, Text(file)), "Reveal availability parses a path without requiring it to exist");
        try
        {
            RegistryFileReveal.CreateStartInfo(file);
            check(false, "Missing reveal target must fail");
        }
        catch (FileNotFoundException)
        {
            check(true, "Missing reveal target reports an error");
        }

        var registry = new RegistryService();
        var browser = new BrowserViewModel(registry, location);
        check(browser.IsInspectorOpen, "The value inspector is open by default in a new browser tab");
        var modified = new DateTimeOffset(2026, 11, 16, 10, 20, 30, TimeSpan.Zero);
        var first = RegistryEntry.ForValue(value, modified, location);
        var second = new RegistryEntry(null, Text(new string('x', 5000)));
        var dialogs = new List<ValueInspectorViewModel>();
        browser.InspectorRequested += inspector =>
        {
            dialogs.Add(inspector);
            return Task.CompletedTask;
        };
        browser.IsInspectorOpen = false;
        browser.SetSelection([first]);
        await browser.InspectCommand.ExecuteAsync(null);
        check(
            !browser.IsInspectorOpen
            && browser.Inspector is null
            && dialogs.Count == 1
            && dialogs[0].Name == "Path"
            && dialogs[0].CanRevealFile, "Inspect opens a dialog with the selected value when the detail panel is closed");
        check(
            dialogs[0].HasLastWriteTime
            && dialogs[0].LastWriteText == modified.ToLocalTime().ToString("G")
            && dialogs[0].LastWriteHelp.Contains("containing key, shared by all its values", StringComparison.Ordinal),
            "Dialog inspection shows the containing key's local last-write time and explains its scope");
        browser.IsInspectorOpen = true;
        check(
            browser.Inspector?.Name == "Path" && browser.Inspector.CanRevealFile,
            "Opening the detail panel inspects the current selection");
        check(
            browser.Inspector is { HasLastWriteTime: true } && browser.Inspector.LastWriteText == dialogs[0].LastWriteText,
            "The inspector pane shares the dialog's modification metadata");
        await browser.InspectCommand.ExecuteAsync(null);
        check(
            browser.IsInspectorOpen && browser.Inspector?.Name == "Path" && dialogs.Count == 1,
            "Inspect uses the open detail panel without opening a dialog");

        var originalInspector = browser.Inspector;
        var inspectorUpdates = 0;
        browser.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(browser.Inspector))
            {
                inspectorUpdates++;
            }
        };
        var previewUpdates = new List<Task>();
        for (var index = 0; index < 20; index++)
        {
            browser.SetSelection([index % 2 == 0 ? first : second]);
            previewUpdates.Add(browser.PendingPreview);
        }

        check(
            ReferenceEquals(browser.SelectedEntry, second)
            && browser.EditValueCommand.CanExecute(null)
            && !browser.IsPreviewCurrent
            && ReferenceEquals(browser.Inspector, originalInspector)
            && inspectorUpdates == 0, "Rapid navigation updates selection and commands immediately without rebuilding the stale preview");
        await Task.WhenAll(previewUpdates);
        check(
            browser.IsPreviewCurrent && browser.Inspector?.Data.Length == 5000 && inspectorUpdates == 1,
            "Rapid navigation publishes only the final selected value after the preview settles");
        browser.SetSelection([first]);
        var explicitPreview = browser.PendingPreview;
        await browser.InspectCommand.ExecuteAsync(null);
        await explicitPreview;
        check(
            browser.IsPreviewCurrent && browser.Inspector?.Data == first.Data && inspectorUpdates == 2,
            "Explicit Inspect refreshes immediately and cancels the delayed preview");

        browser.SetSelection([second]);
        await browser.PendingPreview;
        check(
            browser.Inspector?.Data.Length == 5000 && !browser.Inspector.CanRevealFile,
            "Inspector follows selection and retains full value contents");
        check(
            browser.Inspector is { HasLastWriteTime: false } && !new ValueInspectorViewModel(value).HasLastWriteTime,
            "Inspection without key metadata hides the timestamp instead of inventing a value update time");
        browser.SetSelection([first, second]);
        check(
            browser.IsInspectorOpen && browser.Inspector is null, "Multiple selection clears inspector contents without closing the pane");
        var key = location.Child("Preview key");
        browser.SetSelection([new RegistryEntry(key, null)]);
        await browser.PendingPreview;
        check(
            browser.Inspector is { Name: "Preview key", Heading: "Key", HasNavigationIcon: true, HasStoredValue: false }
            && browser.Inspector.KeyLocation == key
            && browser.Inspector.KeyPath == key.Address
            && browser.Inspector.HasKeyPath
            && !browser.Inspector.HasText
            && !browser.Inspector.InterpretCommand.CanExecute(null),
            "Key selection shows its own name path and navigation icon without byte controls");
        check(!browser.InspectCommand.CanExecute(null), "Inspect is unavailable for a key");
        browser.SetSelection([new RegistryEntry(null, null, IsDefaultPlaceholder: true)]);
        await browser.PendingPreview;
        check(
            browser.Inspector is
            {
                Name: "(Default)", Heading: "REG_SZ · Value not set", Data: "(value not set)",
                HasText: true, HasStoredValue: false, HasKeyPath: false
            }
            && browser.Inspector.Glyph == "\uE8EC"
            && !browser.Inspector.InterpretCommand.CanExecute(null)
            && !browser.InspectCommand.CanExecute(null), "An unset default value has an explicit preview without inventing a stored value");
        browser.Inspector!.InterpretCommand.Execute(null);
        check(browser.Inspector.Result.StartsWith("Choose a format"), "An unset value cannot be interpreted programmatically");
        browser.SetSelection([new RegistryEntry(null, new RegistryValue(string.Empty, RegistryValueType.String, []))]);
        await browser.PendingPreview;
        check(
            browser.Inspector is { Name: "(Default)", Heading: "REG_SZ · 0 bytes", Data: "", HasStoredValue: true }
            && browser.Inspector.InterpretCommand.CanExecute(null)
            && browser.InspectCommand.CanExecute(null), "A stored empty default remains distinct from the unset placeholder");
        browser.SetSelection([new RegistryEntry(null, null, ParentLocation: RegistryLocation.Parse("Computer"))]);
        await browser.PendingPreview;
        check(
            browser.Inspector is { Heading: "Parent key", KeyPath: "Computer", HasStoredValue: false }
            && browser.Inspector.KeyLocation!.IsComputer,
            "The virtual parent previews its destination without pretending to contain value bytes");
        browser.SetSelection([]);
        check(
            browser.Inspector is { HasNavigationIcon: true, HasStoredValue: false }
            && browser.Inspector.KeyLocation == location
            && browser.Inspector.KeyPath == location.Address, "Without a list selection the inspector previews the current tree key");
        var overview = new BrowserViewModel(registry, RegistryLocation.Parse("Computer")) { IsInspectorOpen = true };
        check(overview.Inspector is null, "The Computer overview without a selected entry keeps its selection prompt");
        browser.SetSelection([first]);
        var closingPreview = browser.PendingPreview;
        browser.IsInspectorOpen = false;
        await closingPreview;
        check(
            browser.Inspector is null && browser.IsPreviewCurrent,
            "Closing the inspector releases its contents and cancels the delayed update");
        browser.SetSelection([second]);
        check(!browser.IsPreviewCurrent, "The inline preview also waits for selection to settle");
        await browser.PendingPreview;
        check(
            browser.IsPreviewCurrent && browser.Inspector is null,
            "The inline preview becomes current without constructing a hidden inspector");
        await browser.InspectCommand.ExecuteAsync(null);
        check(
            !browser.IsInspectorOpen && dialogs.Count == 2 && dialogs[1].Data.Length == 5000,
            "Inspect after closing the detail panel opens a dialog with full current contents");
        check(dialogs[0].Data == first.Data, "Dialog inspection retains its original value after browser selection changes");

        var closingBrowser = new BrowserViewModel(registry, location) { IsInspectorOpen = true };
        var lastInspector = closingBrowser.Inspector;
        closingBrowser.SetSelection([second]);
        var canceledPreview = closingBrowser.PendingPreview;
        closingBrowser.CancelPending();
        await canceledPreview;
        check(
            ReferenceEquals(closingBrowser.Inspector, lastInspector),
            "Closing a browser prevents its delayed preview from publishing into the closed view");

        browser.SetSelection([new RegistryEntry(null, Text(file))]);
        Exception? revealFailure = null;
        browser.FileRevealFailed += error => revealFailure = error;
        check(browser.RevealFileCommand.CanExecute(null), "A syntactically valid path offers reveal without a filesystem probe");
        await browser.RevealFileCommand.ExecuteAsync(null);
        check(
            revealFailure is FileNotFoundException && browser.Error == revealFailure.Message,
            "Invoking reveal checks the missing path on a worker and reports the failure");
        browser.CancelPending();
        overview.CancelPending();

        var settings = new AppSettings();
        var shell = new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry, target => new BrowserViewModel(registry, target),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        shell.Initialize(location);
        check(!shell.ReopenClosedTabCommand.CanExecute(null), "Reopen is disabled before any registry tab closes");
        var closed = shell.OpenBrowser(RegistryLocation.Parse(@"\\server\HKLM\Software", RegistryView.Registry32));
        closed.Browser.RestoreLayout(new BrowserLayout(TreeWidth: 432, SortColumn: "Data", SortDescending: true));
        closed.Browser.SetSelection([new RegistryEntry(null, new RegistryValue("", RegistryValueType.String, []))]);
        shell.CloseTab(closed);
        shell.ReopenClosedTabCommand.Execute(null);
        var restored = (BrowserTab)shell.ActiveTab!;
        check(
            restored != closed
            && restored.Browser != closed.Browser
            && restored.Browser.Location == closed.Browser.Location
            && restored.Browser.Layout.TreeWidth == 432
            && restored.Browser.Layout.SortColumn == "Data"
            && restored.InitialValue == "",
            "Reopen recreates the browser with its computer, registry view, layout and default-value selection");
        check(!shell.ReopenClosedTabCommand.CanExecute(null), "Reopening consumes closed-tab history");
        for (var index = 0; index < 25; index++)
        {
            var tab = shell.OpenBrowser(location.Child(index.ToString()));
            shell.CloseTab(tab);
        }

        var reopened = 0;
        while (shell.ReopenClosedTabCommand.CanExecute(null))
        {
            shell.ReopenClosedTabCommand.Execute(null);
            check(
                ((BrowserTab)shell.ActiveTab!).Browser.Location.Name == (24 - reopened).ToString(),
                "Closed tabs reopen in reverse close order");
            reopened++;
        }

        check(reopened == 20, "Closed-tab history is bounded");
        var a = shell.OpenBrowser(location.Child("A"));
        var c = shell.OpenBrowser(location.Child("C"));
        shell.CloseTabsToRight(restored);
        shell.ReopenClosedTabCommand.Execute(null);
        check(((BrowserTab)shell.ActiveTab!).Browser.Location == c.Browser.Location, "Bulk-close actions also populate reopen history");
        shell.WindowClosed();
    }
}
