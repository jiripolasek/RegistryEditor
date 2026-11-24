// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace JPSoftworks.Controls;

public partial class HexEditor
{
    // The byte row template draws these in order, with selection above the column bands.
    internal const int OffsetHighlight = 0;
    internal const int BandHighlight = 1;
    internal const int InactiveHighlight = 2;
    internal const int ActiveHighlight = 3;

    public static readonly DependencyProperty ShowColumnHeadersProperty = DependencyProperty.Register(
        nameof(ShowColumnHeaders), typeof(bool), typeof(HexEditor), new PropertyMetadata(true, (sender, _) => ((HexEditor)sender).UpdateViewport()));

    public static readonly DependencyProperty ShowStatusBarProperty = DependencyProperty.Register(
        nameof(ShowStatusBar), typeof(bool), typeof(HexEditor), new PropertyMetadata(true));

    public static readonly DependencyProperty SelectionBackgroundProperty = RegisterBrush(nameof(SelectionBackground));
    public static readonly DependencyProperty SelectionForegroundProperty = RegisterBrush(nameof(SelectionForeground));
    public static readonly DependencyProperty InactiveSelectionBorderBrushProperty = RegisterBrush(nameof(InactiveSelectionBorderBrush));
    public static readonly DependencyProperty InactiveSelectionBackgroundProperty = RegisterBrush(nameof(InactiveSelectionBackground));
    public static readonly DependencyProperty InactiveSelectionForegroundProperty = RegisterBrush(nameof(InactiveSelectionForeground));

    private readonly DispatcherTimer _caretBlink = new();
    private readonly UISettings _caretSettings = new();
    private bool _selectionLayoutPending;
    private bool _revealColumnAfterLayout;
    private bool _pointerOver;
    private bool _focusWithin;

    private void UpdateFieldState()
    {
        var state = this.BorderThickness == default ? "Normal"
            : !this.IsEnabled ? "Disabled"
            : this._focusWithin ? "Focused"
            : this._pointerOver ? "PointerOver" : "Normal";
        VisualStateManager.GoToState(this, state, false);
    }

    /// <summary>Shows the offset, hex byte and ASCII headings above the data rows.</summary>
    public bool ShowColumnHeaders
    {
        get => (bool)this.GetValue(ShowColumnHeadersProperty);
        set => this.SetValue(ShowColumnHeadersProperty, value);
    }

    /// <summary>Shows the status beneath the data rows. Automation status remains available when hidden.</summary>
    public bool ShowStatusBar
    {
        get => (bool)this.GetValue(ShowStatusBarProperty);
        set => this.SetValue(ShowStatusBarProperty, value);
    }

    public Brush? SelectionBackground
    {
        get => (Brush?)this.GetValue(SelectionBackgroundProperty);
        set => this.SetValue(SelectionBackgroundProperty, value);
    }

    public Brush? SelectionForeground
    {
        get => (Brush?)this.GetValue(SelectionForegroundProperty);
        set => this.SetValue(SelectionForegroundProperty, value);
    }

    public Brush? InactiveSelectionBorderBrush
    {
        get => (Brush?)this.GetValue(InactiveSelectionBorderBrushProperty);
        set => this.SetValue(InactiveSelectionBorderBrushProperty, value);
    }

    /// <summary>Fallback outline brush for hosts that styled the inactive selection background.</summary>
    public Brush? InactiveSelectionBackground
    {
        get => (Brush?)this.GetValue(InactiveSelectionBackgroundProperty);
        set => this.SetValue(InactiveSelectionBackgroundProperty, value);
    }

    public Brush? InactiveSelectionForeground
    {
        get => (Brush?)this.GetValue(InactiveSelectionForegroundProperty);
        set => this.SetValue(InactiveSelectionForegroundProperty, value);
    }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(HexEditor), new PropertyMetadata(null, (sender, _) =>
        {
            var editor = (HexEditor)sender;
            if (editor.IsLoaded)
            {
                editor.Rows.Children.Clear();
                editor.SelectionOutlines.Children.Clear();
                editor.UpdateViewport(dataChanged: true);
            }
        }));

    private RichTextBlock CreateRow()
    {
        var row = (RichTextBlock)((DataTemplate)this.Resources["ByteRowTemplate"]).LoadContent();
        row.CharacterSpacing = this.CharacterSpacing;
        var inactive = row.TextHighlighters[InactiveHighlight];
        var active = row.TextHighlighters[ActiveHighlight];
        if (this.SelectionBackground is { } background)
        {
            active.Background = background;
        }

        if (this.SelectionForeground is { } foreground)
        {
            active.Foreground = foreground;
        }

        if (this.InactiveSelectionForeground is { } inactiveForeground)
        {
            inactive.Foreground = inactiveForeground;
        }

        return row;
    }

    private (RichTextBlock Row, Rect Bounds)? GetCharacterBounds(long offset)
    {
        var index = (offset / HexEditorRow.BytesPerRow) - this._firstRow;
        if (index < 0 || index >= this.Rows.Children.Count)
        {
            return null;
        }

        var row = (RichTextBlock)this.Rows.Children[(int)index];
        var column = (int)(offset % HexEditorRow.BytesPerRow);
        var character = this._ascii ? HexEditorRow.AsciiStart(this._digits) + column
            : HexEditorRow.HexStart(this._digits) + (column * 3) + (this._buffer.LowNibble ? 1 : 0);
        var start = RowText(row).ContentStart;
        if (offset == this._buffer.Length)
        {
            var insertion = start.GetCharacterRect(LogicalDirection.Forward);
            insertion.X = character * this.CellWidth;
            return insertion.Height > 0 ? (row, insertion) : null;
        }
        var position = start.GetPositionAtOffset(character, LogicalDirection.Forward);
        return position?.GetCharacterRect(LogicalDirection.Forward) is { Height: > 0 } bounds ? (row, bounds) : null;
    }

    private void HideCaret()
    {
        this._caretBlink.Stop();
        this.EditCaret.Visibility = Visibility.Collapsed;
    }

    private bool CanShowCaret => this.IsEnabled && this._buffer.CanWriteByte(this._buffer.Caret)
        && this.FocusState != FocusState.Unfocused && this.OffsetPanel.Visibility == Visibility.Collapsed;

    private void ClearSelectionVisuals()
    {
        this.Rows.LayoutUpdated -= this.ArrangeSelection;
        this._selectionLayoutPending = this._revealColumnAfterLayout = false;
        this.SelectionOutlines.Children.Clear();
        this.HideCaret();
    }

    private void RequestSelectionLayout()
    {
        if (!this.IsLoaded || this.Rows.Children.Count == 0)
        {
            this.ClearSelectionVisuals();
            return;
        }

        if (!this.CanShowCaret)
        {
            this.HideCaret();
        }

        // A recycled row's text positions are valid after its next arrange.
        if (!this._selectionLayoutPending)
        {
            this._selectionLayoutPending = true;
            this.Rows.LayoutUpdated += this.ArrangeSelection;
            this.Rows.InvalidateArrange();
        }
    }

    private void ArrangeSelection(object? sender, object args)
    {
        this.Rows.LayoutUpdated -= this.ArrangeSelection;
        this._selectionLayoutPending = false;
        this.PositionOutlines();
        this.UpdateHover(layoutChanged: true);
        if (this.CanShowCaret)
        {
            this.PositionCaret();
        }

        if (this._revealColumnAfterLayout)
        {
            this._revealColumnAfterLayout = false;
            this.BringActiveCharacterIntoView();
        }
    }

    private void PositionOutlines()
    {
        var outlines = this.SelectionOutlines.Children;
        var count = 0;
        foreach (RichTextBlock row in this.Rows.Children)
        {
            var ranges = row.TextHighlighters[InactiveHighlight].Ranges;
            if (ranges.Count == 0)
            {
                continue;
            }

            var range = ranges[0];
            var bounds = this.RangeBounds(row, range.StartIndex, range.Length);
            if (bounds.Width <= 0)
            {
                continue;
            }

            if (count == outlines.Count)
            {
                var border = (Border)((DataTemplate)this.Resources["SelectionOutlineTemplate"]).LoadContent();
                if ((this.InactiveSelectionBorderBrush ?? this.InactiveSelectionBackground) is { } brush)
                {
                    border.BorderBrush = brush;
                }

                outlines.Add(border);
            }

            PlaceMark((Border)outlines[count++], bounds);
        }

        while (outlines.Count > count)
        {
            outlines.RemoveAt(outlines.Count - 1);
        }
    }

    private Rect RangeBounds(RichTextBlock row, int index, int length)
    {
        var text = RowText(row).ContentStart;
        var start = text.GetPositionAtOffset(index, LogicalDirection.Forward)?.GetCharacterRect(LogicalDirection.Forward);
        var end = text.GetPositionAtOffset(index + length, LogicalDirection.Backward)?.GetCharacterRect(LogicalDirection.Backward);
        if (start is null || end is null || end.Value.X <= start.Value.X)
        {
            return default;
        }

        var origin = row.TransformToVisual(this.Rows).TransformPoint(new Point(start.Value.X, 0));
        return new Rect(origin.X, origin.Y, end.Value.X - start.Value.X, row.ActualHeight);
    }

    private static void PlaceMark(Border mark, Rect bounds)
    {
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y);
        mark.Width = bounds.Width;
        mark.Height = bounds.Height;
    }

    private void PositionCaret()
    {
        this._caretBlink.Stop();
        if (this.GetCharacterBounds(this._buffer.Caret) is not { } cell)
        {
            this.EditCaret.Visibility = Visibility.Collapsed;
            return;
        }

        var bounds = cell.Row.TransformToVisual(this.Rows).TransformBounds(cell.Bounds);
        this.CaretPosition.X = bounds.X;
        this.CaretPosition.Y = bounds.Y;
        this.EditCaret.Height = bounds.Height;
        this.EditCaret.Width = Math.Max(1u, this._caretSettings.CaretWidth);
        this.EditCaret.Background = this._buffer.Caret == this._buffer.Length ? this.Foreground : cell.Row.TextHighlighters[ActiveHighlight].Foreground;
        this.EditCaret.Opacity = 1;
        this.EditCaret.Visibility = Visibility.Visible;

        var interval = this._caretSettings.CaretBlinkRate;
        if (interval is > 0 and < uint.MaxValue && this._caretSettings.AnimationsEnabled)
        {
            this._caretBlink.Interval = TimeSpan.FromMilliseconds(interval);
            this._caretBlink.Start();
        }
    }
}
