// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Windows.Input;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class ValueInspector : UserControl
{
    private readonly WindowDialogPresenter? _dialogs;

    internal ValueInspectorViewModel ViewModel { get; }

    internal NoteDraft? Note { get; }

    internal ICommand? OpenNoteCommand { get; }

    internal bool IsInterpretationExpanded
    {
        get => this.InterpretationControls.Visibility == Visibility.Visible;
        set
        {
            this.InterpretationControls.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            this.InterpretationChevron.Glyph = value ? "\uE70D" : "\uE76C";
            AutomationProperties.SetName(this.InterpretBytesToggle, value ? "Hide byte interpretation" : "Show byte interpretation");
        }
    }

    internal ValueInspector(
        ValueInspectorViewModel viewModel,
        WindowDialogPresenter? dialogs = null,
        NoteDraft? note = null,
        ICommand? openNoteCommand = null)
    {
        this.ViewModel = viewModel;
        this._dialogs = dialogs;
        this.Note = note;
        this.OpenNoteCommand = openNoteCommand;
        this.InitializeComponent();
        this.RawBytesView.Width = HexView.DialogWidth;
        this.Loaded += (_, _) => viewModel.PropertyChanged += this.ViewModel_PropertyChanged;
        this.Unloaded += (_, _) => viewModel.PropertyChanged -= this.ViewModel_PropertyChanged;
    }

    private void ToggleByteInterpretation_Click(object sender, RoutedEventArgs args)
    {
        this.IsInterpretationExpanded = !this.IsInterpretationExpanded;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Keep the hex view on the byte range chosen for interpretation.
        if (e.PropertyName is nameof(ValueInspectorViewModel.Offset) or nameof(ValueInspectorViewModel.Length))
        {
            this.HighlightBytes(this.RawBytesView);
        }
    }

    private void HighlightBytes(HexView view)
    {
        if (double.IsFinite(this.ViewModel.Offset) && double.IsFinite(this.ViewModel.Length))
        {
            view.Highlight((long)this.ViewModel.Offset, (long)this.ViewModel.Length);
        }
    }

    private void Inspector_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = this._dialogs is not null && args.NewSize.Width < HexView.DialogWidth + 4;
        this.RawBytesFrame.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        this.OpenRawBytes.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OpenRawBytes_Click(object sender, RoutedEventArgs args)
    {
        if (this._dialogs is null)
        {
            return;
        }

        var view = new HexView { Bytes = this.ViewModel.Bytes, Width = HexView.DialogWidth, Height = 360 };
        AutomationProperties.SetAutomationId(view, "ExpandedValueHexView");
        AutomationProperties.SetName(view, "Raw bytes");
        view.Loaded += (_, _) => this.HighlightBytes(view);
        await this._dialogs.ShowAsync(new AppDialog("Raw bytes", view, "Close", string.Empty));
    }

    private void CopyReadout_Loaded(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        button.Loaded -= this.CopyReadout_Loaded;
        var row = button.Parent is StackPanel actions ? (Grid)actions.Parent : (Grid)button.Parent;
        var pointerOver = false;
        var focusWithin = false;

        // Opacity keeps the button's space and keyboard/automation access intact.
        void UpdateOpacity()
        {
            button.Opacity = pointerOver || focusWithin ? 1 : 0;
        }

        row.PointerEntered += (_, _) =>
        {
            pointerOver = true;
            UpdateOpacity();
        };
        row.PointerExited += (_, _) =>
        {
            pointerOver = false;
            UpdateOpacity();
        };
        row.GotFocus += (_, _) =>
        {
            focusWithin = true;
            UpdateOpacity();
        };
        row.LostFocus += (_, _) =>
        {
            focusWithin = false;
            UpdateOpacity();
        };
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs args)
    {
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ContentDialog dialog)
            {
                dialog.Hide();
                break;
            }
        }
    }

    private void CopyReadout_Click(object sender, RoutedEventArgs args)
    {
        ClipboardText.Set((string)((Button)sender).Tag);
    }
}
