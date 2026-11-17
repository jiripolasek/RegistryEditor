// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.Storage.Pickers;

namespace JPSoftworks.Regedit.Dialogs;

internal sealed class RegistryFileDialogs : IRegistryFileDialogs
{
    private readonly nint _handle;
    private readonly FrameworkElement _owner;

    internal RegistryFileDialogs(nint handle, FrameworkElement owner)
    {
        this._handle = handle;
        this._owner = owner;
    }

    public async Task<string?> PickOpenAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, this._handle);
        picker.FileTypeFilter.Add(".reg");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickSaveAsync(string currentFile)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(currentFile) };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, this._handle);
        picker.FileTypeChoices.Add("Registry file", new List<string> { ".reg" });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public async Task<RegistryFileSaveChoice> ConfirmChangesAsync(string fileName)
    {
        var dialog = new AppDialog(
            "Unsaved registry file", $"Save changes to {fileName}?", "Save", defaultButton: ContentDialogButton.Close)
        {
            SecondaryButtonText = "Discard"
        };
        return await this.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => RegistryFileSaveChoice.Save,
            ContentDialogResult.Secondary => RegistryFileSaveChoice.Discard,
            _ => RegistryFileSaveChoice.Cancel
        };
    }

    public async Task<bool> ConfirmImportAsync(string fileName, RegistryView view)
    {
        var dialog = new AppDialog(
            "Import registry file?",
            $"Import the current editor contents of {fileName} into this computer's {(view == RegistryView.Registry32 ? "32" : "64")}-bit registry?\n\n" +
            "This can add, replace, or delete keys and values anywhere on this computer. Import can partially succeed before an error and cannot be automatically undone. The original file is not changed.",
            "Import", defaultButton: ContentDialogButton.Close);
        return await this.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task<ContentDialogResult> ShowAsync(AppDialog dialog)
    {
        dialog.XamlRoot = this._owner.XamlRoot;
        dialog.RequestedTheme = this._owner.ActualTheme;
        return await dialog.ShowAsync();
    }
}
