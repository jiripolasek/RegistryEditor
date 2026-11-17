// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.Win32;

namespace JPSoftworks.Regedit.Dialogs;

internal enum RegistryFileSaveChoice { Save, Discard, Cancel }

internal interface IRegistryFileDialogs
{
    Task<string?> PickOpenAsync();

    Task<string?> PickSaveAsync(string currentFile);

    Task<RegistryFileSaveChoice> ConfirmChangesAsync(string fileName);

    Task<bool> ConfirmImportAsync(string fileName, RegistryView view);
}
