// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class SnapshotComparisonViewModel(
    RegistryComparison comparison,
    Func<Task<string?>> saveFile,
    Action<string> copy) : ObservableObject
{
    public RegistryComparison Comparison { get; } = comparison;

    private RegistryBranchSnapshot Before => this.Comparison.Before;

    public string Scope =>
        $"{this.Before.Root.Address} · {this.Before.Computer} · {(this.Before.Root.View == RegistryView.Registry32 ? "32" : "64")}-bit";

    public string Summary =>
        $"{this.Comparison.Differences.Count:N0} differences · Before: {this.Before.Completed.ToLocalTime():G} · After: {this.Comparison.After.Completed.ToLocalTime():G}\nCaptures read the branch over time. Pause other writes for a stable comparison. Exports apply the selected after-state.";

    public bool NoDifferences => this.Comparison.Differences.Count == 0;

    internal event Action<string, string, NoticeKind>? Notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CopyPowerShellCommand), nameof(CopyRegExeCommand))]
    internal partial IReadOnlyList<RegistryDifference> Selected { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BeforeDetail), nameof(AfterDetail), nameof(Target), nameof(HasTarget))]
    private partial RegistryDifference? Current { get; set; }

    public bool HasTarget => this.Current is not null;

    public string Target => this.Current is { } item
        ? item.Location.Address +
          (item.Before is not null || item.After is not null ? "\r\n" + item.Name : string.Empty)
        : string.Empty;

    public string BeforeDetail => this.Current is { } item ? Detail(item.Before, item.BeforeText) : string.Empty;

    public string AfterDetail => this.Current is { } item ? Detail(item.After, item.AfterText) : string.Empty;

    internal void Select(IReadOnlyList<RegistryDifference> selected, RegistryDifference? current)
    {
        this.Selected = selected;
        this.Current = current ?? selected.LastOrDefault();
    }

    private bool HasSelection => this.Selected.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportAsync(CancellationToken token)
    {
        var selected = this.Selected.ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        try
        {
            if (await saveFile() is { } file)
            {
                await Task.Run(() => this.Comparison.ExportAsync(selected, file, token), token);
                token.ThrowIfCancellationRequested();
                this.Notice?.Invoke("Differences exported", file, NoticeKind.Success);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.Notice?.Invoke("Could not export", ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPowerShell()
    {
        this.CopyScript(RegistryScripts.PowerShell);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyRegExe()
    {
        this.CopyScript(RegistryScripts.RegExe);
    }

    private void CopyScript(Func<IReadOnlyList<RegistryDifference>, string> format)
    {
        try
        {
            var selected = this.Comparison.ValidateSelection(this.Selected);
            copy(format(this.Comparison.Differences.Where(selected.Contains).ToArray()));
            this.Notice?.Invoke(
                "Script copied", "Review it before running. Copying does not change the registry.", NoticeKind.Success);
        }
        catch (Exception ex)
        {
            this.Notice?.Invoke("Could not copy script", ex.Message, NoticeKind.Error);
        }
    }

    private static string Detail(RegistryValue? value, string text)
    {
        if (value is null)
        {
            return text;
        }

        var preview = text.Length <= 4096 ? text : text[..4096] + "\n[Preview shortened]";
        var bytes = Convert.ToHexString(value.Data.AsSpan(0, Math.Min(value.Data.Length, 1024)));
        return
            $"{value.TypeName} · {value.Data.Length:N0} bytes\n{preview}\n\nRaw bytes: {bytes}{(value.Data.Length > 1024 ? "… (preview)" : string.Empty)}";
    }
}
