// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;
using JPSoftworks.Regedit.Core.Import;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Files;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class RegistryFilePreviewViewModelTests
{
    private const string Header = "Windows Registry Editor Version 5.00\r\n";
    private const string Key = "HKEY_CURRENT_USER\\Software\\RegistryFileViewModelFixture";
    private const string Source = Header + "[" + Key + "]\r\n\"Value\"=\"original\"\r\n";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("Regedit.FileViewModel.");
        try
        {
            await DocumentAsync(directory.FullName, check);
            await FileCommandsAsync(directory.FullName, check);
            await DiagnosticsAsync(directory.FullName, check);
            await DiagnosticNoticeAsync(directory.FullName, check);
            await ImportAsync(directory.FullName, check);
            await CloseAsync(directory.FullName, check);
            await ShellCloseAsync(directory.FullName, check);
            await BatchCloseAsync(directory.FullName, check);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static RegistryFilePreviewViewModel Create(Dialogs dialogs, Func<string, string, RegistryView, Task>? import = null)
    {
        return new RegistryFilePreviewViewModel(
            RegistryView.Registry32, RegistryFileLayout.Split, dialogs, import ?? ((_, _, _) => Task.CompletedTask), NullLogger.Instance);
    }

    private static List<(string Title, string Message, NoticeKind Kind)> CaptureNotices(RegistryFilePreviewViewModel model)
    {
        var notices = new List<(string Title, string Message, NoticeKind Kind)>();
        model.Notice += (title, message, kind) => notices.Add((title, message, kind));
        return notices;
    }

    private static async Task<string> WriteAsync(string directory, string name, string text = Source)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(true, true));
        return path;
    }

    private static async Task SettledAsync(RegistryFilePreviewViewModel model)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (model.IsParsing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (model.IsParsing)
        {
            throw new InvalidOperationException("The registry file preview did not settle.");
        }
    }

    private static async Task DocumentAsync(string directory, Action<bool, string> check)
    {
        var file = await WriteAsync(directory, "document.reg");
        var dialogs = new Dialogs();
        var model = Create(dialogs);
        var notices = CaptureNotices(model);
        check(
            !model.SaveCommand.CanExecute(null)
            && !model.ImportCommand.CanExecute(null)
            && model.OpenCommand.CanExecute(null)
            && model.Layout == RegistryFileLayout.Split, "An empty file model exposes open but disables commands that require a document");
        var sourceLoaded = 0;
        model.SourceLoaded += () => sourceLoaded++;
        await model.OpenAsync(file);
        check(
            sourceLoaded == 1
            && model.Text == Source
            && model.FilePath == file
            && !model.IsDirty
            && model.Document?.CanImport == true
            && model.ImportCommand.CanExecute(null)
            && model.TargetRegistryView == RegistryView.Registry32
            && model.Title == "document.reg",
            "Loading populates source, file identity, target view and command state without a native editor");
        check(
            model.SelectedKeyPath == Key && model.Rows.Count == 2 && model.Rows[1].Data == "original",
            "The file model selects its first key and builds source-order value rows");

        var jumps = new List<(int Line, bool Focus)>();
        model.SourceLineRequested += (line, focus) => jumps.Add((line, focus));
        model.SelectedValue = model.Rows[1];
        check(jumps is [(3, false)], "Selecting a preview row requests its source line without stealing focus");
        jumps.Clear();
        model.Text = Header
                     + "["
                     + Key
                     + "]\r\n\"Value\"=\"edited\"\r\n"
                     + "["
                     + Key
                     + "\\Child]\r\n@=\"child\"\r\n"
                     + "[-"
                     + Key
                     + "]\r\n["
                     + Key
                     + "]\r\n\"After\"=\"retained\"\r\n";
        check(
            model.IsDirty && model.Title.EndsWith(" *") && model.Document is null && !model.ImportCommand.CanExecute(null),
            "Editing invalidates import immediately and marks the document dirty");
        model.SelectSourceLine(8);
        await SettledAsync(model);
        check(
            model.Document?.KeyChanges[Key] == RegistryFileKeyChange.Recreate
            && model.Rows.Select(row => row.Operation)
                .SequenceEqual(["Create/open key", "Set value", "Delete key", "Create/open key", "Set value"]),
            "The preview model retains sequential delete/recreate instructions");
        check(jumps.Count == 0, "Parsing and source-driven selection never request a caret jump");

        model.Text = Header + "[" + Key + "]\r\n\"Broken\"=unknown\r\n";
        await SettledAsync(model);
        check(
            !model.ImportCommand.CanExecute(null)
            && model.DiagnosticLine == 3
            && notices.Count == 0
            && model.HasDiagnostics
            && model.IsProblemsExpanded, "Invalid source disables import and opens Problems without an operation notice");
        model.Layout = RegistryFileLayout.Preview;
        model.GoToErrorCommand.Execute(null);
        check(
            model.Layout == RegistryFileLayout.Split && jumps[^1] == (3, true),
            "Go to error requests an editor-visible layout and focused source navigation");

        var navigation = new List<RegistryLocation>();
        model.OpenKeyRequested += navigation.Add;
        model.Text = Source;
        model.Text = Source.Replace("original", "latest", StringComparison.Ordinal);
        model.Text = Source;
        await SettledAsync(model);
        check(
            !model.IsDirty && model.Document?.Diagnostics.Count == 0 && notices.Count == 0 && model.Rows[1].Data == "original",
            "Rapid edits publish only the current preview and restoring saved text clears dirty state");
        model.OpenKeyCommand.Execute(null);
        check(
            navigation is [{ View: RegistryView.Registry32 }] && navigation[0].Address == Key,
            "Open key carries the selected path and explicit registry view");
        model.SelectRegistry64Command.Execute(null);
        model.OpenKeyCommand.Execute(null);
        check(navigation[^1].View == RegistryView.Registry64, "Changing the target view affects key navigation");

        var history = new List<bool>();
        model.EditHistoryRequested += history.Add;
        model.SetEditorHistory(true, false);
        model.UndoCommand.Execute(null);
        model.RedoCommand.Execute(null);
        check(
            history is [false] && model.UndoCommand.CanExecute(null) && !model.RedoCommand.CanExecute(null),
            "History commands use editor-reported availability and request native history operations");
        await model.CloseCommand.ExecuteAsync(null);
        check(
            model.IsClosed && !model.SaveCommand.CanExecute(null) && !model.UndoCommand.CanExecute(null),
            "Closing a clean document disables all editing commands");
    }

    private static async Task FileCommandsAsync(string directory, Action<bool, string> check)
    {
        var file = await WriteAsync(directory, "commands.reg");
        var other = await WriteAsync(directory, "other.reg", Source.Replace("original", "other", StringComparison.Ordinal));
        var dialogs = new Dialogs();
        var model = Create(dialogs);
        var notices = CaptureNotices(model);
        await model.OpenAsync(file);
        var saves = 0;
        model.Saved += () => saves++;
        model.Text = Source.Replace("original", "edited", StringComparison.Ordinal);
        dialogs.Choice = RegistryFileSaveChoice.Cancel;
        await model.ReloadCommand.ExecuteAsync(null);
        await model.OpenAsync(other);
        await model.OpenCommand.ExecuteAsync(null);
        check(
            model.IsDirty && model.FilePath == file && dialogs.OpenCalls == 0 && model.Text.Contains("edited", StringComparison.Ordinal),
            "Cancel preserves unsaved edits during reload, host navigation and the open command");
        dialogs.Choice = RegistryFileSaveChoice.Discard;
        dialogs.OpenFile = null;
        await model.OpenCommand.ExecuteAsync(null);
        check(model.IsDirty && model.FilePath == file, "Canceling the picker after Discard leaves the original dirty document intact");
        await model.ReloadCommand.ExecuteAsync(null);
        check(!model.IsDirty && model.Text == Source, "Confirmed reload restores disk contents");

        model.Text = Source.Replace("original", "saved", StringComparison.Ordinal);
        await model.SaveCommand.ExecuteAsync(null);
        var savedBytes = await File.ReadAllBytesAsync(file);
        check(
            !model.IsDirty
            && saves == 1
            && savedBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
            && (await File.ReadAllTextAsync(file)).Contains("saved", StringComparison.Ordinal),
            "Save preserves encoding and requests the editor save point after committing the document");
        model.Text = Source.Replace("original", "unsaved", StringComparison.Ordinal);
        await File.WriteAllTextAsync(file, Source + "; external\r\n", new UTF8Encoding(true, true));
        await model.SaveCommand.ExecuteAsync(null);
        check(
            model.IsDirty
            && saves == 1
            && notices.LastOrDefault().Kind == NoticeKind.Error
            && (await File.ReadAllTextAsync(file)).Contains("; external", StringComparison.Ordinal),
            "An external edit prevents overwrite, retains dirty state and publishes a recoverable error");

        dialogs.SaveFile = null;
        await model.SaveAsCommand.ExecuteAsync(null);
        check(model.FilePath == file && model.IsDirty, "Canceling Save as preserves file identity and edits");
        var copy = Path.Combine(directory, "copy.reg");
        dialogs.SaveFile = copy;
        await model.SaveAsCommand.ExecuteAsync(null);
        check(
            model.FilePath == copy && !model.IsDirty && saves == 2 && await File.ReadAllTextAsync(copy) == model.Text,
            "Save as commits current text and switches the document identity");
        model.Text += "; keep before open\r\n";
        dialogs.Choice = RegistryFileSaveChoice.Save;
        dialogs.OpenFile = other;
        model.SetEditorHistory(true, true);
        await model.OpenCommand.ExecuteAsync(null);
        check(
            model.FilePath == other
            && !model.IsDirty
            && !model.UndoCommand.CanExecute(null)
            && (await File.ReadAllTextAsync(copy)).Contains("; keep before open", StringComparison.Ordinal),
            "Save before opening commits the old document and resets history availability for the new one");
        await model.TryCloseAsync();
    }

    private static async Task DiagnosticsAsync(string directory, Action<bool, string> check)
    {
        var invalid = Header + "[" + Key + "]\r\n\"First\"=unknown\r\n\"Second\"=dword:invalid\r\n";
        var file = await WriteAsync(directory, "problems.reg", invalid);
        var model = Create(new Dialogs());
        var notices = CaptureNotices(model);
        var jumps = new List<(int Line, bool Focus)>();
        model.SourceLineRequested += (line, focus) => jumps.Add((line, focus));
        await model.OpenAsync(file);
        check(
            model.Diagnostics.Select(error => error.Line).SequenceEqual([3, 4])
            && model.Diagnostics.All(error => error.Message.Length > 0)
            && model.DiagnosticsSummary == "2 errors"
            && model.HasDiagnostics
            && !model.HasNoDiagnostics
            && model.IsProblemsExpanded
            && notices.Count == 0, "Problems exposes every parser error with its line and message without using an InfoBar");
        check(jumps.Count == 0, "Publishing diagnostics never requests a caret jump");

        var previous = model.Diagnostics;
        model.IsProblemsExpanded = false;
        model.Text = invalid + "; typing\r\n";
        check(
            model.IsParsing
            && ReferenceEquals(model.Diagnostics, previous)
            && !model.CanNavigateDiagnostics
            && !model.GoToDiagnosticCommand.CanExecute(previous[0]),
            "Pending parsing retains the Problems list and temporarily disables stale navigation");
        await SettledAsync(model);
        check(
            !model.IsProblemsExpanded && jumps.Count == 0,
            "Editing an invalid file preserves a collapsed Problems panel and leaves the caret alone");
        check(
            !model.GoToDiagnosticCommand.CanExecute(new RegistryFileDiagnostic(999, "Stale error")),
            "Diagnostics that do not belong to the current parse cannot navigate the editor");

        model.Layout = RegistryFileLayout.Preview;
        model.ShowProblemsCommand.Execute(null);
        check(
            model.Layout == RegistryFileLayout.Split && model.IsProblemsExpanded && jumps.Count == 0,
            "Showing Problems from preview-only reveals the editor without moving the caret");
        model.Layout = RegistryFileLayout.Editor;
        model.GoToDiagnosticCommand.Execute(model.Diagnostics[1]);
        check(
            model.Layout == RegistryFileLayout.Editor && jumps is [(4, true)],
            "Choosing any problem focuses its source line while preserving editor-only mode");

        model.Text = Source;
        await SettledAsync(model);
        check(
            !model.HasDiagnostics
            && model.HasNoDiagnostics
            && model.Diagnostics.Count == 0
            && !model.ShowProblemsCommand.CanExecute(null)
            && !model.GoToDiagnosticCommand.CanExecute(previous[0])
            && model.ImportCommand.CanExecute(null), "Correcting the file removes Problems and disables stale problem navigation");
        model.Text = invalid;
        await SettledAsync(model);
        check(
            model.IsProblemsExpanded && jumps is [(4, true)],
            "A new set of errors opens Problems without stealing editor focus or moving its caret");
        await model.TryCloseAsync();
    }

    private static async Task DiagnosticNoticeAsync(string directory, Action<bool, string> check)
    {
        var file = await WriteAsync(directory, "diagnostic-notice.reg");
        var model = Create(new Dialogs());
        var notices = CaptureNotices(model);
        await model.OpenAsync(file);
        model.Text = Header + "[" + Key + "]\r\n\"Broken\"=invalid\r\n";
        await SettledAsync(model);
        await model.SaveCommand.ExecuteAsync(null);
        check(
            notices.LastOrDefault().Title == "File saved"
            && model.DiagnosticLine == 3
            && model.GoToErrorCommand.CanExecute(null)
            && !model.ImportCommand.CanExecute(null),
            "Saving invalid source preserves diagnostic navigation independently of the operation notice");
        var requestedLine = 0;
        model.SourceLineRequested += (line, _) => requestedLine = line;
        model.GoToErrorCommand.Execute(null);
        check(requestedLine == 3, "The saved document's diagnostic still navigates to its source error");
        model.Text = Source;
        await SettledAsync(model);
        check(
            model.DiagnosticLine is null
            && !model.GoToErrorCommand.CanExecute(null)
            && model.ImportCommand.CanExecute(null)
            && notices.Count == 1
            && notices.LastOrDefault().Title == "File saved",
            "Correcting the source clears diagnostic navigation while preserving the operation notice");
        await model.TryCloseAsync();
    }

    private static async Task ImportAsync(string directory, Action<bool, string> check)
    {
        var file = await WriteAsync(directory, "import.reg");
        var dialogs = new Dialogs();
        string? snapshot = null, importedText = null, importedSource = null;
        RegistryView? importedView = null;
        var calls = 0;
        var failImport = false;
        var model = Create(
            dialogs, async (path, source, view) =>
            {
                calls++;
                snapshot = path;
                importedText = await File.ReadAllTextAsync(path);
                importedSource = source;
                importedView = view;
                if (failImport)
                {
                    throw new IOException("Test import failure");
                }
            });
        var notices = CaptureNotices(model);
        await model.OpenAsync(file);
        model.Text = Header + ";Requires=Logoff\r\n[" + Key + "]\r\n\"Value\"=\"unsaved\"\r\n";
        await SettledAsync(model);
        dialogs.ImportConfirmed = false;
        await model.ImportCommand.ExecuteAsync(null);
        check(
            calls == 0 && model.IsDirty && await File.ReadAllTextAsync(file) == Source,
            "Canceling import neither writes to the registry nor saves the source file");

        var confirmedText = model.Text;
        dialogs.ImportConfirmed = true;
        dialogs.OnImportConfirmation = () =>
        {
            check(
                model.IsBusy
                && !model.CanInteract
                && !model.SaveCommand.CanExecute(null)
                && !model.SelectRegistry64Command.CanExecute(null), "Import confirmation gates other document commands");
            model.SelectRegistry64Command.Execute(null);
            model.Text = Source;
        };
        await model.ImportCommand.ExecuteAsync(null);
        check(
            calls == 1
            && importedView == RegistryView.Registry32
            && importedSource == file
            && importedText == confirmedText
            && snapshot is not null
            && !File.Exists(snapshot)
            && await File.ReadAllTextAsync(file) == Source,
            "Import uses the confirmed unsaved snapshot and target view, then removes the temporary file");
        check(
            notices.LastOrDefault().Title == "Import complete"
            && notices.LastOrDefault().Kind == NoticeKind.Success
            && notices.LastOrDefault().Message.Contains("File recommends: Sign out.", StringComparison.Ordinal),
            "Successful import follow-up comes from the imported snapshot metadata");

        dialogs.OnImportConfirmation = null;
        failImport = true;
        await model.ImportCommand.ExecuteAsync(null);
        check(
            notices.LastOrDefault().Title == "Couldn't import the registry file"
            && notices.LastOrDefault().Kind == NoticeKind.Error
            && model.CanInteract
            && snapshot is not null
            && !File.Exists(snapshot)
            && !notices.LastOrDefault().Message.Contains("File recommends", StringComparison.Ordinal),
            "Failed imports restore command availability, remove snapshots and do not publish success follow-up");
        model.Text = Header + "[" + Key + "]\r\n\"Value\"=bad\r\n";
        var confirmations = dialogs.ImportCalls;
        await model.ImportCommand.ExecuteAsync(null);
        check(
            calls == 2 && dialogs.ImportCalls == confirmations,
            "Import validates current source even when a disabled command is invoked directly");
        dialogs.Choice = RegistryFileSaveChoice.Discard;
        await model.TryCloseAsync();
    }

    private static async Task CloseAsync(string directory, Action<bool, string> check)
    {
        var file = await WriteAsync(directory, "close.reg");
        var dialogs = new Dialogs { Choice = RegistryFileSaveChoice.Cancel };
        var model = Create(dialogs);
        var notices = CaptureNotices(model);
        await model.OpenAsync(file);
        model.Text += "; edited\r\n";
        var closed = 0;
        model.CloseRequested += () => closed++;
        check(
            !await model.TryCloseAsync() && !model.IsClosed && model.IsDirty && model.CanInteract && closed == 0,
            "Canceled close preserves the document and resumes interaction");
        var reply = new TaskCompletionSource<RegistryFileSaveChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogs.CloseReply = reply.Task;
        var pending = model.TryCloseAsync();
        var prompts = dialogs.ChangesCalls;
        check(
            !await model.TryCloseAsync() && dialogs.ChangesCalls == prompts && model.IsBusy,
            "Repeated document close requests share the active confirmation without opening another dialog");
        reply.SetResult(RegistryFileSaveChoice.Save);
        check(
            await pending
            && model.IsClosed
            && !model.IsDirty
            && closed == 1
            && (await File.ReadAllTextAsync(file)).Contains("; edited", StringComparison.Ordinal),
            "Saving during close persists edits and requests one native close");
        check(await model.TryCloseAsync() && closed == 1, "An approved close is idempotent");
        var failures = new List<(string Title, string Message, NoticeKind Kind)>();
        model.Notice += (title, message, kind) => failures.Add((title, message, kind));
        model.ReportFailure(new IOException("Native window close failed"));
        check(
            notices.LastOrDefault().Kind == NoticeKind.Error
            && notices.Count > 0
            && notices.LastOrDefault().Message == "Native window close failed",
            "The native close boundary can publish an observed error through the file model");

        model.ReportFailure(new IOException("Native window close failed"));
        check(
            failures.Count == 2
            && failures.All(notice => notice.Message == "Native window close failed" && notice.Kind == NoticeKind.Error),
            "Repeated file-operation failures each publish a notification event");

        var failedSaveFile = await WriteAsync(directory, "failed-close-save.reg");
        var failedSaveDialogs = new Dialogs { Choice = RegistryFileSaveChoice.Save };
        var failedSave = Create(failedSaveDialogs);
        var failedSaveNotices = CaptureNotices(failedSave);
        await failedSave.OpenAsync(failedSaveFile);
        failedSave.Text += "; unsaved close\u005cn";
        await File.WriteAllTextAsync(failedSaveFile, Source + "; external change\u005cn", new UTF8Encoding(true, true));
        check(
            !await failedSave.TryCloseAsync()
            && failedSave.IsDirty
            && !failedSave.IsClosed
            && failedSave.CanInteract
            && failedSaveNotices.LastOrDefault().Kind == NoticeKind.Error,
            "A failed save prevents close and leaves the edited document available for recovery");
        failedSaveDialogs.Choice = RegistryFileSaveChoice.Discard;
        check(await failedSave.TryCloseAsync(), "A failed close can be retried with an explicit discard");

        var discard = Create(new Dialogs { Choice = RegistryFileSaveChoice.Discard });
        await discard.OpenAsync(file);
        discard.Text = "invalid pending parse";
        check(await discard.TryCloseAsync(), "Discard can close a document while its preview is pending");
        await Task.Delay(300);
        check(discard.IsClosed && discard.Document is null && !discard.IsParsing, "A canceled parse cannot publish a document after close");
    }

    private static async Task ShellCloseAsync(string directory, Action<bool, string> check)
    {
        var settings = AppSettings.Load(Path.Combine(directory, "shell-settings.json"));
        var registry = new RegistryService();
        var shell = new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry, location => new BrowserViewModel(registry, location),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        var file = await WriteAsync(directory, "shell.reg");
        var firstDialogs = new Dialogs { Choice = RegistryFileSaveChoice.Cancel };
        var first = Create(firstDialogs);
        var second = Create(new Dialogs());
        await first.OpenAsync(file);
        await second.OpenAsync(file);
        first.Text += "; dirty child\r\n";
        shell.RegisterRegistryFile(first);
        shell.RegisterRegistryFile(second);
        check(
            !await shell.TryCloseAsync(_ => Task.FromResult(NoteCloseChoice.Discard))
            && !shell.CanCloseWindow
            && !second.IsClosed
            && shell.CanOpenRegistryFile
            && !shell.NoteStore.IsPaused, "A canceled file close stops application shutdown and resumes the shell");

        var reply = new TaskCompletionSource<RegistryFileSaveChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstDialogs.CloseReply = reply.Task;
        var closing = shell.TryCloseAsync(_ => Task.FromResult(NoteCloseChoice.Discard));
        check(
            shell.IsClosing
            && !shell.CanOpenRegistryFile
            && !await shell.TryCloseAsync(_ => throw new InvalidOperationException("Duplicate shell close")),
            "Application close owns reentrancy and blocks opening more file windows");
        reply.SetResult(RegistryFileSaveChoice.Discard);
        check(
            await closing && first.IsClosed && second.IsClosed && shell.CanCloseWindow,
            "Application shutdown closes registered document models without native Window dependencies");
        shell.UnregisterRegistryFile(first);
        shell.UnregisterRegistryFile(second);
        shell.WindowClosed();

        var freshSettings = AppSettings.Load(Path.Combine(directory, "navigation-settings.json"));
        var navigationShell = new MainWindowViewModel(
            freshSettings, new InvestigationStore(freshSettings, () => { }), registry, location => new BrowserViewModel(registry, location),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(freshSettings));
        var editorRequests = 0;
        navigationShell.RegistryFileEditorSettingsRequested += () => editorRequests++;
        navigationShell.OpenRegistryFileEditorSettingsCommand.Execute(null);
        check(
            navigationShell.ActiveTab is SettingsTab && editorRequests == 1,
            "Registry file settings navigation is owned by the shell view model");
        navigationShell.WindowClosed();
    }

    private static async Task BatchCloseAsync(string directory, Action<bool, string> check)
    {
        var settings = AppSettings.Load(Path.Combine(directory, "batch-close-settings.json"));
        var registry = new RegistryService();
        var shell = new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry, location => new BrowserViewModel(registry, location),
            NullLogger<MainWindowViewModel>.Instance,
            NotesStoreTests.CreateStore(settings, _ => throw new IOException("Test note save failure")));
        var file = await WriteAsync(directory, "batch-close.reg");
        var firstDialogs = new Dialogs { Choice = RegistryFileSaveChoice.Discard };
        var secondDialogs = new Dialogs { Choice = RegistryFileSaveChoice.Cancel };
        var first = Create(firstDialogs);
        var firstNotices = CaptureNotices(first);
        var second = Create(secondDialogs);
        await first.OpenAsync(file);
        await second.OpenAsync(file);
        first.Text += "; first unsaved edit\r\n";
        second.Text += "; second unsaved edit\r\n";
        shell.RegisterRegistryFile(first);
        shell.RegisterRegistryFile(second);
        var closed = 0;
        first.CloseRequested += () => closed++;
        second.CloseRequested += () => closed++;
        check(
            !await shell.TryCloseAsync(_ => Task.FromResult(NoteCloseChoice.Discard))
            && !first.IsClosed
            && !second.IsClosed
            && first.IsDirty
            && second.IsDirty
            && closed == 0
            && first.CanInteract
            && second.CanInteract, "Canceling a later preview preserves earlier Discard decisions as editable unsaved documents");

        secondDialogs.Choice = RegistryFileSaveChoice.Discard;
        var note = shell.GetNote(RegistryLocation.Parse("HKCU\\Software\\BatchClose"));
        note.Text = "Unsaved note";
        check(
            !await shell.TryCloseAsync(_ => Task.FromResult(NoteCloseChoice.Cancel))
            && !first.IsClosed
            && !second.IsClosed
            && first.IsDirty
            && second.IsDirty
            && note.IsDirty
            && closed == 0
            && first.CanInteract
            && second.CanInteract, "Canceling the notes prompt preserves every approved preview and its unsaved edits");

        var noteReply = new TaskCompletionSource<NoteCloseChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = shell.TryCloseAsync(_ => noteReply.Task);
        check(
            first.IsClosePending
            && second.IsClosePending
            && !first.CanInteract
            && !second.CanInteract
            && !first.IsClosed
            && !second.IsClosed
            && closed == 0, "Approved previews are read-only while remaining exit confirmations are pending");
        var activation = 0;
        first.ActivationRequested += () => activation++;
        check(
            !await first.TryCloseAsync()
            && activation == 1
            && firstNotices.LastOrDefault().Message == "Finish the current operation before closing.",
            "Closing an occupied preview provides feedback and requests activation");

        first.Text += "; programmatic edit after approval\r\n";
        noteReply.SetResult(NoteCloseChoice.Discard);
        try
        {
            await closing;
            check(false, "Changing an approved preview must invalidate its close decision");
        }
        catch (InvalidOperationException)
        {
            check(
                !first.IsClosed && !second.IsClosed && closed == 0 && first.CanInteract && second.CanInteract,
                "A changed close approval aborts before any preview is destroyed");
        }

        check(
            await shell.TryCloseAsync(_ => Task.FromResult(NoteCloseChoice.Discard))
            && first.IsClosed
            && second.IsClosed
            && closed == 2
            && shell.CanCloseWindow, "Preview windows close only after every current preview and note decision succeeds");
        shell.WindowClosed();
    }

    private sealed class Dialogs : IRegistryFileDialogs
    {
        internal string? OpenFile { get; set; }

        internal string? SaveFile { get; set; }

        internal RegistryFileSaveChoice Choice { get; set; } = RegistryFileSaveChoice.Discard;

        internal Task<RegistryFileSaveChoice>? CloseReply { get; set; }

        internal bool ImportConfirmed { get; set; }

        internal Action? OnImportConfirmation { get; set; }

        internal int OpenCalls { get; private set; }

        internal int ChangesCalls { get; private set; }

        internal int ImportCalls { get; private set; }

        public Task<string?> PickOpenAsync()
        {
            this.OpenCalls++;
            return Task.FromResult(this.OpenFile);
        }

        public Task<string?> PickSaveAsync(string currentFile)
        {
            return Task.FromResult(this.SaveFile);
        }

        public Task<RegistryFileSaveChoice> ConfirmChangesAsync(string fileName)
        {
            this.ChangesCalls++;
            return this.CloseReply ?? Task.FromResult(this.Choice);
        }

        public Task<bool> ConfirmImportAsync(string fileName, RegistryView view)
        {
            this.ImportCalls++;
            this.OnImportConfirmation?.Invoke();
            return Task.FromResult(this.ImportConfirmed);
        }
    }
}
