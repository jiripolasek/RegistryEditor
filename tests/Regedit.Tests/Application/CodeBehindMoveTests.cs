// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Security;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

/// <summary>Covers logic that used to live in view code-behind: copy actions, rows, suggestions and app menu.</summary>
internal static class CodeBehindMoveTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        ResultActions(check);
        Rows(check);
        RemoteProbe(check);
        await SuggestionsAsync(check);
        await ApplicationMenuAsync(check);
    }

    private static void RemoteProbe(Action<bool, string> check)
    {
        var target = ApplicationRegistryViewModel.RemoteRoot("SERVER01", RegistryView.Registry32);
        var opened = new List<RegistryLocation>();
        ApplicationRegistryViewModel.ProbeRemote(target, opened.Add);
        check(opened.SequenceEqual([target]), "A successful remote hive probe needs no fallback");

        opened.Clear();
        ApplicationRegistryViewModel.ProbeRemote(
            target, location =>
            {
                opened.Add(location);
                if (location.Hive == RegistryHive.LocalMachine)
                {
                    throw new SecurityException();
                }
            });
        check(
            opened.SequenceEqual([target, target with { Hive = RegistryHive.Users }]),
            "A connection can use HKEY_USERS when HKEY_LOCAL_MACHINE is denied, preserving the computer and registry view");

        opened.Clear();
        var denied = new UnauthorizedAccessException();
        try
        {
            ApplicationRegistryViewModel.ProbeRemote(
                target, location =>
                {
                    opened.Add(location);
                    throw denied;
                });
            check(false, "An inaccessible remote computer requires authentication");
        }
        catch (UnauthorizedAccessException ex) when (ReferenceEquals(ex, denied))
        {
            check(opened.Count == 2, "Access denied by both remote hives reaches the credential prompt");
        }

        foreach (var error in new[] { 1219, 1311, 1326, 1327 })
        {
            foreach (var hresult in new[] { error, unchecked((int)(0x80070000u | (uint)error)) })
            {
                opened.Clear();
                var logonFailure = new IOException("Registry sign-in failed", hresult);
                try
                {
                    ApplicationRegistryViewModel.ProbeRemote(
                        target, location =>
                        {
                            opened.Add(location);
                            throw logonFailure;
                        });
                    check(false, "Remote authentication failures propagate");
                }
                catch (IOException ex) when (ReferenceEquals(ex, logonFailure))
                {
                    check(
                        opened.SequenceEqual([target, target with { Hive = RegistryHive.Users }]),
                        $"Remote probe preserves authentication error 0x{hresult:X8} after trying both hives");
                }
            }
        }

        opened.Clear();
        var offline = new IOException("Offline");
        try
        {
            ApplicationRegistryViewModel.ProbeRemote(
                target, location =>
                {
                    opened.Add(location);
                    throw offline;
                });
            check(false, "Remote network failures propagate");
        }
        catch (IOException ex) when (ReferenceEquals(ex, offline))
        {
            check(opened.Count == 1, "A remote network failure is not retried against another hive");
        }
    }

    private static void ResultActions(Action<bool, string> check)
    {
        var location = RegistryLocation.Parse(@"HKCU\Software\Test");
        var time = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var first = new WatchObservation(
            new RegistryDifference(
                location, RegistryChangeKind.ValueAdded,
                After: new RegistryValue("A", RegistryValueType.DWord, [1, 0, 0, 0])), time);
        var second = new WatchObservation(
            new RegistryDifference(location.Child("Child"), RegistryChangeKind.KeyAdded), time.AddMinutes(1));
        var rows = new[] { InvestigationRow.Observation(first), InvestigationRow.Observation(second) };

        var copied = new List<string>();
        var notices = new List<(string Title, NoticeKind Kind)>();
        var actions = new ToolResultActionsViewModel(copied.Add);
        actions.Notice += (title, _, kind) => notices.Add((title, kind));
        check(
            !actions.CopyPowerShellCommand.CanExecute(null) && !actions.CopyPathsCommand.CanExecute(null),
            "Copy actions are disabled without a selection");

        actions.Select(rows);
        actions.CopyPowerShellCommand.Execute(null);
        actions.CopyRegExeCommand.Execute(null);
        check(
            copied.Count == 2 && copied[0] == RegistryScripts.PowerShell([first.Change, second.Change]) &&
            copied[1] == RegistryScripts.RegExe([first.Change, second.Change]) &&
            notices.All(notice => notice is ("Script copied", NoticeKind.Success)),
            "Script copying formats watch changes in display order and reports success");

        copied.Clear();
        actions.CopyPathsCommand.Execute(null);
        actions.CopyResultsCommand.Execute(null);
        check(
            copied[0] == location.Address + Environment.NewLine + location.Child("Child").Address &&
            copied[1].Split(Environment.NewLine) is [var line, _] &&
            line == $"{rows[0].TableSubject}\t{rows[0].Path}\t{rows[0].TableSummary}",
            "Path and table copies use the tool row subject, path and summary");

        actions.ReleaseCache();
        check(
            actions.Selected.Count == 2 && actions.HasChanges && actions.CopyPowerShellCommand.CanExecute(null),
            "Releasing the cache on unload keeps the selection for a view that is reparented");

        var empty = new JournalEntry(Guid.NewGuid(), time, "Activity", [], []);
        actions.Select([InvestigationRow.Journal(empty, "Completed")]);
        check(
            !actions.HasChanges && !actions.CopyPowerShellCommand.CanExecute(null) &&
            actions.CopyPathsCommand.CanExecute(null), "Journal entries without differences cannot produce a script");

        var failing = new ToolResultActionsViewModel(_ => throw new InvalidOperationException("Clipboard busy"));
        var failure = default((string Title, string Message, NoticeKind Kind));
        failing.Notice += (title, message, kind) => failure = (title, message, kind);
        failing.CopyExactBytesCommand.Execute(new RegistryValue("A", RegistryValueType.Binary, [0xAB, 0xCD]));
        check(
            failure == ("Could not copy exact bytes", "Clipboard busy", NoticeKind.Error),
            "Clipboard failures surface as error notices");

        var value = new RegistryValue("A", RegistryValueType.Binary, [0xAB, 0xCD]);
        check(
            ResultText.Hex(value) == "ABCD" && ResultText.BytesCopied(value) == "Copied 2 bytes as hexadecimal." &&
            ResultText.Paths([rows[0], rows[0]]) == location.Address,
            "Result text copies exact hex bytes and de-duplicates paths");
    }

    private static void Rows(Action<bool, string> check)
    {
        check(
            ToolPresentation.EmptyState(ToolKind.Notes, false) ==
            ("No notes yet", "Select a registry key to write your first note.") &&
            ToolPresentation.EmptyState(ToolKind.Journal, true).Title == "No matches",
            "Empty states depend on the tool and whether a search is active");
        var location = RegistryLocation.Parse(@"HKCU\Software", RegistryView.Registry32);
        check(
            ToolPresentation.Address(location) == location.Address + " · 32-bit" &&
            ToolPresentation.Address(null) == "Select a registry tab", "Tool addresses carry the registry view");

        var written = new NoteDraft(location, "Line one\nLine two");
        var blank = new NoteDraft(location.Child("Blank"), string.Empty);
        using var noteStore = NotesStoreTests.CreateStore(new AppSettings());
        var notes = new NotesViewModel(
            noteStore, (_, _) => Task.CompletedTask, _ => { }, false, _ => { }, _ => Task.FromResult(false));
        var visible = notes.VisibleRows([written, blank], string.Empty).ToArray();
        check(
            visible is [{ Source: NoteDraft only, Subtitle: "Line one Line two" }] && only == written,
            "Notes list only written notes, flattened to one line");
        check(
            !notes.VisibleRows([written], "missing").Any() && notes.VisibleRows([written], "TWO").Any(),
            "Note search matches row text case-insensitively");
        notes.Selected = blank;
        check(notes.VisibleRows([written, blank], string.Empty).Count() == 2, "The selected note stays listed");
    }

    private static async Task SuggestionsAsync(Action<bool, string> check)
    {
        var reads = new List<RegistryLocation>();
        var suggestions = new RegistryAddressSuggestions(location =>
        {
            reads.Add(location);
            return Enumerable.Range(0, 40).Select(index => $"Key{index:00}").Append("Other").ToArray();
        });
        var roots = await suggestions.SuggestAsync("hkc", RegistryView.Registry64, CancellationToken.None);
        var expected = RegistryLocation.Roots.Where(root =>
            root.Alias.StartsWith("hkc", StringComparison.OrdinalIgnoreCase) ||
            root.Name.StartsWith("hkc", StringComparison.OrdinalIgnoreCase)).Select(root => root.Name);
        check(
            roots.Select(item => item.Label).SequenceEqual(expected) &&
            roots.Any(item => item.Label == "HKEY_CURRENT_USER") && reads.Count == 0,
            "Root suggestions match aliases without reading the registry");

        var keys = await suggestions.SuggestAsync(
            @"Computer\HKCU\Software\key", RegistryView.Registry64, CancellationToken.None);
        check(
            keys.Count == RegistryAddressSuggestions.SuggestionLimit && keys[0].Label == "Key00" &&
            reads is [{ SubKey: "Software" }] &&
            keys[0].Address == RegistryLocation.Parse(@"HKCU\Software").Child("Key00").Address,
            "Subkey suggestions strip the Computer prefix, filter by prefix and stop at the limit");

        reads.Clear();
        var remote = await suggestions.SuggestAsync(
            @"\\SERVER\HKLM\Soft", RegistryView.Registry64, CancellationToken.None);
        check(remote.Count == 0 && reads.Count == 0, "Remote paths never query the network while typing");

        var current = RegistryLocation.Parse(@"HKCU\Software");
        var hives = await suggestions.ChildrenAsync(null, current, CancellationToken.None);
        check(
            hives.Count == current.GetComputer().GetHives().Length && reads.Count == 0,
            "Breadcrumb children of the computer are its hives");
    }

    private static async Task ApplicationMenuAsync(Action<bool, string> check)
    {
        var target = ApplicationRegistryViewModel.RemoteRoot(@" \\SERVER01\ ", RegistryView.Registry32);
        check(
            target.Machine == "SERVER01" && target.Hive == RegistryHive.LocalMachine &&
            target.View == RegistryView.Registry32,
            "Typed computer names resolve to the remote HKEY_LOCAL_MACHINE root");

        var directory = Directory.CreateTempSubdirectory("RegeditAppMenu.");
        try
        {
            var settings = new AppSettings();
            var registry = new RegistryService();
            var window = new MainWindowViewModel(
                settings, new InvestigationStore(settings, () => { }), registry,
                location => new BrowserViewModel(registry, location), NullLogger<MainWindowViewModel>.Instance,
                NotesStoreTests.CreateStore(settings));
            var journal = new JournalViewModel(
                new RegistryJournal(Path.Combine(directory.FullName, "journal")),
                NullLogger<JournalViewModel>.Instance);
            var dialogs = new MenuDialogs();
            var started = new List<ProcessStartInfo>();
            var standardStarts = new List<RegistryLocation>();
            var menu = new ApplicationRegistryViewModel(
                window, registry, journal, dialogs, new RegistryBranchCommands(registry), NullLogger.Instance,
                isAdministrator: false, started.Add, (_, location) => standardStarts.Add(location));
            var notices = new List<(string Title, string Message, NoticeKind Kind)>();
            menu.Notice += (title, message, kind) => notices.Add((title, message, kind));

            await menu.ImportCommand.ExecuteAsync(null);
            check(
                dialogs.Calls.SequenceEqual(["open"]) && notices.Count == 0,
                "Import without a tab targets this computer and stops quietly when no file is picked");

            var previews = new List<(string File, RegistryView View)>();
            menu.OpenFilePreviewAsync = (file, view) =>
            {
                previews.Add((file, view));
                return Task.CompletedTask;
            };
            dialogs.OpenFile = "preview.reg";
            dialogs.Calls.Clear();
            await menu.ImportCommand.ExecuteAsync(null);
            check(
                dialogs.Calls.SequenceEqual(["open"]) && previews is [("preview.reg", RegistryView.Registry64)] &&
                notices.Count == 0, "Import opens a file preview without confirming or writing to the registry");

            dialogs.Calls.Clear();
            await menu.ImportCommand.ExecuteAsync("dropped.REG");
            check(
                dialogs.Calls.Count == 0 && previews[^1] == ("dropped.REG", RegistryView.Registry64) &&
                notices.Count == 0,
                "Dropped registry files open the preview without a picker, including an uppercase extension");
            var previewCount = previews.Count;
            await menu.ImportCommand.ExecuteAsync("unsupported.txt");
            check(
                dialogs.Calls.Count == 0 && previews.Count == previewCount && notices is [{ Kind: NoticeKind.Error }],
                "Unsupported dropped files are rejected without opening a picker or preview");
            notices.Clear();

            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            menu.OpenFilePreviewAsync = (_, _) => opened.Task;
            var opening = menu.ImportCommand.ExecuteAsync("pending.reg");
            check(
                !opening.IsCompleted && menu.ImportCommand.IsRunning,
                "The import command remains active until its preview-opening task completes");
            opened.SetResult();
            await opening;
            check(
                !menu.ImportCommand.IsRunning && notices.Count == 0,
                "Completing preview opening finishes the command without an error notice");

            var failedOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            menu.OpenFilePreviewAsync = (_, _) => failedOpen.Task;
            opening = menu.ImportCommand.ExecuteAsync("failed.reg");
            failedOpen.SetException(new IOException("Preview initialization failed"));
            await opening;
            check(
                notices is [{ Kind: NoticeKind.Error, Message: "Preview initialization failed" }],
                "An asynchronous preview-opening failure is observed by the application command guard");
            notices.Clear();

            var canceledOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            menu.OpenFilePreviewAsync = (_, _) => canceledOpen.Task;
            opening = menu.ImportCommand.ExecuteAsync(null);
            canceledOpen.SetCanceled();
            await opening;
            check(
                notices is [{ Kind: NoticeKind.Information, Title: "Import canceled" }],
                "Canceled preview opening is observed and reported as cancellation");
            notices.Clear();
            dialogs.OpenFile = null;

            dialogs.Calls.Clear();
            await menu.ConnectCommand.ExecuteAsync(null);
            check(
                dialogs.Calls.SequenceEqual(["computer"]) && notices.Count == 0 && window.ContextBrowser is null,
                "Canceling the computer name dialog opens no tab");

            await menu.ElevateCommand.ExecuteAsync(null);
            check(started.Count == 1 && notices.Count == 0, "Elevation starts one elevated process");

            var elevated = new ApplicationRegistryViewModel(
                window, registry, journal, dialogs, new RegistryBranchCommands(registry), NullLogger.Instance,
                isAdministrator: true, started.Add, (_, location) => standardStarts.Add(location));
            check(!elevated.ElevateCommand.CanExecute(null), "Elevation is unavailable when already elevated");
            check(
                !menu.OpenStandardAccessCommand.CanExecute(null),
                "Standard access is unavailable when already unelevated");
            await menu.OpenStandardAccessCommand.ExecuteAsync(null);
            check(standardStarts.Count == 0, "Standard access cannot be launched from an unelevated window");

            window.OpenBrowser(target);
            await elevated.OpenStandardAccessCommand.ExecuteAsync(null);
            check(
                standardStarts.SequenceEqual([target]) && notices.Count == 0,
                "Standard access preserves the current computer, path and registry view");

            var failedLaunch = new ApplicationRegistryViewModel(
                window, registry, journal, dialogs, new RegistryBranchCommands(registry), NullLogger.Instance,
                isAdministrator: true,
                startStandardAccess: (_, _) => throw new InvalidOperationException("No desktop shell"));
            failedLaunch.Notice += (title, message, kind) => notices.Add((title, message, kind));
            await failedLaunch.OpenStandardAccessCommand.ExecuteAsync(null);
            check(
                notices is [{ Kind: NoticeKind.Error, Message: "No desktop shell" }],
                "Standard-access launch failures use the existing error notice");
            notices.Clear();
            await menu.ImportCommand.ExecuteAsync("remote.reg");
            check(
                notices is [{ Kind: NoticeKind.Error }] && previews.Count == previewCount,
                "Dropping a registry file in a remote context keeps the local-import restriction");
            notices.Clear();
            menu.OpenFilePreviewAsync = (file, view) =>
            {
                previews.Add((file, view));
                return Task.CompletedTask;
            };
            window.OpenBrowser(new RegistryLocation { View = RegistryView.Registry32 });
            await menu.ImportCommand.ExecuteAsync("local.reg");
            check(
                previews[^1] == ("local.reg", RegistryView.Registry32) && dialogs.Calls.SequenceEqual(["computer"]) &&
                notices.Count == 0,
                "Dropped registry files preserve the current local registry view without showing a picker");
            var refreshed = new List<string>();
            window.OpenBrowser(RegistryLocation.Parse("HKCU\\Software\\ImportRefreshOne"));
            window.VisibleBrowser!.RegistryChanged += (_, _) =>
            {
                refreshed.Add("first");
                throw new IOException("Test first refresh failure");
            };
            window.OpenBrowser(RegistryLocation.Parse("HKCU\\Software\\ImportRefreshTwo"));
            window.VisibleBrowser!.RegistryChanged += (_, _) =>
            {
                refreshed.Add("second");
                throw new IOException("Test second refresh failure");
            };
            await menu.RefreshAfterImportAsync();
            check(
                refreshed.SequenceEqual(["first", "second"]) && notices.Count == 0,
                "Post-import refresh failures stay isolated per tab and do not replace the import result");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class MenuDialogs : IRegistryBranchDialogs
    {
        internal List<string> Calls { get; } = [];

        internal string? OpenFile { get; set; }

        // Canceled without connecting, so tests never reach a network registry.
        public Task<bool> RequestComputerNameAsync(Func<string, Task> connect)
        {
            this.Calls.Add("computer");
            return Task.FromResult(false);
        }

        public Task<string?> PickOpenAsync(string extension)
        {
            this.Calls.Add("open");
            return Task.FromResult(this.OpenFile);
        }

        public Task<bool> ConfirmAsync(string title, string message, string accept)
        {
            this.Calls.Add("confirm");
            return Task.FromResult(false);
        }

        public Task<string?> PickSaveAsync(string name, string extension, string description)
        {
            throw new InvalidOperationException();
        }

        public Task<string?> RequestMountNameAsync(string file, string previous, string? error)
        {
            throw new InvalidOperationException();
        }

        public Task<ReportRange?> ChoosePrintRangeAsync(RegistryLocation location)
        {
            throw new InvalidOperationException();
        }

        public Task PreviewReportAsync(string title, string report, CancellationToken token)
        {
            throw new InvalidOperationException();
        }

        public void ShowPermissions(RegistryLocation target)
        {
            throw new InvalidOperationException();
        }
    }
}
