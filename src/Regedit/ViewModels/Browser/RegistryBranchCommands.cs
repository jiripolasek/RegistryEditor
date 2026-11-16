// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed partial class RegistryBranchCommands(RegistryService registry)
{
    internal IRegistryBranchDialogs? Dialogs { get; set; }

    internal event Func<RegistryLocation?, Task>? TreeChanged;

    internal event Action<string, string, NoticeKind>? Notice;

    internal event Action<Exception>? Failed;

    internal event Action? RegistryActivityCompleted;

    private bool _closed;

    private static bool HasTarget(RegistryLocation? location)
    {
        return location is not null;
    }

    private static bool IsKey(RegistryLocation? location)
    {
        return location is { IsComputer: false };
    }

    private static bool CanLoad(RegistryLocation? parent)
    {
        return parent is { Hive: RegistryHive.LocalMachine or RegistryHive.Users, SubKey.Length: 0 };
    }

    private static bool CanUnload(RegistryLocation? location)
    {
        return CanLoad(location?.Parent);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task ExportAsync(RegistryLocation? location, CancellationToken token)
    {
        return this.ExportLocationAsync(location, location?.IsComputer == true, token);
    }

    internal Task ExportLocationAsync(RegistryLocation? location, bool all, CancellationToken token)
    {
        return this.WithDialogsAsync(
            location, async (target, dialogs) =>
            {
                var roots = target.GetScopeRoots(all);
                if (await dialogs.PickSaveAsync(all ? "Registry export" : target.Name, ".reg", "Registry file") is not
                    { } file)
                {
                    return;
                }

                token.ThrowIfCancellationRequested();
                this.Notice?.Invoke(
                    "Exporting…", "The destination is replaced only after export finishes. Press Esc to cancel.",
                    NoticeKind.Information);
                var skipped = await Task.Run(
                    () => RegistryExport.ExportAsync(registry, roots, file, token, all), token);
                this.Notice?.Invoke(
                    skipped == 0 ? "Export complete" : "Exported accessible keys",
                    file + (skipped == 0
                        ? string.Empty
                        : $" · {skipped} inaccessible branches were skipped and noted in the file."),
                    skipped == 0 ? NoticeKind.Success : NoticeKind.Warning);
            });
    }

    [RelayCommand(CanExecute = nameof(IsKey))]
    private Task SaveHiveAsync(RegistryLocation? location)
    {
        return this.WithDialogsAsync(
            location, async (target, dialogs) =>
            {
                if (IsKey(target) && await dialogs.PickSaveAsync(target.Name, ".hiv", "Registry hive") is { } file &&
                    !this._closed)
                {
                    await RegistryService.SaveHiveAsync(target, file);
                    this.Notice?.Invoke("Hive saved", file, NoticeKind.Success);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadHiveAsync(RegistryLocation? location)
    {
        return this.WithDialogsAsync(
            location, async (parent, dialogs) =>
            {
                if (!CanLoad(parent) || await dialogs.PickOpenAsync("*") is not { } file)
                {
                    return;
                }

                string? error = null;
                var previous = string.Empty;
                while (!this._closed && await dialogs.RequestMountNameAsync(file, previous, error) is { } name &&
                       !this._closed)
                {
                    previous = name;
                    RegistryLocation target;
                    try
                    {
                        target = parent.Child(name);
                        if ((await Task.Run(() => registry.SubKeys(parent))).Contains(
                                name, StringComparer.OrdinalIgnoreCase))
                        {
                            throw new IOException("That key already exists. Choose a new mount name.");
                        }

                        if (this._closed)
                        {
                            return;
                        }

                        await Task.Run(() => this.RecordHiveActivity(
                            "Load hive", target,
                            file + "\nHive mounting is not automatically reversible from the journal.", "load",
                            target.Address, file));
                    }
                    catch (Exception ex)
                    {
                        this.Failed?.Invoke(ex);
                        error = ex.Message;
                        continue;
                    }

                    await this.NotifyTreeChangedAsync(target);
                    return;
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanUnload))]
    private Task UnloadHiveAsync(RegistryLocation? location)
    {
        return this.WithDialogsAsync(
            location, async (target, dialogs) =>
            {
                if (!CanUnload(target) || !await dialogs.ConfirmAsync(
                        "Unload hive?",
                        $"{target.Address}\n\nChanges will be flushed to the hive file and the hive will be detached. This command requires administrator access.",
                        "Unload") || this._closed)
                {
                    return;
                }

                await Task.Run(() => this.RecordHiveActivity(
                    "Unload hive", target,
                    "Hive contents are flushed to its backing file. This operation is not automatically reversible from the journal.",
                    "unload", target.Address));
                await this.NotifyTreeChangedAsync(target.Parent!);
            });
    }

    private void RecordHiveActivity(
        string operation,
        RegistryLocation target,
        string details,
        params string[] arguments)
    {
        var journal = registry.Journal ??
                      throw new InvalidOperationException("The registry change journal is unavailable.");
        journal.RecordActivity(
            operation, target, details, () => RegistryService.RunRegAsync(arguments).GetAwaiter().GetResult());
    }

    private Task NotifyTreeChangedAsync(RegistryLocation? navigateTo = null)
    {
        return !this._closed && this.TreeChanged is { } changed ? changed(navigateTo) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsKey))]
    private Task PermissionsAsync(RegistryLocation? location)
    {
        return this.WithDialogsAsync(
            location, (target, dialogs) =>
            {
                if (IsKey(target))
                {
                    dialogs.ShowPermissions(target);
                    return this.NotifyTreeChangedAsync();
                }

                return Task.CompletedTask;
            });
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task PrintAsync(RegistryLocation? location, CancellationToken token)
    {
        return this.WithDialogsAsync(
            location, async (target, dialogs) =>
            {
                if (await dialogs.ChoosePrintRangeAsync(target) is not { } range)
                {
                    return;
                }

                var roots = target.GetScopeRoots(range == ReportRange.AllHives);
                var report = await Task.Run(
                    () => RegistryReport.Create(registry, roots, range != ReportRange.CurrentKey, token), token);
                token.ThrowIfCancellationRequested();
                await dialogs.PreviewReportAsync(target.Address, report, token);
            });
    }

    private async Task WithDialogsAsync(
        RegistryLocation? location,
        Func<RegistryLocation, IRegistryBranchDialogs, Task> action)
    {
        if (this._closed || location is null || this.Dialogs is not { } dialogs)
        {
            return;
        }

        using var connection = RemoteRegistryCredentials.Retain((string?[])[location.Machine]);
        try
        {
            await action(location, dialogs);
        }
        catch (OperationCanceledException)
        {
            if (!this._closed)
            {
                this.Notice?.Invoke("Canceled", "The operation was canceled.", NoticeKind.Information);
            }
        }
        catch (Exception ex)
        {
            this.Failed?.Invoke(ex);
            if (!this._closed)
            {
                this.Notice?.Invoke("Couldn't complete the operation", ex.Message, NoticeKind.Error);
            }
        }
        finally
        {
            this.RegistryActivityCompleted?.Invoke();
        }
    }

    internal void Close()
    {
        this._closed = true;
        this.ExportCommand.Cancel();
        this.PrintCommand.Cancel();
    }
}
