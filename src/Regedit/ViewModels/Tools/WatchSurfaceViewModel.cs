// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class WatchSurfaceViewModel : ObservableObject
{
    internal event Func<Task<WatchDefinition?>>? TargetRequested;

    internal event Action<string, Exception>? ActionFailed;

    private readonly MainWindowViewModel _window;
    private BrowserViewModel? _browser;
    private bool _active;
    private int _seenCount;
    private WatchObservation? _lastSeen;

    internal BulkObservableCollection<WatchObservation> VisibleObservations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Context), nameof(CaptureStatus), nameof(PendingText), nameof(Target), nameof(HasCapture), nameof(IsCapturing))]
    public partial WatchSession? Selected { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel), nameof(PendingText))]
    public partial bool IsViewingPaused { get; set; }

    [ObservableProperty]
    public partial bool IsFollowing { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public IReadOnlyList<WatchObservation> SelectedObservations { get; set; } = [];

    public string PauseLabel => this.IsViewingPaused ? "Resume viewing" : "Pause viewing";

    public int PendingCount => Math.Max(0, (this.Selected?.Observations.Count ?? 0) - this._seenCount);

    public bool HasCapture => this.Selected is not null;

    public bool HasMultipleCaptures => this._window.Watches.Count > 1;

    public bool IsCapturing => this.Selected?.IsRunning == true;

    public string Target => this.Selected?.Target ?? string.Empty;

    public string PendingText => !this.HasCapture
        ? string.Empty
        :
        !this.IsCapturing
            ? "Capture stopped · Observations retained"
            :
            this.IsViewingPaused
                ?
                $"Viewing paused · {this.PendingCount:N0} new {(this.PendingCount == 1 ? "observation" : "observations")} · Capture continues"
                : this.IsFollowing
                    ? "Following latest observations"
                    : "Reading earlier observations · Follow latest to return";

    public string Context => this.Selected is { } session
        ? session.Status + " · Started " + session.Started.ToLocalTime().ToString("G")
        : "Choose an explicit local registry target. Observations are retained for this window.";

    public string CaptureStatus => this.Selected?.Status ?? string.Empty;

    private RegistryLocation? CurrentLocation =>
        this._window.ContextLocation is { IsComputer: false, Machine: null } location ? location : null;

    private RegistryEntry? CurrentValue =>
        this._window.ContextBrowser?.SelectedEntries is [var row] && row is { IsKey: false, IsParent: false } ? row : null;

    public string BranchTarget => FormatTarget(this._window.ContextLocation, null);

    public string ValueTarget => this.CurrentValue is { } row
        ? FormatTarget(this._window.ContextLocation, row.Value?.Name ?? string.Empty)
        : "Select one value in the registry tab, including (Default) if it has not been set.";

    private bool CanStartBranch =>
        this.CurrentLocation is not null && !this._window.IsClosing && !this._window.CanCloseWindow;

    private bool CanStartValue => this.CanStartBranch && this.CurrentValue is not null;

    private bool CanStop => this.Selected?.IsRunning == true;

    private bool CanRestart =>
        this.Selected?.IsRunning == false && !this._window.IsClosing && !this._window.CanCloseWindow;

    private bool HasSession => this.Selected is not null;

    private bool HasObservations => this.Selected?.Observations.Count > 0;

    internal WatchSurfaceViewModel(MainWindowViewModel window) { this._window = window; }

    [RelayCommand]
    private async Task NewAsync()
    {
        try
        {
            if (this.TargetRequested is { } request && await request() is { } target)
            {
                this.Start(target);
            }
        }
        catch (Exception error)
        {
            this.Fail("Start watch", error);
        }
    }

    internal static string FormatTarget(RegistryLocation? location, string? valueName)
    {
        return location is null
            ? "Select a registry key first. Live watches support local keys only."
            : (location.Machine ?? Environment.MachineName)
              + " · "
              + location.Address
              + (location.View == RegistryView.Registry32 ? " · 32-bit" : " · 64-bit")
              + (valueName is null ? " · Branch" : " · Value: " + (valueName.Length == 0 ? "(Default)" : valueName));
    }

    partial void OnSelectedChanging(WatchSession? value) { this.DetachSession(); }

    partial void OnSelectedChanged(WatchSession? value)
    {
        this._seenCount = 0;
        this._lastSeen = null;
        this.SelectedObservations = [];
        this.IsViewingPaused = false;
        this.IsFollowing = true;
        this.Error = null;
        this.AttachSession();
        this.RefreshVisible(true);
        this.RefreshCommands();
    }

    partial void OnQueryChanged(string value) { this.RefreshVisible(true); }

    partial void OnIsViewingPausedChanged(bool value)
    {
        if (value)
        {
            this.IsFollowing = false;
        }
        else
        {
            this.RefreshVisible(false);
        }
    }

    partial void OnIsFollowingChanged(bool value) { this.OnPropertyChanged(nameof(this.PendingText)); }

    internal void Activate()
    {
        if (this._active)
        {
            return;
        }

        this._active = true;
        this._window.PropertyChanged += this.WindowChanged;
        this._window.Watches.CollectionChanged += this.SessionsChanged;
        this.AttachSession();
        this.UpdateTargets();
        if (this.Selected is null || !this._window.Watches.Contains(this.Selected))
        {
            this.Selected = this._window.Watches.LastOrDefault();
        }

        this.IsViewingPaused = this.IsCapturing && this.IsViewingPaused;
        this.RefreshVisible(false);
        this.RefreshCommands();
    }

    internal void Deactivate()
    {
        this.DetachSession();
        this._active = false;
        this._window.PropertyChanged -= this.WindowChanged;
        this._window.Watches.CollectionChanged -= this.SessionsChanged;
        if (this._browser is not null)
        {
            this._browser.PropertyChanged -= this.BrowserChanged;
        }

        this._browser = null;
    }

    private void AttachSession()
    {
        if (!this._active || this.Selected is null)
        {
            return;
        }

        this.Selected.Observations.CollectionChanged += this.ObservationsChanged;
        this.Selected.PropertyChanged += this.SessionChanged;
    }

    private void DetachSession()
    {
        if (this.Selected is null)
        {
            return;
        }

        this.Selected.Observations.CollectionChanged -= this.ObservationsChanged;
        this.Selected.PropertyChanged -= this.SessionChanged;
    }

    private void WindowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainWindowViewModel.ContextBrowser) or nameof(MainWindowViewModel.ContextLocation))
        {
            this.UpdateTargets();
        }
    }

    private void BrowserChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(BrowserViewModel.SelectedEntries) or nameof(BrowserViewModel.Location))
        {
            this.UpdateTargets();
        }
    }

    private void SessionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        this.OnPropertyChanged(nameof(this.HasMultipleCaptures));
        if (this.Selected is null || !this._window.Watches.Contains(this.Selected))
        {
            this.Selected = this._window.Watches.LastOrDefault();
        }
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs args)
    {
        this.IsViewingPaused = this.IsCapturing && this.IsViewingPaused;
        this.OnPropertyChanged(nameof(this.Context));
        this.OnPropertyChanged(nameof(this.CaptureStatus));
        this.OnPropertyChanged(nameof(this.IsCapturing));
        this.OnPropertyChanged(nameof(this.PendingText));
        this.RefreshCommands();
    }

    private void ObservationsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        this.RefreshVisible(false);
    }

    private void UpdateTargets()
    {
        if (this._browser is not null)
        {
            this._browser.PropertyChanged -= this.BrowserChanged;
        }

        this._browser = this._window.ContextBrowser;
        if (this._active && this._browser is not null)
        {
            this._browser.PropertyChanged += this.BrowserChanged;
        }

        this.OnPropertyChanged(nameof(this.BranchTarget));
        this.OnPropertyChanged(nameof(this.ValueTarget));
        this.StartBranchCommand.NotifyCanExecuteChanged();
        this.StartValueCommand.NotifyCanExecuteChanged();
    }

    private bool Matches(WatchObservation observation)
    {
        var change = observation.Change;
        return new[] { change.Change, change.Name, change.Location.Address, change.Types }.Any(text => text.Contains(
            this.Query, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshVisible(bool replace)
    {
        var observations = this.Selected?.Observations;
        var count = observations?.Count ?? 0;
        if (this._seenCount > 0 && (count < this._seenCount || !ReferenceEquals(observations![this._seenCount - 1], this._lastSeen)))
        {
            this._seenCount = 0;
            this._lastSeen = null;
            this.SelectedObservations = [];
            replace = true;
        }

        var end = this.IsViewingPaused ? Math.Min(count, this._seenCount) : count;
        if (replace)
        {
            this.VisibleObservations.ReplaceAll(observations?.Take(end).Where(this.Matches) ?? []);
        }
        else
        {
            this.VisibleObservations.AddRange(observations?.Skip(this._seenCount).Take(end - this._seenCount).Where(this.Matches) ?? []);
        }

        this._seenCount = end;
        this._lastSeen = end > 0 ? observations![end - 1] : null;
        if (replace && this.SelectedObservations.Count > 0)
        {
            var visible = new HashSet<WatchObservation>(this.VisibleObservations, ReferenceEqualityComparer.Instance);
            this.SelectedObservations = this.SelectedObservations.Where(visible.Contains).ToArray();
        }

        this.OnPropertyChanged(nameof(this.PendingCount));
        this.OnPropertyChanged(nameof(this.PendingText));
        this.RefreshCommands();
    }

    private void RefreshCommands()
    {
        this.StopCommand.NotifyCanExecuteChanged();
        this.RestartCommand.NotifyCanExecuteChanged();
        this.RemoveCommand.NotifyCanExecuteChanged();
        this.ClearCommand.NotifyCanExecuteChanged();
        this.PauseViewingCommand.NotifyCanExecuteChanged();
        this.FollowLatestCommand.NotifyCanExecuteChanged();
        this.OpenCommand.NotifyCanExecuteChanged();
        this.OpenBackgroundCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartBranch))]
    private void StartBranch()
    {
        if (this.CanStartBranch)
        {
            this.Start(new WatchDefinition(this.CurrentLocation!));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartValue))]
    private void StartValue()
    {
        if (this.CanStartValue)
        {
            this.Start(new WatchDefinition(this.CurrentLocation!, this.CurrentValue!.Value?.Name ?? string.Empty));
        }
    }

    private void Start(WatchDefinition definition)
    {
        try
        {
            this.Error = null;
            this.Selected = this._window.StartWatch(definition.Location, definition.ValueName, this._window.DispatchWatchUpdate);
        }
        catch (Exception error)
        {
            this.Fail("Start watch", error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() { this.Selected?.Stop(); }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        if (this.CanRestart)
        {
            this.Start(this.Selected!.Definition);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Remove()
    {
        if (this.Selected is { IsRunning: false } session)
        {
            this._window.RemoveWatch(session);
        }
    }

    [RelayCommand(CanExecute = nameof(HasObservations))]
    private void Clear() { this.Selected?.Observations.Clear(); }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void PauseViewing() { this.IsViewingPaused = !this.IsViewingPaused; }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void FollowLatest()
    {
        this.IsFollowing = true;
        this.IsViewingPaused = false;
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task OpenAsync()
    {
        if (this.Selected is not { } session)
        {
            return;
        }

        try
        {
            await this._window.NavigateFromToolsAsync(session.Definition.Location, true);
        }
        catch (Exception error)
        {
            this.Fail("Open watched key", error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void OpenBackground()
    {
        if (this.Selected is { } session)
        {
            this._window.OpenBrowser(session.Definition.Location, select: false);
        }
    }

    private void Fail(string operation, Exception error)
    {
        this.Error = error.Message;
        this.ActionFailed?.Invoke(operation, error);
    }

    internal void CopyFrom(WatchSurfaceViewModel source)
    {
        var active = this._active;
        this.Deactivate();
        this.Selected = source.Selected;
        this.Query = source.Query;
        this.IsViewingPaused = source.IsViewingPaused;
        this.IsFollowing = source.IsFollowing;
        this._seenCount = source._seenCount;
        this._lastSeen = source._lastSeen;
        this.VisibleObservations.ReplaceAll(source.VisibleObservations);
        this.SelectedObservations = source.SelectedObservations.ToArray();
        this.OnPropertyChanged(nameof(this.PendingText));
        if (active)
        {
            this.Activate();
        }
    }
}
