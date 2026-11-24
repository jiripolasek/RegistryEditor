// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml;

namespace JPSoftworks.Regedit.Views.Tools;

public sealed partial class ToolView
{
    private async void ChangeNoteScope_Click(object sender, RoutedEventArgs args)
    {
        var browser = this._window.ViewModel.ContextBrowser;
        if (this.Notes is not { } notes ||
            notes.CreateScopeEditor(browser?.Location, browser?.SelectedEntry?.Value) is not { } editor)
        {
            return;
        }

        var form = new NoteScopeForm(editor);
        var dialog = new AppDialog("Where should this note appear?", form, "Save scope")
        {
            IsPrimaryButtonEnabled = editor.CanSave
        };
        dialog.FocusOnOpen(form.InitialFocus);
        editor.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(NoteScopeViewModel.CanSave))
            {
                dialog.IsPrimaryButtonEnabled = editor.CanSave;
            }
        };
        dialog.PrimaryButtonClick += (_, click) => click.Cancel = !notes.SaveScope(editor);
        await this._window.Dialogs.ShowAsync(dialog);
    }
}
