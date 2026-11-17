// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.ViewModels.Files;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed partial class MainWindowViewModel
{
    internal event Action? RegistryFileEditorSettingsRequested;

    private readonly List<RegistryFilePreviewViewModel> _registryFiles = [];

    internal bool CanOpenRegistryFile => !this.IsClosing && !this.CanCloseWindow;

    internal RegistryFileLayout RegistryFileLayout => this._settings.RegistryFileLayout;

    internal void RegisterRegistryFile(RegistryFilePreviewViewModel model)
    {
        this._registryFiles.Add(model);
    }

    internal void UnregisterRegistryFile(RegistryFilePreviewViewModel model)
    {
        this._registryFiles.Remove(model);
    }

    [RelayCommand]
    private void OpenRegistryFileEditorSettings()
    {
        if (this.CanOpenRegistryFile)
        {
            this.ShowSettings(false);
            this.RegistryFileEditorSettingsRequested?.Invoke();
        }
    }

    private static async Task<bool> PrepareRegistryFilesAsync(RegistryFilePreviewViewModel[] files)
    {
        foreach (var model in files)
        {
            if (!await model.PrepareCloseAsync())
            {
                return false;
            }
        }

        return true;
    }
}
