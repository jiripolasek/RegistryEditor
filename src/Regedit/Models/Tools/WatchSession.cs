// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Infrastructure.Collections;

namespace JPSoftworks.Regedit.Models.Tools;

public sealed class WatchSession : ObservableObject
{
    private readonly object _gate = new();

    private readonly TaskCompletionSource<bool>
        _baselineReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly BulkObservableCollection<WatchObservation> _observations = [];
    private CancellationTokenSource? _cancellation = new();
    private Exception? _error;
    private string _status = "Capturing baseline…";
    private bool _running = true;

    public WatchDefinition Definition { get; }

    public ObservableCollection<WatchObservation> Observations => this._observations;

    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    public Exception? Error { get => this._error; private set => this.SetProperty(ref this._error, value); }

    public string Target => Environment.MachineName + " · " + this.Definition.Location.Address +
                            (this.Definition.Location.View == Microsoft.Win32.RegistryView.Registry32
                                ? " · 32-bit"
                                : " · 64-bit") + (this.Definition.ValueName is { } name
                                ? " · Value: " + (name.Length == 0 ? "(Default)" : name)
                                : " · Branch");

    public string Title => this.Definition.Location.Name + (this.Definition.ValueName is { } name
        ? " · " + (name.Length == 0 ? "(Default)" : name)
        : string.Empty);

    public string Status
    {
        get => this._status;
        private set
        {
            if (this.SetProperty(ref this._status, value))
            {
                this.OnPropertyChanged(nameof(this.DisplayTitle));
                this.OnPropertyChanged(nameof(this.CaptureLabel));
            }
        }
    }

    public string DisplayTitle => this.ToString();

    public string CaptureLabel => (this.IsRunning ? "Active" : "Stopped") + " · " + this.Started.ToString("d") + " " +
                                  this.Started.ToString("HH:mm:ss.fff") + " · " + this.Title;

    public bool IsRunning
    {
        get => this._running;
        private set
        {
            if (this.SetProperty(ref this._running, value))
            {
                this.OnPropertyChanged(nameof(this.CaptureLabel));
            }
        }
    }

    public Task Completion { get; }

    internal Task<bool> BaselineReady => this._baselineReady.Task;

    public override string ToString()
    {
        return this.Definition.Location.Address +
               (this.Definition.Location.View == Microsoft.Win32.RegistryView.Registry32 ? " · 32-bit" : " · 64-bit") +
               (this.Definition.ValueName is { } name
                   ? " · " + (name.Length == 0 ? "(Default)" : name)
                   : string.Empty) + " · " + this.Started.ToString("d") + " " + this.Started.ToString("HH:mm:ss.fff") +
               " · " + this.Status;
    }

    public WatchSession(RegistryService registry, WatchDefinition definition, Func<Action, Task> dispatch)
    {
        RegistryWorkspace.ValidateLocation(definition.Location);
        if (definition.Location.Machine is not null)
        {
            throw new ArgumentException("Live watch supports local registry keys only.");
        }

        if (definition.ValueName is { } valueName)
        {
            RegistryService.ValidateValueName(valueName);
        }

        this.Definition = definition;
        this.Completion = this.RunAsync(registry, dispatch, this._cancellation.Token);
    }

    public void Stop()
    {
        lock (this._gate)
        {
            this._cancellation?.Cancel();
        }
    }

    private async Task RunAsync(RegistryService registry, Func<Action, Task> dispatch, CancellationToken token)
    {
        Exception? failure = null;
        Task? watcher = null;
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher = RegistryWatcher.RunAsync(
                registry, this.Definition.Location, comparison => dispatch(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var changes = comparison.Differences;
                    if (this.Observations.Count + changes.Count > 10000)
                    {
                        throw new IOException(
                            "The 10,000-observation limit was reached. Earlier observations are retained; restart to capture a fresh session.");
                    }

                    this._observations.AddRange(
                        changes.Select(change => new WatchObservation(change, comparison.After.Completed)));
                    var count = this.Observations.Count;
                    this.Status = count == 1 ? "Watching · 1 observation" : $"Watching · {count:N0} observations";
                }), () => ready.TrySetResult(), token, this.Definition.ValueName);
            await Task.WhenAny(ready.Task, watcher).ConfigureAwait(false);
            if (ready.Task.IsCompleted)
            {
                await dispatch(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (this.Observations.Count == 0)
                    {
                        this.Status = "Watching";
                    }
                }).ConfigureAwait(false);
                this._baselineReady.TrySetResult(true);
            }

            await watcher.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            this.Stop();
            if (watcher is not null)
            {
                try
                {
                    await watcher.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            var status = failure is null ? "Stopped" : "Stopped: " + failure.Message;
            try
            {
                await dispatch(() =>
                {
                    this.Error = failure;
                    this.Status = status;
                    this.IsRunning = false;
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The window dispatcher can disappear during shutdown; retain state without notifying detached views.
                this._error = failure;
                this._status = status;
                this._running = false;
            }

            lock (this._gate)
            {
                this._cancellation?.Dispose();
                this._cancellation = null;
            }

            this._baselineReady.TrySetResult(false);
        }
    }
}
