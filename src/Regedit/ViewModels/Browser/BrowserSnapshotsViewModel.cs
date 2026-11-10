// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class BrowserSnapshotsViewModel(RegistryService registry) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private bool _closed;

    internal event Func<Task<string?>>? SaveFileRequested;

    internal event Func<Task<string?>>? OpenFileRequested;

    internal event Func<RegistryLocation, Task<bool>>? RemoteConfirmationRequested;

    internal event Func<RegistryComparison, Task>? ComparisonRequested;

    internal event Action<string, string, NoticeKind>? Notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(CaptureCommand), nameof(CompareFileCommand), nameof(CompareLastCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareLastCommand))]
    internal partial RegistryBranchSnapshot? LastSnapshot { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowComparisonCommand))]
    internal partial RegistryComparison? LastComparison { get; private set; }

    private bool IsIdle => !this._closed && !this.IsBusy;

    private bool CanCapture(RegistryLocation? location)
    {
        return this.IsIdle && location is { IsComputer: false };
    }

    private bool CanCompareLast => this.IsIdle && this.LastSnapshot is not null;

    private bool HasComparison => this.LastComparison is not null;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private Task CaptureAsync(RegistryLocation? location)
    {
        return !this.CanCapture(location)
            ? Task.CompletedTask
            : this.RunAsync(async token =>
            {
                using var connection = RemoteRegistryCredentials.Retain([location!.Machine]);
                if (this.SaveFileRequested is null || await this.SaveFileRequested() is not { } file)
                {
                    return;
                }

                this.ReportReading("Capturing branch…");
                var snapshot = await Task.Run(() => RegistryBranchSnapshot.Capture(registry, location!, token), token);
                await Task.Run(() => snapshot.SaveAsync(file, token), token);
                token.ThrowIfCancellationRequested();
                this.LastSnapshot = snapshot;
                this.Notice?.Invoke(
                    "Snapshot saved",
                    $"{snapshot.Keys.Length:N0} keys · {snapshot.Keys.Sum(key => key.Values.Length):N0} values\n{file}",
                    NoticeKind.Success);
            });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task CompareFileAsync()
    {
        return this.RunAsync(async token =>
        {
            if (this.OpenFileRequested is not null && await this.OpenFileRequested() is { } file)
            {
                var snapshot = await Task.Run(() => RegistryBranchSnapshot.LoadAsync(file, token), token);
                await this.CompareAsync(snapshot, token);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanCompareLast))]
    private Task CompareLastAsync()
    {
        return this.LastSnapshot is { } before
            ? this.RunAsync(token => this.CompareAsync(before, token))
            : Task.CompletedTask;
    }

    private async Task CompareAsync(RegistryBranchSnapshot before, CancellationToken token)
    {
        using var connection = RemoteRegistryCredentials.Retain([before.Root.Machine]);
        token.ThrowIfCancellationRequested();
        if (before.Root.Machine is null && !before.Computer.Equals(
                Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This local snapshot belongs to {before.Computer}. Compare it on that computer.");
        }

        if (before.Root.Machine is not null && (this.RemoteConfirmationRequested is null ||
                                                !await this.RemoteConfirmationRequested(before.Root)))
        {
            return;
        }

        this.ReportReading("Comparing branch…");
        var comparison = await Task.Run(
            () => new RegistryComparison(
                before, RegistryBranchSnapshot.Capture(registry, before.Root, token, true), token), token);
        token.ThrowIfCancellationRequested();
        this.LastSnapshot = before;
        this.LastComparison = comparison;
        this.Notice?.Invoke(
            "Comparison complete", $"{comparison.Differences.Count:N0} differences in {before.Root.Address}",
            NoticeKind.Information);
        await this.ShowComparisonAsync();
    }

    [RelayCommand(CanExecute = nameof(HasComparison))]
    private Task ShowComparisonAsync()
    {
        return this.LastComparison is { } result && this.ComparisonRequested is { } show
            ? show(result)
            : Task.CompletedTask;
    }

    private void ReportReading(string title)
    {
        this.Notice?.Invoke(
            title,
            "Reading keys, types, and raw values. No registry data is changed. Inaccessible branches stop the capture.",
            NoticeKind.Information);
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (!this.IsIdle)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        this._cancellation = cancellation;
        this.IsBusy = true;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!this._closed)
            {
                this.Notice?.Invoke("Canceled", "The operation was canceled.", NoticeKind.Information);
            }
        }
        catch (Exception ex)
        {
            if (!this._closed)
            {
                this.Notice?.Invoke("Snapshot failed", ex.Message, NoticeKind.Error);
            }
        }
        finally
        {
            this._cancellation = null;
            this.IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    internal void Cancel()
    {
        this._cancellation?.Cancel();
    }

    internal void Close()
    {
        this._closed = true;
        this.Cancel();
    }
}
