// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Controls;

public partial class HexEditor
{
    public static readonly DependencyProperty ColumnBandSizeProperty = DependencyProperty.Register(
        nameof(ColumnBandSize), typeof(int), typeof(HexEditor), new PropertyMetadata(0, OnBandChanged));

    public static readonly DependencyProperty ColumnBandBrushProperty = DependencyProperty.Register(
        nameof(ColumnBandBrush), typeof(Brush), typeof(HexEditor), new PropertyMetadata(null, (sender, _) => ((HexEditor)sender).UpdateColumns()));

    public static readonly DependencyProperty ColumnBandForegroundProperty = DependencyProperty.Register(
        nameof(ColumnBandForeground), typeof(Brush), typeof(HexEditor), new PropertyMetadata(null, OnBandChanged));

    /// <summary>Number of bytes in each alternating column group. Zero disables banding.</summary>
    public int ColumnBandSize
    {
        get => (int)this.GetValue(ColumnBandSizeProperty);
        set => this.SetValue(ColumnBandSizeProperty, value);
    }

    public Brush? ColumnBandBrush
    {
        get => (Brush?)this.GetValue(ColumnBandBrushProperty);
        set => this.SetValue(ColumnBandBrushProperty, value);
    }

    /// <summary>Foreground for bytes in alternating column groups in both panes. Null uses the normal text color.</summary>
    public Brush? ColumnBandForeground
    {
        get => (Brush?)this.GetValue(ColumnBandForegroundProperty);
        set => this.SetValue(ColumnBandForegroundProperty, value);
    }

    private readonly TextBlock[] _byteLabels = new TextBlock[HexEditorRow.BytesPerRow];

    private void InitializeColumns()
    {
        var style = (Style)this.Resources["ColumnLabelStyle"];
        for (var i = 0; i < this._byteLabels.Length; i++)
        {
            var label = new TextBlock { Style = style, Text = i.ToString("X2", CultureInfo.InvariantCulture), TextAlignment = TextAlignment.Center };
            this._byteLabels[i] = label;
            this.ColumnLabels.Children.Add(label);
        }

        this.ColumnRuler.SizeChanged += (_, _) => this.UpdateColumns();
    }

    private double CellWidth => this.ColumnRuler.Text.Length == 0 ? 0 : this.ColumnRuler.ActualWidth / this.ColumnRuler.Text.Length;

    private static void OnBandChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (HexEditor)sender;
        if (editor.IsLoaded)
        {
            editor.UpdateColumns();
            foreach (RichTextBlock row in editor.Rows.Children)
            {
                editor.UpdateBandText(row);
            }
        }
    }

    private void UpdateBandText(RichTextBlock row)
    {
        var layer = row.TextHighlighters[BandHighlight];
        var ranges = layer.Ranges;
        var size = this.ColumnBandSize;
        if (size is <= 0 or >= HexEditorRow.BytesPerRow || this.ColumnBandForeground is not { } foreground)
        {
            if (ranges.Count > 0)
            {
                ranges.Clear();
                row.InvalidateArrange();
            }
            return;
        }

        layer.Foreground = foreground;
        ranges.Clear();
        var ascii = HexEditorRow.AsciiStart(this._digits);
        var count = RowText(row).Text.Length - ascii;
        for (var column = size; column < count; column += 2 * size)
        {
            var bytes = Math.Min(size, count - column);
            ranges.Add(new TextRange { StartIndex = HexEditorRow.HexStart(this._digits) + column * 3, Length = bytes * 3 - 1 });
            ranges.Add(new TextRange { StartIndex = ascii + column, Length = bytes });
        }
        row.InvalidateArrange();
    }

    private void UpdateColumns()
    {
        if (!this.IsLoaded || this.CellWidth <= 0)
        {
            return;
        }

        var cell = this.CellWidth;
        var hex = HexEditorRow.HexStart(this._digits);
        var ascii = HexEditorRow.AsciiStart(this._digits);
        for (var i = 0; i < this._byteLabels.Length; i++)
        {
            this._byteLabels[i].Margin = new Thickness((hex + i * 3) * cell, 0, 0, 0);
            this._byteLabels[i].Width = 2 * cell;
        }

        this.AsciiLabel.Margin = new Thickness(ascii * cell, 0, 0, 0);
        this.OffsetDivider.Margin = new Thickness((hex - 1.5) * cell, 0, 0, 0);
        this.AsciiDivider.Margin = new Thickness((ascii - 1.5) * cell, 0, 0, 0);

        var bands = this.ColumnBands.Children;
        var count = 0;
        var size = this.ColumnBandSize;
        if (size > 0 && size < HexEditorRow.BytesPerRow && this.Rows.ActualHeight > 0)
        {
            for (var column = size; column < HexEditorRow.BytesPerRow; column += 2 * size)
            {
                var bytes = Math.Min(size, HexEditorRow.BytesPerRow - column);
                PlaceBand(hex + column * 3 - 0.5, bytes * 3);
                PlaceBand(ascii + column, bytes);
            }
        }

        while (bands.Count > count)
        {
            bands.RemoveAt(bands.Count - 1);
        }

        void PlaceBand(double start, int width)
        {
            if (count == bands.Count)
            {
                bands.Add(new Border());
            }

            var band = (Border)bands[count++];
            Canvas.SetLeft(band, start * cell);
            band.Width = width * cell;
            band.Height = this.Rows.ActualHeight;
            band.Background = this.ColumnBandBrush;
        }
    }
}
