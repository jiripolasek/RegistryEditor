// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class ByteInterpreter : UserControl
{
    internal ValueInspectorViewModel ViewModel { get; }

    internal ByteInterpreter(ValueInspectorViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        this.Loaded += (_, _) =>
        {
            this.ViewModel.PropertyChanged += this.RangeChanged;
            ValueInspector.HighlightBytes(this.ViewModel, this.BytesView);
        };
        this.Unloaded += (_, _) => this.ViewModel.PropertyChanged -= this.RangeChanged;
    }

    internal void FocusOffset()
    {
        this.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => this.OffsetBox.Focus(FocusState.Programmatic));
    }

    private void RangeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ValueInspectorViewModel.Offset) or nameof(ValueInspectorViewModel.Length))
        {
            ValueInspector.HighlightBytes(this.ViewModel, this.BytesView);
        }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs args)
    {
        ClipboardText.Set(this.ViewModel.Result);
    }
}
