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
    public RegistryLocation Scope { get; } = scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(Parent))]
    public partial RegistryLocation Location { get; private set; } = scope;

    [ObservableProperty]
    public partial RegistryUsageEntry[] Rows { get; private set; } = [];

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

    internal static string FormatCount(long count, string unit) => $"{count:N0} {unit}{(count == 1 ? string.Empty : "s")}";

    internal static string DescribePartialResults(long skippedBranches)
    {
        var incomplete = skippedBranches == 1
            ? "One branch could not be fully scanned."
            : $"{skippedBranches:N0} branches could not be fully scanned.";
        return incomplete + " The totals include only scanned data.";
    }

    private static bool CanScan(RegistryLocation? location) => location is { IsComputer: false };

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(RegistryLocation? location, CancellationToken token)
    {
        if (location is null)
        {
            return;
        }

        this.Location = location;
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
            this.Rows = report.Subkeys;
            if (total.SkippedBranches > 0)
            {
                this.CoverageWarning = DescribePartialResults(total.SkippedBranches);
            }

            if (this.Rows.Length == 0)
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
            this.OnPropertyChanged(nameof(this.IsBusy));
        }
    }
}
