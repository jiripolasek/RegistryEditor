// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryEditDialog : ContentDialog
{
    public RegistryEditPrompt Prompt { get; }

    private RegistryEditDialog(RegistryEditPrompt prompt)
    {
        this.Prompt = prompt;
        this.InitializeComponent();
        switch (prompt)
        {
            case RegistryNamePrompt name:
                var (title, header, primary) = name.Operation switch
                {
                    RegistryNameOperation.CreateKey => ("New key", "Key name", "Create"),
                    RegistryNameOperation.Rename => ("Rename", "New name", "Rename"),
                    RegistryNameOperation.Paste => ("Paste into " + name.PasteDestination, "Name of the copy", "Paste"),
                    RegistryNameOperation.DuplicateValue => ("Duplicate value", "Name of the copy", "Duplicate"),
                    _ => throw new ArgumentOutOfRangeException(nameof(prompt))
                };
                this.Title = title;
                this.PrimaryButtonText = primary;
                this.NameBox.Header = header;
                this.NameBox.Text = name.Name;
                this.NameBox.Visibility = Visibility.Visible;
                this.Opened += (_, _) =>
                {
                    this.NameBox.Focus(FocusState.Keyboard);
                    this.NameBox.SelectAll();
                };
                this.PrimaryButtonClick += (_, _) => name.Name = this.NameBox.Text;
                break;
            case RegistryValueEditorViewModel editor:
                this.Title = editor.Title;
                this.PrimaryButtonText = editor.IsCreating ? "Create" : "Save";
                var content = new RegistryValueEditor(editor);
                this.ValueEditorHost.Content = content;
                this.ValueEditorHost.Visibility = Visibility.Visible;
                if (editor.IsRaw)
                {
                    // Room for 16 bytes per row plus the ASCII column in the hex preview.
                    WidenForHexView(this, this.ContentPanel);
                }

                this.Opened += (_, _) => content.FocusEditor();
                this.PrimaryButtonClick += (_, args) => args.Cancel = !editor.Validate();
                break;
            case RegistryDeletePrompt delete:
                this.Title = delete.Title;
                this.PrimaryButtonText = "Delete";
                this.DefaultButton = ContentDialogButton.Close;
                this.DeleteMessage.Text = delete.Message;
                this.DeleteMessage.Visibility = Visibility.Visible;
                break;
        }

        if (prompt.Recovery is { } recovery)
        {
            this.RecoveryPanel.Visibility = Visibility.Visible;
            this.RecoveryTarget.Text = recovery.Target.Description;
            this.RecoveryGuidance.Text = recovery.Guidance;
            this.ElevateButton.Command = recovery.OpenAsAdministratorCommand;
            this.ElevateButton.Visibility = recovery.CanOpenAsAdministrator ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal static async Task<bool> ShowAsync(WindowDialogPresenter dialogs, RegistryEditPrompt prompt)
    {
        return await dialogs.ShowAsync(new RegistryEditDialog(prompt)) == ContentDialogResult.Primary;
    }

    /// <summary>Widens a dialog so a <see cref="Controls.Shared.HexView"/> can show 16 bytes per row.</summary>
    internal static void WidenForHexView(ContentDialog dialog, FrameworkElement content)
    {
        // The dialog adds 24 px padding per side plus its border; leave slack so the content is never clipped.
        dialog.Resources["ContentDialogMaxWidth"] = Controls.Shared.HexView.DialogWidth + 80;
        content.MinWidth = Controls.Shared.HexView.DialogWidth;
        content.MaxWidth = Controls.Shared.HexView.DialogWidth;
    }
}
