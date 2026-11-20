// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Lists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Controls.Lists;

/// <summary>
///     Declares one column of a <see cref="ColumnListView" />. Supply either <see cref="CellTemplate" /> (its root
///     receives the row item as DataContext, so x:Bind templates work) or <see cref="CellText" /> for a trimmed text
///     cell that needs no reflection binding.
/// </summary>
public sealed partial class ListColumn : DependencyObject
{
    public string Id { get; set; } = string.Empty;

    public string Header { get; set; } = string.Empty;

    /// <summary>Accessible column name; defaults to <see cref="Header" />.</summary>
    public string? AutomationName { get; set; }

    public DataTemplate? CellTemplate { get; set; }

    public Func<object?, string?>? CellText { get; set; }

    public HorizontalAlignment HeaderAlignment { get; set; } = HorizontalAlignment.Left;

    public bool Sortable { get; set; }

    public double DefaultWidth { get; set; }

    public double MinWidth { get; set; } = 48;

    public double MaxWidth { get; set; } = 1600;

    public double FillWeight { get; set; } = 1;

    public bool CanHide { get; set; } = true;

    public bool CanResize { get; set; } = true;

    public bool CanReorder { get; set; } = true;

    public bool DefaultVisible { get; set; } = true;

    /// <summary>Allows inline editing (F2, slow click or <see cref="ColumnListView.BeginEdit" />) in this column.</summary>
    public bool IsEditable { get; set; }

    /// <summary>Initial editor text for an item; defaults to <see cref="CellText" />.</summary>
    public Func<object?, string?>? EditText { get; set; }

    internal string Name => this.AutomationName ?? this.Header;

    internal string TextForEdit(object? item)
    {
        return (this.EditText ?? this.CellText)?.Invoke(item) ?? string.Empty;
    }

    /// <summary>Re-reads <see cref="CellText" /> for a text cell; templated cells update through their own bindings.</summary>
    internal void RefreshCell(FrameworkElement cell, object? item)
    {
        if (this.CellTemplate is null && cell is TextBlock text)
        {
            text.Text = this.CellText?.Invoke(item) ?? string.Empty;
        }
    }

    public ColumnSpec ToSpec()
    {
        return new ColumnSpec(
            this.Id, this.DefaultWidth, this.MinWidth, this.MaxWidth, this.FillWeight, this.CanHide, this.CanResize, this.CanReorder,
            this.DefaultVisible);
    }

    internal FrameworkElement CreateCell()
    {
        if (this.CellTemplate?.LoadContent() is FrameworkElement templated)
        {
            return templated;
        }

        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment
                = this.HeaderAlignment == HorizontalAlignment.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            IsTextScaleFactorEnabled = true
        };
        var format = this.CellText;
        text.DataContextChanged += (sender, args) => ((TextBlock)sender).Text = format?.Invoke(args.NewValue) ?? string.Empty;
        return text;
    }
}
