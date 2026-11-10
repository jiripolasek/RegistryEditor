// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class AppDialog : ContentDialog
{
    internal AppDialog(
        string title,
        object content,
        string primary = "",
        string close = "Cancel",
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        this.InitializeComponent();
        this.Title = title;
        this.PrimaryButtonText = primary;
        this.CloseButtonText = close;
        this.DefaultButton = defaultButton;
        if (content is string message)
        {
            this.MessageText.Text = message;
            this.MessageText.Visibility = Visibility.Visible;
        }
        else
        {
            this.BodyHost.Content = content;
            this.BodyHost.Visibility = Visibility.Visible;
            if (content is ValueInspector or HexView)
            {
                RegistryEditDialog.WidenForHexView(this, this.ContentPanel);
            }
        }
    }

    internal void ShowError(string message)
    {
        this.ErrorText.Text = message;
        this.ErrorText.Visibility = Visibility.Visible;
    }

    internal void FocusOnOpen(Control field)
    {
        this.Opened += (_, _) =>
        {
            field.Focus(FocusState.Keyboard);
            if (field is TextBox text)
            {
                text.SelectAll();
            }
        };
    }
}
