// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Security;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Interop;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Printing;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Printing;
using Windows.Storage.Pickers;

namespace JPSoftworks.Regedit.Dialogs;

internal sealed class RegistryDialogs(MainWindow window) : IRegistryBranchDialogs
{
    public async Task<string?> PickOpenAsync(string extension)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);
        picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickSaveAsync(string name, string extension, string description)
    {
        var picker = new FileSavePicker { SuggestedFileName = name };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);
        picker.FileTypeChoices.Add(description, new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    internal async Task<bool> ShowFormAsync(
        string title,
        object content,
        string primary,
        Func<Task> accept,
        [CallerMemberName] string operation = "",
        ContentDialogButton defaultButton = ContentDialogButton.Primary,
        Control? initialFocus = null)
    {
        var dialog = new AppDialog(title, content, primary, defaultButton: defaultButton);
        if (initialFocus is not null)
        {
            dialog.FocusOnOpen(initialFocus);
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                await accept();
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    window.Logger.LogFailure(LogLevel.Warning, operation, ex);
                }

                args.Cancel = true;
                dialog.ShowError(ex.Message);
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        return await window.Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public Task<bool> RequestComputerNameAsync(Func<string, Task> connect)
    {
        var form = new NameForm(
            "Computer name", placeholder: "SERVER01",
            hint:
            "Uses your Windows credentials first. If sign-in fails or access is denied, Windows asks you to sign in with another account. The remote computer must allow Remote Registry connections.");
        return this.ShowFormAsync(
            "Connect network registry", form, "Connect", () =>
            {
                var computer = form.Value;
                return NetworkRegistryConnection.ConnectAsync(window.Handle, computer, () => connect(computer));
            }, "Connect network registry", ContentDialogButton.Close, form.Input);
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept)
    {
        return this.ShowFormAsync(
            title, message, accept, () => Task.CompletedTask, defaultButton: ContentDialogButton.Close);
    }

    public async Task<string?> RequestMountNameAsync(string file, string previous, string? error)
    {
        var form = new NameForm("Mount key name", previous, file);
        var dialog = new AppDialog("Load hive", form, "Load", defaultButton: ContentDialogButton.Close);
        dialog.FocusOnOpen(form.Input);
        if (error is not null)
        {
            dialog.ShowError(error);
        }

        return await window.Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? form.Value : null;
    }

    public async Task<ReportRange?> ChoosePrintRangeAsync(RegistryLocation location)
    {
        if (!PrintManager.IsSupported())
        {
            throw new InvalidOperationException("Printing is not available on this computer.");
        }

        var form = new PrintRangeForm(location.IsComputer);
        return await this.ShowFormAsync(
            "Print registry", form, "Preview", () => Task.CompletedTask,
            initialFocus: location.IsComputer ? null : form.Input)
            ? form.Range
            : null;
    }

    public Task PreviewReportAsync(string title, string report, CancellationToken token)
    {
        return new RegistryPrintDocument(title, report).ShowAsync(window.Handle, token);
    }

    public void ShowPermissions(RegistryLocation target)
    {
        SecurityEditor.Show(window.Handle, target, window.Journal);
    }
}
