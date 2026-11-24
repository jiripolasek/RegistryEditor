// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace JPSoftworks.Controls;

public partial class HexEditor
{
    private readonly DispatcherTimer _hoverDelay = new();
    private readonly ToolTip _offsetTip = new() { Placement = PlacementMode.Bottom };
    private Point? _hoverPoint;
    private (long Offset, bool Ascii)? _hoverByte;
    private bool _menuOpen;

    private void InitializeHover()
    {
        ToolTipService.SetToolTip(this.HoverMarks, this._offsetTip);
        this.Rows.PointerExited += (_, _) => this.ClearHover();
        this.Rows.ContextRequested += (_, args) =>
        {
            this._menuOffset = args.TryGetPosition(this.Rows, out var point) ? this.HitTestByte(point, exact: true)?.Offset : null;
        };
        this.Viewport.ViewChanged += (_, _) => this.UpdateHover(layoutChanged: true);
        this._hoverDelay.Tick += (_, _) =>
        {
            this._hoverDelay.Stop();
            if (this._hoverByte is { } hit && this._dragPointer is null && this.IsLoaded)
            {
                var mark = hit.Ascii ? this.AsciiHover : this.HexHover;
                this._offsetTip.PlacementRect = new Rect(Canvas.GetLeft(mark), Canvas.GetTop(mark), mark.Width, mark.Height);
                this._offsetTip.Content = $"Offset {this.FormatOffset(hit.Offset)} ({hit.Offset:N0})";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this._offsetTip, (string)this._offsetTip.Content);
                this._offsetTip.IsOpen = true;
            }
        };
    }

    private void ClearHover()
    {
        this._hoverPoint = null;
        this.HideHover();
    }

    private void HideHover()
    {
        this._hoverByte = null;
        this._hoverDelay.Stop();
        this._offsetTip.IsOpen = false;
        this.HoverMarks.Visibility = Visibility.Collapsed;
    }

    private void UpdateHover(bool layoutChanged = false)
    {
        if (this._hoverPoint is not { } point || !this.IsLoaded || this._menuOpen)
        {
            return;
        }

        var hit = point.X >= 0 && point.X < this.Viewport.ViewportWidth && point.Y >= 0 && point.Y < this.Viewport.ViewportHeight
            ? this.HitTestByte(this.Viewport.TransformToVisual(this.Rows).TransformPoint(point), exact: true)
            : null;
        if (hit is not { } target)
        {
            if (this._hoverByte is not null)
            {
                this.HideHover();
            }
            return;
        }

        if (this._hoverByte != target)
        {
            this._hoverByte = target;
            this._offsetTip.IsOpen = false;
            this._hoverDelay.Stop();
            this._hoverDelay.Interval = TimeSpan.FromMilliseconds(Math.Max(1u, this._caretSettings.MouseHoverTime));
            this._hoverDelay.Start();
        }
        else if (!layoutChanged)
        {
            return;
        }

        var index = (int)(target.Offset / HexEditorRow.BytesPerRow - this._firstRow);
        var row = (RichTextBlock)this.Rows.Children[index];
        var column = (int)(target.Offset % HexEditorRow.BytesPerRow);
        PlaceMark(this.HexHover, this.RangeBounds(row, HexEditorRow.HexStart(this._digits) + column * 3, 2));
        PlaceMark(this.AsciiHover, this.RangeBounds(row, HexEditorRow.AsciiStart(this._digits) + column, 1));
        var selected = target.Offset >= this.SelectionStart && target.Offset - this.SelectionStart < this.SelectionLength;
        this.HexHover.ClearValue(Border.BorderBrushProperty);
        this.AsciiHover.ClearValue(Border.BorderBrushProperty);
        if (selected)
        {
            var active = this._ascii ? this.AsciiHover : this.HexHover;
            active.BorderBrush = row.TextHighlighters[ActiveHighlight].Foreground;
        }
        this.HoverMarks.Visibility = Visibility.Visible;
    }
}
