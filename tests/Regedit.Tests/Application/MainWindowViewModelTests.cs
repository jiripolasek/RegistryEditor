// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class MainWindowViewModelTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var settings = new AppSettings
        {
            Tabs = [new SavedTab("HKCU"), new SavedTab("InvalidHive"), new SavedTab("HKLM", true)]
        };
        var registry = new RegistryService();
        var shell = new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry,
            location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(settings, () => throw new IOException("Test note save failure")));
        shell.Initialize(null);
        var first = (BrowserTab)shell.Tabs[0];
        var second = (BrowserTab)shell.Tabs[1];
        check(
            shell.Tabs.Count == 2 && shell.ActiveTab == second &&
            second.Browser.Location.View == RegistryView.Registry32,
            "Restore skips an invalid address, preserves registry views and selects the last restored tab");
        shell.OpenSettingsCommand.Execute(null);
        var settingsTab = shell.ActiveTab;
        shell.OpenShortcutsCommand.Execute(null);
        check(
            shell.ActiveTab == settingsTab && shell.Tabs.Count == 3 && shell.VisibleBrowser is null &&
            shell.ContextBrowser == second.Browser,
            "Settings and keyboard shortcuts reuse one tab and retain the last browser context");

        shell.OpenInBackground(RegistryLocation.Parse("HKCR"));
        var background = (BrowserTab)shell.Tabs[^1];
        check(
            shell.ActiveTab == settingsTab && shell.ContextBrowser == second.Browser,
            "Opening a background tab from a tool leaves its selection and registry context alone");
        shell.Tabs.Move(shell.Tabs.IndexOf(background), 0);
        check(
            shell.CaptureWorkspace("Reordered").Tabs.SequenceEqual(
                new[] { background.Browser.Location, first.Browser.Location, second.Browser.Location }),
            "Workspace capture follows the reordered tab strip");
        shell.NextTabCommand.Execute(null);
        check(
            shell.ActiveTab == background && shell.VisibleBrowser == background.Browser,
            "Next tab wraps in display order");
        shell.PreviousTabCommand.Execute(null);
        check(shell.ActiveTab == settingsTab, "Previous tab wraps back to Settings");
        List<WindowTab> closed = [];
        shell.TabClosed += closed.Add;
        shell.CloseTab(background);
        check(
            shell.ContextBrowser == second.Browser && shell.ActiveTab == settingsTab,
            "Closing a background context restores the most recently visited surviving browser");
        shell.CloseTab(second);
        shell.CloseTab(first);

        check(
            closed.SequenceEqual(new[] { background, second, first }) && shell.Tabs.Count == 1 &&
            shell.ActiveTab == settingsTab && shell.ContextBrowser is null && shell.VisibleBrowser is null,
            "Only closed tabs are canceled and closing the final browser clears the tool context");
        var eligibilityChanges = 0;
        shell.DisconnectCommand.CanExecuteChanged += (_, _) => eligibilityChanges++;
        shell.OpenBrowser(RegistryLocation.Parse(@"\\server\HKLM"));
        check(
            eligibilityChanges > 0 && shell.DisconnectCommand.CanExecute(null),
            "Switching to a remote registry enables Disconnect before the menu is opened");
        shell.CloseCurrentTabCommand.Execute(null);
        check(
            eligibilityChanges > 1 && !shell.DisconnectCommand.CanExecute(null),
            "Closing the last remote tab invalidates Disconnect while Settings stays active");

        var closeRequests = 0;
        shell.CloseRequested += () => closeRequests++;
        shell.CloseCurrentTabCommand.Execute(null);
        check(
            closeRequests == 1 && shell.ActiveTab == settingsTab && closed.Count == 4,
            "Last-tab close requests window confirmation before removing or canceling the tab");
        shell.OpenToolCommand.Execute(ToolKind.Favorites);
        var favoriteTab = shell.ActiveTab;
        shell.OpenToolCommand.Execute(ToolKind.Favorites);
        check(
            shell.ActiveTab == favoriteTab && shell.Tabs.Count == 2 && shell.ContextBrowser is null,
            "Favorites can be opened and reused with zero browser tabs");
        var favorite = RegistryLocation.Parse("HKCU\\Software", RegistryView.Registry32);
        await shell.NavigateFromToolsAsync(favorite, false);
        check(
            shell.VisibleBrowser?.Location == favorite && shell.Tabs.Count == 3,
            "Navigating a favorite from a tool opens its exact target in a visible browser");

        var note = shell.GetNote(favorite);
        note.Text = "Keep my draft";
        var active = shell.ActiveTab;
        var reply = new TaskCompletionSource<NoteCloseChoice>();
        var pendingClose = shell.TryCloseAsync(_ => reply.Task);
        shell.CloseCurrentTabCommand.Execute(null);
        var duplicate = await shell.TryCloseAsync(_ => throw new InvalidOperationException("Duplicate close prompt"));
        reply.SetResult(NoteCloseChoice.Cancel);
        check(
            !duplicate && !await pendingClose && shell.ActiveTab == active && shell.Tabs.Count == 3 && note.IsDirty &&
            note.Text == "Keep my draft" && !shell.IsClosing,
            "Repeated close requests and Cancel preserve the active tab and dirty draft");
        var beginPrompt = new TaskCompletionSource();
        var promptShown = new TaskCompletionSource();
        var dismiss = new TaskCompletionSource();
        var coordinatedClose = shell.TryCloseAsync(async _ =>
        {
            await beginPrompt.Task;
            check(shell.TryBeginDialog(true), "The closing window can present its own confirmation");
            promptShown.SetResult();
            try
            {
                await dismiss.Task;
                return NoteCloseChoice.Cancel;
            }
            finally
            {
                shell.EndDialog();
            }
        });
        check(
            shell.IsClosing && !shell.IsDialogOpen && !shell.TryBeginDialog(),
            "An unrelated prompt is rejected while close is pending before any dialog opens");
        var otherSettings = new AppSettings();
        var otherWindow = new MainWindowViewModel(
            otherSettings, new InvestigationStore(otherSettings, () => { }), registry,
            location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(otherSettings));
        check(
            otherWindow.TryBeginDialog() && otherWindow.IsDialogOpen && !shell.IsDialogOpen,
            "A second window owns an independent dialog gate while the first window closes");
        otherWindow.EndDialog();
        beginPrompt.SetResult();
        await promptShown.Task;
        check(
            shell.IsDialogOpen && !shell.TryBeginDialog() && !shell.TryBeginDialog(true),
            "An active close confirmation rejects every overlapping dialog");
        dismiss.SetResult();
        check(
            !await coordinatedClose && !shell.IsClosing && shell.TryBeginDialog(),
            "Cancel releases closing state and allows an ordinary dialog to retry");
        shell.EndDialog();
        shell.OpenSettingsCommand.Execute(null);
        check(ReferenceEquals(shell.GetNote(favorite), note), "A draft survives switching away from its browser");

        var tabSettings = new AppSettings();
        var tabShell = new MainWindowViewModel(
            tabSettings, new InvestigationStore(tabSettings, () => { }), registry,
            location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(tabSettings, () => throw new IOException("Test note save failure")));
        var source = tabShell.OpenBrowser(RegistryLocation.Parse(@"\\server\HKLM\Software", RegistryView.Registry32));
        tabShell.OpenSettingsCommand.Execute(null);
        var tail = tabShell.OpenBrowser(RegistryLocation.Parse("HKCU"));
        tabShell.DuplicateTab(source);
        var copied = (BrowserTab)tabShell.Tabs[1];
        check(
            tabShell.Tabs.Count == 4 && tabShell.ActiveTab == copied && copied != source &&
            copied.Browser != source.Browser && copied.Browser.Location == source.Browser.Location,
            "Duplicating a background tab inserts an independent browser beside its source with the same machine and registry view");
        tabShell.OpenBottomTool(ToolKind.Journal);
        var panelTab = tabShell.ActiveBottomTab;
        var draft = tabShell.GetNote(source.Browser.Location);
        draft.Text = "Keep this draft after closing tabs";
        var tabCloses = new List<WindowTab>();
        tabShell.TabClosed += tabCloses.Add;
        var tabCloseRequests = 0;
        tabShell.CloseRequested += () => tabCloseRequests++;
        tabShell.Tabs.Move(tabShell.Tabs.IndexOf(tail), 0);
        var toRight = tabShell.Tabs.Skip(2).ToArray();
        tabShell.CloseTabsToRight(source);
        check(
            tabShell.Tabs.SequenceEqual(new[] { tail, source }) && tabCloses.SequenceEqual(toRight) &&
            tabShell.ActiveTab == source && tabShell.BottomTabs.SequenceEqual(new[] { panelTab! }),
            "Close tabs to the right follows the reordered strip, releases each closed tab and preserves the bottom panel");
        tabShell.CloseTabsToRight(source);
        tabShell.DuplicateTab(copied);
        tabShell.CloseOtherTabs(copied);
        tabShell.CloseTabsToRight(copied);
        check(
            tabShell.Tabs.SequenceEqual(new[] { tail, source }) && tabCloses.Count == toRight.Length,
            "Tab actions ignore a removed target and closing to the right of the last tab does nothing");

        var tabReply = new TaskCompletionSource<NoteCloseChoice>();
        var closingTabs = tabShell.TryCloseAsync(_ => tabReply.Task);
        tabShell.DuplicateTab(source);
        tabShell.CloseOtherTabs(source);
        tabShell.CloseTabsToRight(tail);
        check(
            tabShell.IsClosing && tabShell.Tabs.SequenceEqual(new[] { tail, source }),
            "Context menu tab actions leave tabs intact while a window close confirmation is pending");
        tabReply.SetResult(NoteCloseChoice.Cancel);
        await closingTabs;
        tabShell.CloseOtherTabs(tail);
        tabShell.CloseOtherTabs(tail);
        check(
            tabShell.Tabs.SequenceEqual(new[] { tail }) && tabShell.ActiveTab == tail && tabCloseRequests == 0 &&
            draft.IsDirty && draft.Text == "Keep this draft after closing tabs" && tabShell.ActiveBottomTab == panelTab,
            "Close other tabs retains its background target, dirty note drafts and panel tabs without requesting window close");
        tabShell.WindowClosed();
    }
}
