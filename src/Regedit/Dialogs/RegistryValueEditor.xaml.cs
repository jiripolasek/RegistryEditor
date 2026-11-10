// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryValueEditor : UserControl
{
    public RegistryValueEditorViewModel ViewModel { get; }

    internal RegistryValueEditor(RegistryValueEditorViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        if (viewModel.IsRaw)
        {
            this.FindName(nameof(this.RawPanel));
            this.Bindings.Update();
            // The hex view is the primary surface; the text editor opens on request (or straight away for a new value).
            this.DataBox.MaxHeight = 160;
            this.EditBytesToggle.IsChecked = viewModel.IsCreating;
            this.UpdateRawEditing();
        }
    }

    private Style GetDataEditorStyle(bool multiline)
    {
        return (Style)this.Resources[
            this.ViewModel.IsNumeric ? "NumericDataEditor" :
            this.ViewModel.IsRaw ? "RawDataEditor" :
            multiline ? "TextDataEditor" : "SingleLineDataEditor"];
    }

    private async void DataBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        if (!this.ViewModel.IsString || this.ViewModel.IsMultiline)
        {
            return;
        }

        // A single-line TextBox otherwise truncates pasted text at the first line break.
        e.Handled = true;
        var currentText = this.DataBox.Text;
        var start = this.DataBox.SelectionStart;
        var length = this.DataBox.SelectionLength;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (this.IsLoaded && this.DataBox.Text == currentText && this.ViewModel.ValidateLineBreaks(text))
                {
                    this.DataBox.Select(start, length);
                    this.DataBox.SelectedText = text;
                }
            }
        }
        catch (Exception ex)
        {
            this.ViewModel.Error = "Could not paste text: " + ex.Message;
        }
    }

    internal void FocusEditor()
    {
        if (this.ViewModel.IsCreating)
        {
            this.NameBox.Focus(FocusState.Keyboard);
            this.NameBox.SelectAll();
        }
        else if (this.DataPanel.Visibility == Visibility.Visible)
        {
            this.DataBox.Focus(FocusState.Keyboard);
            this.DataBox.SelectAll();
        }
        else
        {
            this.EditBytesToggle.Focus(FocusState.Keyboard);
        }
    }

    private void HexView_BytesEdited(object? sender, byte[] bytes)
    {
        this.ViewModel.ApplyBytes(bytes);
    }

    private void EditBytesToggle_Changed(object sender, RoutedEventArgs e)
    {
        this.UpdateRawEditing();
        if (this.EditBytesToggle.IsChecked == true && this.IsLoaded)
        {
            this.DataBox.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateRawEditing()
    {
        this.DataPanel.Visibility = this.EditBytesToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Base_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        this.ViewModel.BaseIndex = this.BaseButtons.SelectedIndex;
        if (this.BaseButtons.SelectedIndex != this.ViewModel.BaseIndex)
        {
            // Restore a rejected base after the radio group finishes changing its selection.
            this.DispatcherQueue.TryEnqueue(() => this.BaseButtons.SelectedIndex = this.ViewModel.BaseIndex);
        }
    }
}
