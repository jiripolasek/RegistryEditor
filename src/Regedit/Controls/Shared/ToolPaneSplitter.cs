// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ToolPaneSplitter : UserControl
{
    private readonly ResizeThumb _thumb = new();
    private ColumnDefinition? _column;
    private RowDefinition? _row;
    private FrameworkElement? _pane;
    private Func<double>? _availableWidth;
    private double? _width, _height;
    private bool _beside;

    private double Maximum => this._beside
        ? Math.Max(220, (this._availableWidth?.Invoke() ?? 0) - 200)
        : Math.Max(100, ((this.Parent as FrameworkElement)?.ActualHeight ?? 0) - 120);

    public ToolPaneSplitter()
    {
        this.Content = this._thumb;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this._thumb, "ToolDetailsResize");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this._thumb, "Resize preview pane");
        ToolTipService.SetToolTip(this._thumb, "Drag to resize the preview pane, or use the arrow keys.");
        this._thumb.DragDelta += (_, args) => this.Resize(this._beside ? -args.HorizontalChange : -args.VerticalChange);
        this._thumb.PreviewKeyDown += this.Resize_KeyDown;
    }

    internal void Arrange(
        FrameworkElement pane,
        ColumnDefinition column,
        RowDefinition row,
        bool beside,
        bool visible,
        Func<double> availableWidth)
    {
        this._pane = pane;
        this._column = column;
        this._row = row;
        this._beside = beside;
        this._availableWidth = availableWidth;
        this.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(this, Grid.GetColumn(pane));
        Grid.SetRow(this, Grid.GetRow(pane));
        Grid.SetColumnSpan(this, Grid.GetColumnSpan(pane));
        this.HorizontalAlignment = beside ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        this.VerticalAlignment = beside ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        this.Width = beside ? 8 : double.NaN;
        this.Height = beside ? double.NaN : 8;
        this.Margin = beside ? new Thickness(pane.Margin.Left - 4, 0, 0, 0) : new Thickness(0, -8, 0, 0);
        var direction = beside ? Orientation.Horizontal : Orientation.Vertical;
        if (this._thumb.ResizeDirection != direction)
        {
            this._thumb.ResizeDirection = direction;
        }

        if (!visible)
        {
            return;
        }

        if (beside && this._width is { } width)
        {
            column.Width = new GridLength(PaneResize.Apply(width, 0, 220, this.Maximum));
        }

        if (!beside && this._height is { } height)
        {
            row.Height = new GridLength(PaneResize.Apply(height, 0, 100, this.Maximum));
        }
    }

    private void Resize(double delta)
    {
        if (this._pane is null || this._column is null || this._row is null)
        {
            return;
        }

        if (this._beside)
        {
            this._width = PaneResize.Apply(this._width ?? this._column.ActualWidth, delta, 220, this.Maximum);
            this._column.Width = new GridLength(this._width.Value);
        }
        else
        {
            this._height = PaneResize.Apply(this._height ?? this._row.ActualHeight, delta, 100, this.Maximum);
            this._row.Height = new GridLength(this._height.Value);
        }
    }

    private void Resize_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var delta = (this._beside, args.Key) switch
        {
            (true, VirtualKey.Left) or (false, VirtualKey.Up) => 24,
            (true, VirtualKey.Right) or (false, VirtualKey.Down) => -24,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        args.Handled = true;
        this.Resize(delta);
    }
}
