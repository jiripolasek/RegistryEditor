// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Interop;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Shell;

/// <summary>Application-menu registry operations: import, full export, network connection and elevation.</summary>
internal sealed partial class ApplicationRegistryViewModel(
    MainWindowViewModel window,
    RegistryService registry,
    JournalViewModel journal,
    IRegistryBranchDialogs dialogs,
    RegistryBranchCommands branches,
    ILogger logger,
    bool isAdministrator,
    Action<ProcessStartInfo>? startProcess = null,
    Action<string, RegistryLocation>? startStandardAccess = null)
{
    private readonly Action<ProcessStartInfo> _startProcess = startProcess ?? (info => Process.Start(info));

    private readonly Action<string, RegistryLocation> _startStandardAccess
        = startStandardAccess ?? RegistryLaunch.StartStandardAccess;

    internal event Action<string, string, NoticeKind>? Notice;

    [RelayCommand]
    private async Task ImportAsync()
    {
        var browser = window.ContextBrowser;
        var location = browser?.Location ?? new RegistryLocation();
        await this.GuardAsync(async () =>
        {
            if (location.Machine is not null)
            {
                throw new InvalidOperationException(
                    "Registry files are imported on this computer. Switch to a local registry tab first.");
            }

            var view = location.View;
            var file = await dialogs.PickOpenAsync(".reg");
            if (file is null)
            {
                return;
            }

            if (!await dialogs.ConfirmAsync(
                    "Import registry file?",
                    $"{file}\n\nThis file can add, replace, or delete keys and values anywhere in this computer's {(view == RegistryView.Registry32 ? "32" : "64")}-bit registry. Only import files you trust. Import is not atomic: changes made before an error cannot be automatically undone.",
                    "Import"))
            {
                return;
            }

            await Task.Run(() => journal.Journal.RecordActivity(
                "Import registry file", location,
                file +
                "\nWindows imports may affect any branch. Previous values are not retained for automatic revert.",
                () => RegistryService.RunRegAsync(
                    "import", file, view == RegistryView.Registry32 ? "/reg:32" : "/reg:64").GetAwaiter().GetResult()));
            if (browser is not null)
            {
                await browser.RefreshAfterImportAsync();
            }

            this.Notice?.Invoke("Import complete", Path.GetFileName(file), NoticeKind.Success);
        });
    }

    [RelayCommand]
    private Task ExportAllAsync(CancellationToken token)
    {
        return branches.ExportLocationAsync(window.ContextLocation ?? new RegistryLocation(), true, token);
    }

    /// <summary>The HKEY_LOCAL_MACHINE root of a typed computer name, accepting a leading or trailing backslash.</summary>
    internal static RegistryLocation RemoteRoot(string computer, RegistryView view)
    {
        return RegistryLocation.Parse($"\\\\{computer.Trim().Trim('\\')}\\HKEY_LOCAL_MACHINE", view);
    }

    internal static void ProbeRemote(RegistryLocation target, Action<RegistryLocation> open)
    {
        try
        {
            open(target);
        }
        catch (Exception ex) when (NetworkRegistryConnection.NeedsCredentials(ex))
        {
            open(target with { Hive = RegistryHive.Users });
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var view = window.ContextLocation?.View ?? RegistryView.Registry64;
        await this.GuardAsync(async () =>
        {
            RegistryLocation? target = null;
            IDisposable? connection = null;
            try
            {
                if (await dialogs.RequestComputerNameAsync(async computer =>
                    {
                        target = RemoteRoot(computer, view);
                        var pending = RemoteRegistryCredentials.Retain(target.Machine!);
                        connection?.Dispose();
                        connection = pending;
                        await Task.Run(() => ProbeRemote(
                            target, location =>
                            {
                                using var key = registry.Open(location);
                            }));
                    }) && !window.IsClosing && !window.CanCloseWindow)
                {
                    window.OpenBrowser(target!.GetComputer());
                }
            }
            finally
            {
                connection?.Dispose();
            }
        });
    }

    private bool CanElevate => !isAdministrator;

    private bool CanOpenStandardAccess => isAdministrator;

    [RelayCommand(CanExecute = nameof(CanOpenStandardAccess))]
    private Task OpenStandardAccessAsync()
    {
        if (!this.CanOpenStandardAccess)
        {
            return Task.CompletedTask;
        }

        var location = window.ContextLocation ?? new RegistryLocation();
        return this.GuardAsync(() =>
        {
            this._startStandardAccess(Environment.ProcessPath!, location);
            return Task.CompletedTask;
        });
    }

    [RelayCommand(CanExecute = nameof(CanElevate))]
    private Task ElevateAsync()
    {
        return this.ElevateLocationAsync(window.ContextLocation ?? new RegistryLocation());
    }

    internal Task ElevateLocationAsync(RegistryLocation location)
    {
        if (!this.CanElevate)
        {
            return Task.CompletedTask;
        }

        return this.GuardAsync(() =>
        {
            this._startProcess(RegistryLaunch.CreateElevationStartInfo(Environment.ProcessPath!, location));
            return Task.CompletedTask;
        });
    }

    private async Task GuardAsync(Func<Task> action, [CallerMemberName] string operation = "")
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            this.Notice?.Invoke("Canceled", "The operation was canceled.", NoticeKind.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            this.Notice?.Invoke("Canceled", "The operation was canceled.", NoticeKind.Information);
        }
        catch (Exception ex)
        {
            logger.LogFailure(LogLevel.Warning, operation, ex);
            this.Notice?.Invoke("Couldn't complete the operation", ex.Message, NoticeKind.Error);
        }
        finally
        {
            journal.RegistryActivityCompleted();
        }
    }
}
