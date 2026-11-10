// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;

namespace JPSoftworks.Regedit.ViewModels.Browser;

public abstract class RegistryEditPrompt : ObservableObject
{
    private string? _error;

    internal Exception? Failure { get; private set; }

    internal RegistryEditRecovery? Recovery { get; private set; }

    internal void SetFailure(
        Exception failure,
        RegistryEditTarget target,
        bool administrator,
        Func<RegistryLocation, Task>? elevate)
    {
        this.Error = failure.Message;
        this.Failure = failure;
        this.Recovery
            = failure is UnauthorizedAccessException or SecurityException or Win32Exception { NativeErrorCode: 5 }
                ? new RegistryEditRecovery(target, administrator, elevate)
                : null;
    }

    public bool HasError => this._error is not null;

    public string? Error
    {
        get => this._error;
        internal set
        {
            if (value is null)
            {
                this.Failure = null;
                this.Recovery = null;
            }

            if (this.SetProperty(ref this._error, value))
            {
                this.OnPropertyChanged(nameof(this.HasError));
            }
        }
    }
}

internal enum RegistryNameOperation { CreateKey, Rename, Paste, DuplicateValue }

internal sealed class RegistryNamePrompt(RegistryNameOperation operation, string name, string? pasteDestination = null)
    : RegistryEditPrompt
{
    internal RegistryNameOperation Operation { get; } = operation;

    internal string? PasteDestination { get; } = pasteDestination;

    internal string Name { get; set; } = name;
}

internal sealed class RegistryDeletePrompt(IReadOnlyList<StoredRegistryItem> items) : RegistryEditPrompt
{
    internal IReadOnlyList<StoredRegistryItem> Items { get; } = items;

    internal string Title => this.Items.Count > 1 ? $"Delete {this.Items.Count} items?" :
        this.Items[0] is StoredRegistryItem.Key ? "Delete key and all subkeys?" : "Delete value?";

    internal string Message => this.Items.Count > 1
        ? string.Join("\n", this.Items.Select(item => item.Path)) +
          "\n\nKeys, their subkeys, and values will be permanently removed."
        : this.Items[0].Path + "\n\nThis permanently removes the " +
          (this.Items[0] is StoredRegistryItem.Key ? "key and everything inside it" : "value") +
          ". Export a backup first if you may need to restore it.";
}
