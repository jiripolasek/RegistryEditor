// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

internal sealed class WindowDialogPresenter
{
    private readonly FrameworkElement _owner;
    private readonly Action _overlap;
    private readonly MainWindowViewModel _state;

    internal bool IsOpen => this._state.IsDialogOpen;

    internal WindowDialogPresenter(FrameworkElement owner, MainWindowViewModel state, Action overlap)
    {
        this._owner = owner;
        this._overlap = overlap;
        this._state = state;
    }

    internal Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        return this.ShowAsync(dialog, false);
    }

    internal Task<ContentDialogResult> ShowCloseConfirmationAsync(ContentDialog dialog)
    {
        return this.ShowAsync(dialog, true);
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, bool closeConfirmation)
    {
        if (!this._state.TryBeginDialog(closeConfirmation))
        {
            this._overlap();
            return ContentDialogResult.None;
        }

        try
        {
            dialog.XamlRoot = this._owner.XamlRoot;
            dialog.RequestedTheme = this._owner.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            this._state.EndDialog();
        }
    }
}
