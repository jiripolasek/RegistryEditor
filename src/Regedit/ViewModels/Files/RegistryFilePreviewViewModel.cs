// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Import;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Files;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Files;

/// <summary>Registry file document state and commands, independent of its editor and window.</summary>
internal sealed partial class RegistryFilePreviewViewModel : ObservableObject
{
    internal event Action? SourceLoaded;

    internal event Action? Saved;

    internal event Action<bool>? EditHistoryRequested;

    internal event Action<int, bool>? SourceLineRequested;

    internal event Action<RegistryLocation>? OpenKeyRequested;

    internal event Action? SettingsRequested;

    internal event Action? CloseRequested;

    internal event Action? ActivationRequested;

    internal event Action<string, string, NoticeKind>? Notice;

    private readonly IRegistryFileDialogs _dialogs;
    private readonly Func<string, string, RegistryView, Task> _import;
    private readonly ILogger _logger;
    private long _approvedCloseRevision;
    private RegistryFileBuffer? _buffer;
    private bool _editorCanUndo, _editorCanRedo;
    private CancellationTokenSource? _parseCancellation;
    private long _revision;

    [ObservableProperty]
    public partial RegistryFileDocument? Document { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasDiagnostics), nameof(HasNoDiagnostics), nameof(DiagnosticsSummary), nameof(DiagnosticLine),
        nameof(CanNavigateDiagnostics))]
    public partial IReadOnlyList<RegistryFileDiagnostic> Diagnostics { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsProblemsExpanded { get; set; }

    [ObservableProperty]
    public partial RegistryFileMetadata Metadata { get; private set; } = RegistryFileMetadata.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<RegistryFilePreviewRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial string? SelectedKeyPath { get; set; }

    [ObservableProperty]
    public partial RegistryFilePreviewRow? SelectedValue { get; set; }

    [ObservableProperty]
    public partial RegistryFileLayout Layout { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetViewLabel), nameof(TargetViewHelp), nameof(IsRegistry32), nameof(IsRegistry64))]
    public partial RegistryView TargetRegistryView { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract), nameof(CanPreview), nameof(CanNavigateDiagnostics))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(CanNavigateDiagnostics))]
    public partial bool IsParsing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract), nameof(CanPreview), nameof(CanNavigateDiagnostics))]
    public partial bool IsClosed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract), nameof(CanPreview), nameof(CanNavigateDiagnostics))]
    public partial bool IsClosePending { get; private set; }

    public string Text
    {
        get => this._buffer?.Text ?? string.Empty;
        set
        {
            if (this._buffer is null || this.IsClosed || this._buffer.Text == value)
            {
                return;
            }

            this._buffer.Text = value;
            this.OnPropertyChanged();
            this.NotifyFileChanged();
            _ = this.ScheduleParse(true);
        }
    }

    public string FilePath => this._buffer?.FilePath ?? "Open a registry file";

    public string Title => this._buffer is null
        ? "Open a .reg file"
        : Path.GetFileName(this._buffer.FilePath) + (this.IsDirty ? " *" : string.Empty);

    public string EncodingName => this._buffer?.EncodingName ?? string.Empty;

    public bool IsDirty => this._buffer?.IsDirty == true;

    public bool CanInteract => !this.IsBusy && !this.IsClosed && !this.IsClosePending;

    public bool CanPreview => this.CanInteract && this.Document is not null;

    public string TargetViewLabel => this.IsRegistry32 ? "32-bit" : "64-bit";

    public string TargetViewHelp => "Import registry view: " + this.TargetViewLabel;

    public bool IsRegistry32 => this.TargetRegistryView == RegistryView.Registry32;

    public bool IsRegistry64 => this.TargetRegistryView == RegistryView.Registry64;

    public string Status
    {
        get
        {
            if (this.IsParsing)
            {
                return "Updating file preview…";
            }

            var keys = this.Document?.Sections.Count ?? 0;
            var values = this.Document?.Sections.Sum(section => section.Values.Count) ?? 0;
            var errors = this.Document?.Diagnostics.Count ?? 0;
            return errors > 0
                ? $"{errors} file error(s)"
                : $"{keys} key {(keys == 1 ? "section" : "sections")} · {values} {(values == 1 ? "value" : "values")}";
        }
    }

    public bool HasDiagnostics => this.Diagnostics.Count > 0;

    public bool HasNoDiagnostics => !this.HasDiagnostics;

    public string DiagnosticsSummary =>
        $"{this.Diagnostics.Count} {(this.Diagnostics.Count == 1 ? "error" : "errors")}";

    public bool CanNavigateDiagnostics => this.CanInteract && !this.IsParsing && this.HasDiagnostics;

    public int? DiagnosticLine => this.Diagnostics.FirstOrDefault()?.Line;

    internal int SourceLine { get; private set; } = 1;

    private bool HasFile => this.CanInteract && this._buffer is not null;

    private bool CanImport => this.HasFile && this.Document?.CanImport == true;

    private bool CanOpenKey => this.CanPreview && this.SelectedKeyPath is not null;

    private bool CanGoToSelectedKey => this.CanOpenKey
                                       && this.Document!.Sections.Any(section =>
                                           StringComparer.OrdinalIgnoreCase.Equals(section.Path, this.SelectedKeyPath));

    private bool CanGoToError => this.CanNavigateDiagnostics;

    private bool CanUndo => this.HasFile && this._editorCanUndo;

    private bool CanRedo => this.HasFile && this._editorCanRedo;

    internal bool CanCompleteClose => this.IsClosed || this.IsClosePending && this._approvedCloseRevision == this._revision;

    internal RegistryFilePreviewViewModel(
        RegistryView view,
        RegistryFileLayout layout,
        IRegistryFileDialogs dialogs,
        Func<string, string, RegistryView, Task> import,
        ILogger logger)
    {
        this.TargetRegistryView = view;
        this.Layout = layout;
        this._dialogs = dialogs;
        this._import = import;
        this._logger = logger;
    }

    internal Task OpenAsync(string file)
    {
        return this.RunAsync(
            "Couldn't open the registry file", async () =>
            {
                if (await this.ConfirmChangesAsync())
                {
                    await this.LoadAsync(file);
                }
            });
    }

    private async Task LoadAsync(string file)
    {
        var buffer = await RegistryFileBuffer.OpenAsync(file);
        this._buffer = buffer;
        this.SourceLine = 1;
        this.Metadata = RegistryFileMetadata.Empty;
        this.Diagnostics = [];
        this.IsProblemsExpanded = false;
        this.SetEditorHistory(false, false);
        this.SourceLoaded?.Invoke();
        this.OnPropertyChanged(nameof(this.Text));
        this.NotifyFileChanged();
        await this.ScheduleParse(false);
    }

    private Task ScheduleParse(bool debounce)
    {
        this._parseCancellation?.Cancel();
        this._parseCancellation?.Dispose();
        this._parseCancellation = new CancellationTokenSource();
        var revision = ++this._revision;
        this.Document = null;
        this.IsParsing = true;
        return this.ParseAsync(this.Text, revision, debounce, this._parseCancellation.Token);
    }

    private async Task ParseAsync(string text, long revision, bool debounce, CancellationToken token)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(250, token);
            }

            var document = await Task.Run(() => RegistryFileDocument.Parse(text), token);
            if (revision != this._revision || this.IsClosed)
            {
                return;
            }

            this.Document = document;
            this.Metadata = document.Metadata;
            var sourceSection = document.Sections.LastOrDefault(section => section.Line <= this.SourceLine);
            var path = sourceSection?.Path ?? this.SelectedKeyPath;
            this.SelectedKeyPath = path is not null && document.KeyChanges.ContainsKey(path)
                ? path
                : document.Sections.FirstOrDefault()?.Path;
            this.UpdateRows();
            this.Diagnostics = document.Diagnostics;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (revision == this._revision && !this.IsClosed)
            {
                this.ReportFailure(ex, "Couldn't read the registry file");
            }
        }
        finally
        {
            if (revision == this._revision)
            {
                this.IsParsing = false;
                this.OnPropertyChanged(nameof(this.DiagnosticLine));
                this.GoToErrorCommand.NotifyCanExecuteChanged();
            }
        }
    }

    internal void SelectSourceLine(int line)
    {
        if (this.SourceLine == line)
        {
            return;
        }

        this.SourceLine = line;
        if (this.Document?.Sections.LastOrDefault(section => section.Line <= line) is { } section)
        {
            this.SelectedKeyPath = section.Path;
        }
    }

    partial void OnSelectedKeyPathChanged(string? value) => this.UpdateRows();

    partial void OnSelectedValueChanged(RegistryFilePreviewRow? value)
    {
        if (value is not null)
        {
            this.SourceLineRequested?.Invoke(value.Line, false);
        }
    }

    private void UpdateRows()
    {
        List<RegistryFilePreviewRow> rows = [];
        if (this.Document is { } document && this.SelectedKeyPath is { } path)
        {
            foreach (var section in document.Sections.Where(section => StringComparer.OrdinalIgnoreCase.Equals(section.Path, path)))
            {
                rows.Add(
                    new(
                        section.IsDeletion ? "Delete key" : "Create/open key", "(Key)", string.Empty,
                        section.IsDeletion ? "Removes the entire subtree" : string.Empty, section.Line,
                        new RegistryEntry(RegistryLocation.Parse(section.Path), null)));
                rows.AddRange(
                    section.Values.Select(value => new RegistryFilePreviewRow(
                        value.Operation, value.DisplayName, value.Value?.TypeName ?? string.Empty,
                        value.Value?.DisplayPreview ?? string.Empty, value.Line, new RegistryEntry(null, value.Value))));
            }
        }

        this.SelectedValue = null;
        this.Rows = rows;
        this.OpenKeyCommand.NotifyCanExecuteChanged();
        this.GoToSelectedKeyCommand.NotifyCanExecuteChanged();
    }

    partial void OnDocumentChanged(RegistryFileDocument? value)
    {
        this.OnPropertyChanged(nameof(this.CanPreview));
        this.OnPropertyChanged(nameof(this.Status));
        this.OnPropertyChanged(nameof(this.DiagnosticLine));
        this.RefreshCommands();
    }

    partial void OnDiagnosticsChanged(IReadOnlyList<RegistryFileDiagnostic> oldValue, IReadOnlyList<RegistryFileDiagnostic> newValue)
    {
        if (oldValue.Count == 0 && newValue.Count > 0)
        {
            this.IsProblemsExpanded = true;
        }

        this.RefreshCommands();
    }

    partial void OnIsParsingChanged(bool value) => this.RefreshCommands();

    partial void OnIsBusyChanged(bool value) => this.RefreshCommands();

    partial void OnIsClosedChanged(bool value) => this.RefreshCommands();

    partial void OnIsClosePendingChanged(bool value) => this.RefreshCommands();

    private bool CanGoToDiagnostic(RegistryFileDiagnostic? diagnostic)
    {
        return this.CanNavigateDiagnostics && diagnostic is not null && this.Diagnostics.Contains(diagnostic);
    }

    internal void SetEditorHistory(bool canUndo, bool canRedo)
    {
        this._editorCanUndo = canUndo;
        this._editorCanRedo = canRedo;
        this.UndoCommand.NotifyCanExecuteChanged();
        this.RedoCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        this.OpenCommand.NotifyCanExecuteChanged();
        this.ReloadCommand.NotifyCanExecuteChanged();
        this.SaveCommand.NotifyCanExecuteChanged();
        this.SaveAsCommand.NotifyCanExecuteChanged();
        this.ImportCommand.NotifyCanExecuteChanged();
        this.CloseCommand.NotifyCanExecuteChanged();
        this.SettingsCommand.NotifyCanExecuteChanged();
        this.OpenKeyCommand.NotifyCanExecuteChanged();
        this.GoToSelectedKeyCommand.NotifyCanExecuteChanged();
        this.GoToErrorCommand.NotifyCanExecuteChanged();
        this.GoToDiagnosticCommand.NotifyCanExecuteChanged();
        this.ShowProblemsCommand.NotifyCanExecuteChanged();
        this.UndoCommand.NotifyCanExecuteChanged();
        this.RedoCommand.NotifyCanExecuteChanged();
        this.SelectRegistry32Command.NotifyCanExecuteChanged();
        this.SelectRegistry64Command.NotifyCanExecuteChanged();
    }

    private void NotifyFileChanged()
    {
        this.OnPropertyChanged(nameof(this.FilePath));
        this.OnPropertyChanged(nameof(this.Title));
        this.OnPropertyChanged(nameof(this.EncodingName));
        this.OnPropertyChanged(nameof(this.IsDirty));
        this.RefreshCommands();
    }

    private async Task RunAsync(string failureTitle, Func<Task> action)
    {
        if (!this.CanInteract)
        {
            return;
        }

        this.IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            this.ReportFailure(ex, failureTitle);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private async Task<bool> SaveFileAsync(bool saveAs)
    {
        if (this._buffer is null)
        {
            return false;
        }

        var file = this._buffer.FilePath;
        if (saveAs)
        {
            file = await this._dialogs.PickSaveAsync(file);
            if (file is null)
            {
                return false;
            }
        }

        await this._buffer.SaveAsync(file);
        if (!this.IsDirty)
        {
            this.Saved?.Invoke();
        }

        this.NotifyFileChanged();
        this.ShowNotice("File saved", this.FilePath, NoticeKind.Success);
        return true;
    }

    private async Task<bool> ConfirmChangesAsync()
    {
        if (!this.IsDirty)
        {
            return true;
        }

        return await this._dialogs.ConfirmChangesAsync(Path.GetFileName(this.FilePath)) switch
        {
            RegistryFileSaveChoice.Save => await this.SaveFileAsync(false),
            RegistryFileSaveChoice.Discard => true,
            _ => false
        };
    }

    internal async Task<bool> PrepareCloseAsync()
    {
        if (this.IsClosed || this.IsClosePending)
        {
            return this.CanCompleteClose;
        }

        if (!this.CanInteract)
        {
            this.ShowCloseBlocked();
            return false;
        }

        var approved = false;
        await this.RunAsync(
            "Couldn't close the file", async () =>
            {
                if (await this.ConfirmChangesAsync())
                {
                    this._approvedCloseRevision = this._revision;
                    this.IsClosePending = true;
                    approved = true;
                }
            });
        return approved;
    }

    internal void CancelPendingClose()
    {
        this.IsClosePending = false;
    }

    internal void CompleteClose()
    {
        if (this.IsClosed)
        {
            return;
        }

        if (!this.CanCompleteClose)
        {
            throw new InvalidOperationException("The registry file changed while closing. Review your changes and close again.");
        }

        this.IsClosed = true;
        this.IsClosePending = false;
        this._revision++;
        this._parseCancellation?.Cancel();
        this._parseCancellation?.Dispose();
        this._parseCancellation = null;
        this.IsParsing = false;
        this.CloseRequested?.Invoke();
    }

    internal async Task<bool> TryCloseAsync()
    {
        if (this.IsClosed)
        {
            return true;
        }

        if (!this.CanInteract)
        {
            this.ShowCloseBlocked();
            return false;
        }

        if (!await this.PrepareCloseAsync())
        {
            return false;
        }

        if (!this.CanCompleteClose)
        {
            this.CancelPendingClose();
            this.ShowNotice(
                "Can't close yet", "The registry file changed while closing. Review your changes and close again.", NoticeKind.Warning);
            return false;
        }

        this.CompleteClose();
        return true;
    }

    private void ShowCloseBlocked()
    {
        this.ShowNotice("Can't close yet", "Finish the current operation before closing.", NoticeKind.Information);
        this.ActivationRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task CloseAsync()
    {
        return this.TryCloseAsync();
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task OpenAsync()
    {
        return this.RunAsync(
            "Couldn't open the registry file", async () =>
            {
                if (await this.ConfirmChangesAsync() && await this._dialogs.PickOpenAsync() is { } file)
                {
                    await this.LoadAsync(file);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task ReloadAsync()
    {
        return this.RunAsync(
            "Couldn't reload the registry file", async () =>
            {
                if (this._buffer is not null && await this.ConfirmChangesAsync())
                {
                    await this.LoadAsync(this._buffer.FilePath);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task SaveAsync()
    {
        return this.RunAsync("Couldn't save the registry file", async () => await this.SaveFileAsync(false));
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task SaveAsAsync()
    {
        return this.RunAsync("Couldn't save the registry file", async () => await this.SaveFileAsync(true));
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task ImportAsync()
    {
        return this.RunAsync(
            "Couldn't import the registry file", async () =>
            {
                if (this._buffer is null)
                {
                    return;
                }

                var bytes = this._buffer.ImportBytes(out var metadata);
                var view = this.TargetRegistryView;
                var source = this.FilePath;
                if (!await this._dialogs.ConfirmImportAsync(Path.GetFileName(source), view))
                {
                    return;
                }

                var folder = Directory.CreateTempSubdirectory("Regedit.Import.");
                try
                {
                    var snapshot = Path.Combine(folder.FullName, "preview.reg");
                    await File.WriteAllBytesAsync(snapshot, bytes);
                    await this._import(snapshot, source, view);
                    var message = Path.GetFileName(source);
                    if (metadata.FollowUp != RegistryFileFollowUp.None)
                    {
                        message += "\n\nFile recommends: " + metadata.FollowUpText + ".";
                    }

                    this.ShowNotice("Import complete", message, NoticeKind.Success);
                }
                finally
                {
                    folder.Delete(true);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Settings()
    {
        if (this.CanInteract)
        {
            this.SettingsRequested?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenKey))]
    private void OpenKey()
    {
        if (this.CanOpenKey)
        {
            this.OpenKeyRequested?.Invoke(RegistryLocation.Parse(this.SelectedKeyPath!, this.TargetRegistryView));
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToSelectedKey))]
    private void GoToSelectedKey()
    {
        if (this.CanGoToSelectedKey)
        {
            this.SourceLineRequested?.Invoke(
                this.Document!.Sections.First(section => StringComparer.OrdinalIgnoreCase.Equals(section.Path, this.SelectedKeyPath)).Line,
                false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToError))]
    private void ShowProblems()
    {
        if (this.CanNavigateDiagnostics)
        {
            if (this.Layout == RegistryFileLayout.Preview)
            {
                this.Layout = RegistryFileLayout.Split;
            }

            this.IsProblemsExpanded = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToError))]
    private void GoToError()
    {
        this.GoToDiagnostic(this.Diagnostics.FirstOrDefault());
    }

    [RelayCommand(CanExecute = nameof(CanGoToDiagnostic))]
    private void GoToDiagnostic(RegistryFileDiagnostic? diagnostic)
    {
        if (this.CanGoToDiagnostic(diagnostic))
        {
            this.ShowProblems();
            this.SourceLineRequested?.Invoke(diagnostic!.Line, true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (this.CanUndo)
        {
            this.EditHistoryRequested?.Invoke(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (this.CanRedo)
        {
            this.EditHistoryRequested?.Invoke(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void SelectRegistry32()
    {
        if (this.CanInteract)
        {
            this.TargetRegistryView = RegistryView.Registry32;
            this.OnPropertyChanged(nameof(this.IsRegistry32));
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void SelectRegistry64()
    {
        if (this.CanInteract)
        {
            this.TargetRegistryView = RegistryView.Registry64;
            this.OnPropertyChanged(nameof(this.IsRegistry64));
        }
    }

    internal void ReportFailure(Exception ex, string title = "Couldn't update the file preview")
    {
        this._logger.LogFailure(LogLevel.Warning, "Registry file preview", ex);
        this.ShowNotice(title, ex.Message, NoticeKind.Error);
    }

    private void ShowNotice(string title, string message, NoticeKind kind)
    {
        this.Notice?.Invoke(title, message, kind);
        this.OnPropertyChanged(nameof(this.DiagnosticLine));
        this.GoToErrorCommand.NotifyCanExecuteChanged();
    }
}
