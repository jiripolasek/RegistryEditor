// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class RegistryUsageTab(RegistryService registry, RegistryLocation scope)
    : WindowTab("Registry Usage", "\uEB05")
{
    internal enum ScanKind
    {
        Navigate,
        Back,
        Forward
    }

    internal sealed record ScanTarget(RegistryLocation Location, ScanKind Kind = ScanKind.Navigate);

    internal event Action<RegistryLocation>? SelectionRequested;

    private readonly NavigationHistory _history = new(scope);
    private RegistryUsageEntry[] _subkeys = [];

    public RegistryLocation Scope { get; } = scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(Parent), nameof(UpTarget), nameof(CurrentTarget))]
    public partial RegistryLocation Location { get; private set; } = scope;

    [ObservableProperty]
    public partial RegistryUsageEntry[] Rows { get; private set; } = [];

    [ObservableProperty]
    public partial bool ShowParentKeyInList { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CoverageWarning { get; private set; } = string.Empty;

    private bool _scanning;

    public override bool IsBusy => this._scanning;
    public override string Tooltip => this.Scope.Address;

    public RegistryLocation? Parent => this.Location.IsSameLocation(this.Scope) ? null : this.Location.Parent;
    public string Description => $"{this.Location.Address} · {(this.Location.View == RegistryView.Registry32 ? 32 : 64)}-bit view";

    public ScanTarget? BackTarget => this._history.CanGoBack ? new(this._history.BackTarget, ScanKind.Back) : null;
    public ScanTarget? ForwardTarget => this._history.CanGoForward ? new(this._history.ForwardTarget, ScanKind.Forward) : null;
    public ScanTarget? UpTarget => this.Parent is { } parent ? new(parent) : null;
    public ScanTarget CurrentTarget => new(this.Location);

    internal RegistryLocation? SelectedSubkey { get; set; }

    internal bool IsParentRow(RegistryUsageEntry row) => row.Location.IsSameLocation(this.Parent);

    partial void OnShowParentKeyInListChanged(bool value)
    {
        if (!this.IsBusy)
        {
            this.PublishRows(this.SelectedSubkey);
        }
    }

    internal static string FormatCount(long count, string unit) => $"{count:N0} {unit}{(count == 1 ? string.Empty : "s")}";

    internal static string DescribePartialResults(long skippedBranches)
    {
        var incomplete = skippedBranches == 1
            ? "One branch could not be fully scanned."
            : $"{skippedBranches:N0} branches could not be fully scanned.";
        return incomplete + " The totals include only scanned data.";
    }

    private bool CanScan(ScanTarget? target)
    {
        return target is { Location.IsComputer: false } && (target.Kind switch
        {
            ScanKind.Back => target == this.BackTarget,
            ScanKind.Forward => target == this.ForwardTarget,
            _ => true
        });
    }

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(ScanTarget? target, CancellationToken token)
    {
        if (target is null || !this.CanScan(target))
        {
            return;
        }

        var location = target.Location;
        var selection = location.IsSameLocation(this.Location) ? this.SelectedSubkey
            : location.IsSameLocation(this.Parent) ? this.Location : null;
        this._history.CurrentSelection = this.SelectedSubkey is { } selected ? (selected, null) : null;
        switch (target.Kind)
        {
            case ScanKind.Back:
                this._history.Back();
                selection = this._history.CurrentSelection?.Location;
                break;
            case ScanKind.Forward:
                this._history.Forward();
                selection = this._history.CurrentSelection?.Location;
                break;
            default:
                this._history.Navigate(location);
                break;
        }

        this.Location = location;
        this.OnPropertyChanged(nameof(this.BackTarget));
        this.OnPropertyChanged(nameof(this.ForwardTarget));
        this.SelectedSubkey = null;
        this._subkeys = [];
        this.Rows = [];
        this.Summary = string.Empty;
        this.EmptyMessage = string.Empty;
        this.CoverageWarning = string.Empty;
        this._scanning = true;
        this.OnPropertyChanged(nameof(this.IsBusy));
        try
        {
            var report = await Task.Run(() => registry.MeasureUsage(location, token), token);
            token.ThrowIfCancellationRequested();
            var total = report.Total;
            this.Summary = $"{FormatCount(total.KeyCount, "key")} · {FormatCount(total.ValueCount, "value")}"
                           + $" · {FormatCount(total.ValueDataBytes, "byte")} of value data";
            this._subkeys = report.Subkeys;
            if (total.SkippedBranches > 0)
            {
                this.CoverageWarning = DescribePartialResults(total.SkippedBranches);
            }

            if (this._subkeys.Length == 0)
            {
                this.EmptyMessage = total.SkippedBranches > 0 ? "No subkeys could be listed." : "This key has no subkeys.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            this.EmptyMessage = "Scan canceled.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or SecurityException)
        {
            this.EmptyMessage = $"Couldn’t scan this branch. {ex.Message}";
        }
        finally
        {
            this._scanning = false;
            this.PublishRows(selection);
            this.OnPropertyChanged(nameof(this.IsBusy));
        }
    }

    private void PublishRows(RegistryLocation? selection)
    {
        this.Rows = this.ShowParentKeyInList && this.Parent is { } parent
            ? [new RegistryUsageEntry(parent, 0, 0, 0, 0), .. this._subkeys]
            : this._subkeys;
        if (selection is not null)
        {
            this.SelectionRequested?.Invoke(selection);
        }
    }
}
