// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryValueEditor : UserControl
{
    public RegistryValueEditorViewModel ViewModel { get; }

    internal RegistryValueEditor(RegistryValueEditorViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        this.FindName(viewModel.IsRaw ? nameof(this.RawPanel) : nameof(this.DataPanel));
        if (viewModel.IsRaw)
        {
            this.ByteEditor.PrepareHexPasteAsync = this.PrepareHexPasteAsync;
            this.ByteEditor.ReadCustomHexPaste = viewModel.ReadRegistryHex;
        }

        this.Bindings.Update();
    }

    private Style GetDataEditorStyle(bool multiline)
    {
        return (Style)this.Resources[
            this.ViewModel.IsNumeric ? "NumericDataEditor" : multiline ? "TextDataEditor" : "SingleLineDataEditor"];
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
        else if (this.ViewModel.IsRaw)
        {
            this.ByteEditor.Focus(FocusState.Keyboard);
        }
        else
        {
            this.DataBox.Focus(FocusState.Keyboard);
            this.DataBox.SelectAll();
        }
    }

    private void HexView_BytesEdited(object? sender, byte[] bytes)
    {
        this.ViewModel.ApplyBytes(bytes);
    }

    private async Task<string?> PrepareHexPasteAsync(string text)
    {
        if (text.AsSpan().IndexOfAny(',', ':') < 0)
        {
            return text;
        }

        var byteCount = await Task.Run(() => this.ViewModel.ReadRegistryHex(text, []));
        if (!this.IsLoaded)
        {
            return null;
        }

        if (byteCount < 0)
        {
            return text;
        }

        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new Button { Content = "Apply conversion" };
        AutomationProperties.SetAutomationId(apply, "ApplyHexPasteConversion");
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = RegistryValueEditorViewModel.ByteConversionHelp(byteCount),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 320
                    },
                    apply
                }
            }
        };
        apply.Click += (_, _) => confirmation.TrySetResult(true);
        flyout.Closed += (_, _) => confirmation.TrySetResult(false);
        void OnUnloaded(object sender, RoutedEventArgs args) => confirmation.TrySetResult(false);
        this.Unloaded += OnUnloaded;
        try
        {
            flyout.ShowAt(this.ByteEditor);
            if (!await confirmation.Task)
            {
                return null;
            }

            return byteCount == 0 ? string.Empty : text;
        }
        finally
        {
            this.Unloaded -= OnUnloaded;
            flyout.Hide();
        }
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
