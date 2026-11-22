// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryEditDialog : UserControl
{
    private static readonly Uri ValueTypeHelp = new("https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-value-types");
    private readonly DialogLayout _layout;
    private readonly double _maxWidth = 548;

    public RegistryEditPrompt Prompt { get; }

    private RegistryEditDialog(RegistryEditPrompt prompt)
    {
        this.Prompt = prompt;
        this.InitializeComponent();
        switch (prompt)
        {
            case RegistryNamePrompt name:
                var (title, subject, header, primary) = name.Operation switch
                {
                    RegistryNameOperation.CreateKey => ("New key", null, "Key name", "Create"),
                    RegistryNameOperation.Rename => ("Rename", null, "New name", "Rename"),
                    RegistryNameOperation.Paste => ("Paste into", name.PasteDestination, "Name of the copy", "Paste"),
                    RegistryNameOperation.DuplicateValue => ("Duplicate value", null, "Name of the copy", "Duplicate"),
                    _ => throw new ArgumentOutOfRangeException(nameof(prompt))
                };
                this.NameBox.Header = header;
                this.NameBox.Text = name.Name;
                this.NameBox.Visibility = Visibility.Visible;
                this._layout = new DialogLayout(title, this, primary, subject: subject)
                {
                    OnOpened = () =>
                    {
                        this.NameBox.Focus(FocusState.Keyboard);
                        this.NameBox.SelectAll();
                    },
                    Accept = () =>
                    {
                        name.Name = this.NameBox.Text;
                        return true;
                    }
                };
                break;
            case RegistryValueEditorViewModel editor:
                var content = new RegistryValueEditor(editor);
                this.ValueEditorHost.Content = content;
                this.ValueEditorHost.Visibility = Visibility.Visible;
                this._layout = new DialogLayout(
                    editor.Title, this, editor.IsCreating ? "Create" : "Save",
                    subject: editor.IsCreating ? null : editor.DisplayName, subtitle: editor.TypeName, help: ValueTypeHelp)
                {
                    OnOpened = content.FocusEditor,
                    Accept = editor.Validate
                };
                if (editor.IsRaw)
                {
                    // Room for 16 bytes per row plus the ASCII column in the hex preview.
                    this.ContentPanel.MinWidth = this.ContentPanel.MaxWidth = Controls.Shared.HexView.DialogWidth;
                    this._maxWidth = Controls.Shared.HexView.DialogWidth + 80;
                    this._layout.EnableHorizontalScrolling();
                }

                break;
            case RegistryDeletePrompt delete:
                this.DeleteMessage.Text = delete.Message;
                this.DeleteMessage.Visibility = Visibility.Visible;
                this._layout = new DialogLayout(delete.Title, this, "Delete", defaultButton: ContentDialogButton.Close, warning: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(prompt));
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
        var editor = new RegistryEditDialog(prompt);
        return await dialogs.ShowAsync(editor._layout, editor._maxWidth) == ContentDialogResult.Primary;
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
