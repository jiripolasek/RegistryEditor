// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Views.Files;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private readonly List<RegistryFileWindow> _registryFileWindows = [];

    private async Task OpenRegistryFileAsync(string file, RegistryView view)
    {
        if (!this.ViewModel.CanOpenRegistryFile)
        {
            return;
        }

        var preview = new RegistryFileWindow(
            this.AppWindow, this.Shell.RequestedTheme, this.Settings, this.Shell.XamlRoot.RasterizationScale,
            dialogs => this.RegistryMenu.CreateFilePreview(view, dialogs));
        this.ViewModel.RegisterRegistryFile(preview.ViewModel);
        preview.ViewModel.OpenKeyRequested += this.ActivateForRegistryFileKey;
        preview.ViewModel.SettingsRequested += this.Activate;
        this._registryFileWindows.Add(preview);
        preview.Closed += (_, _) =>
        {
            this._registryFileWindows.Remove(preview);
            this.ViewModel.UnregisterRegistryFile(preview.ViewModel);
            preview.ViewModel.OpenKeyRequested -= this.ActivateForRegistryFileKey;
            preview.ViewModel.SettingsRequested -= this.Activate;
        };
        await preview.OpenAsync(file);
    }

    private void RegistryFile_DragOver(object sender, DragEventArgs args)
    {
        this.HandleRegistryFileDrag(args, false);
    }

    private void RegistryFile_Drop(object sender, DragEventArgs args)
    {
        this.HandleRegistryFileDrag(args, true);
    }

    private async void HandleRegistryFileDrag(DragEventArgs args, bool openPreview)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            args.Handled = true;
            args.AcceptedOperation = DataPackageOperation.None;
            if (!this.ViewModel.CanOpenRegistryFile || this.ViewModel.IsDialogOpen ||
                this.RegistryMenu.ImportCommand.IsRunning)
            {
                return;
            }

            var items = await args.DataView.GetStorageItemsAsync();
            if (items.Count != 1 || !items[0].IsOfType(StorageItemTypes.File))
            {
                return;
            }

            var file = items[0].Path;
            if (!string.Equals(Path.GetExtension(file), ".reg", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            args.AcceptedOperation = DataPackageOperation.Copy;
            if (openPreview)
            {
                await this.RegistryMenu.ImportCommand.ExecuteAsync(file);
            }
            else
            {
                args.DragUIOverride.Caption = "Open registry file preview";
                args.DragUIOverride.IsCaptionVisible = true;
            }
        }
        catch (Exception ex)
        {
            this.Logger.LogFailure(LogLevel.Warning, "Open dropped registry file", ex);
            if (openPreview)
            {
                this.ShowApplicationNotice("Couldn't open registry file", ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ActivateForRegistryFileKey(RegistryLocation location)
    {
        this.Activate();
    }
}
