// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Browser;

public sealed partial class BrowserViewModel
{
    internal event Action<Exception>? FileRevealFailed;

    internal event Action<Exception>? WebSearchFailed;

    private CancellationTokenSource? _previewCancellation;

    internal Task PendingPreview { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    internal partial bool IsPreviewCurrent { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsInspectorOpen { get; set; } = true;

    [ObservableProperty]
    internal partial ValueInspectorViewModel? Inspector { get; private set; }

    private bool CanOpenLinkTarget =>
        !this.IsLoading && RegistryLink.Resolve(this.SingleEntry?.Value, this.Location) is not null;

    private bool CanRevealFile => RegistryFileReveal.CanResolve(this.Location, this.SingleEntry?.Value);

    [RelayCommand]
    private void SearchWeb((RegistryLocation? Location, string? ValueName) target)
    {
        if (target.Location is not { IsComputer: false } location)
        {
            return;
        }

        try
        {
            var uri = RegistryWebSearch.CreateUri(this._preferences?.WebSearchUrl ?? RegistryWebSearch.Bing, location, target.ValueName);
            this._launchWebSearch(uri);
        }
        catch (Exception ex)
        {
            this.WebSearchFailed?.Invoke(ex);
            this.Error = ex.Message;
        }
    }

    partial void OnIsInspectorOpenChanged(bool value) => this.UpdateInspector();

    partial void OnIsLoadingChanged(bool value)
    {
        this.OpenLinkTargetCommand.NotifyCanExecuteChanged();
        this.UpdateInspector();
    }

    partial void OnSelectedEntriesChanged(IReadOnlyList<RegistryEntry> value)
    {
        this._previewCancellation?.Cancel();
        if (this._closed) return;
        if (value.Count != 1 || this.IsLoading)
        {
            this.UpdateInspector();
            return;
        }

        this.IsPreviewCurrent = false;
        this.PendingPreview = this.UpdatePreviewAfterSelectionAsync();
    }

    private async Task UpdatePreviewAfterSelectionAsync()
    {
        using var request = new CancellationTokenSource();
        this._previewCancellation = request;
        var token = request.Token;
        try
        {
            // Keep decoding and XAML construction out of rapid selection changes.
            await Task.Delay(150, token);
            if (!token.IsCancellationRequested && !this._closed)
            {
                this._previewCancellation = null;
                this.UpdateInspector();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                this.Error = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(this._previewCancellation, request))
            {
                this._previewCancellation = null;
            }
        }
    }

    private void UpdateInspector()
    {
        this._previewCancellation?.Cancel();
        this.Inspector = this.IsInspectorOpen && !this.IsLoading && !this._closed ? this.CreateInspector() : null;
        this.IsPreviewCurrent = true;
    }

    private ValueInspectorViewModel? CreateInspector()
    {
        var row = this.SingleEntry;
        if (row is null && this.SelectedEntries.Count == 0 && this.IsKeyLocation)
        {
            row = new RegistryEntry(this.Location, null);
        }

        if (row is null)
        {
            return null;
        }

        row = row with { SourceLocation = this.Location };
        var target = row.LinkLocation;
        var inspector = new ValueInspectorViewModel(
            row,
            row.Value?.Type is RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.Link
                or RegistryValueType.MultiString
                ? row.Data
                : null, this.CanRevealFile ? this.RevealFileCommand : null,
            target is null ? null : new AsyncRelayCommand(() => this.NavigateAsync(target)));
        _ = inspector.LoadTargetStatusAsync(this.Registry);
        return inspector;
    }

    [RelayCommand(CanExecute = nameof(CanOpenLinkTarget))]
    private Task OpenLinkTargetAsync()
    {
        return !this.IsLoading && RegistryLink.Resolve(this.SingleEntry?.Value, this.Location) is { } target
            ? this.NavigateAsync(target)
            : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRevealFile))]
    private async Task RevealFileAsync()
    {
        var location = this.Location;
        var value = this.SingleEntry?.Value;
        if (!RegistryFileReveal.CanResolve(location, value))
        {
            return;
        }

        try
        {
            var start = await Task.Run(() => RegistryFileReveal.CreateStartInfo(
                RegistryFileReveal.Resolve(location, value) ?? throw new FileNotFoundException("The file or folder no longer exists.")));
            using var process = Process.Start(start);
        }
        catch (Exception ex)
        {
            this.FileRevealFailed?.Invoke(ex);
            this.Error = ex.Message;
        }
    }
}
