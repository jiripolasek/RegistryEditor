// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ToolDetails : UserControl
{
    internal event Action<RegistryValue>? InspectValueRequested;

    internal event Action<RegistryValue>? CopyBytesRequested;

    public UIElement? RelatedContent
    {
        get => this.Related.Content as UIElement;
        set
        {
            this.Related.Content = value;
            this.Related.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public ToolDetails()
    {
        this.InitializeComponent();
    }

    internal void FocusComparison()
    {
        this.UpdateLayout();
        var field = FocusManager.FindFirstFocusableElement(this.Comparisons) as UIElement ?? this.Heading;
        field.Focus(FocusState.Keyboard);
        field.StartBringIntoView();
    }

    private ToolComparison? Comparison(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as ToolComparison;
    }

    public static Visibility RawBytesVisibility(RegistryValue? before, RegistryValue? after)
    {
        return before is null && after is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public static FontFamily ReadoutFont(RegistryValue? before, RegistryValue? after)
    {
        return new(before is not null || after is not null ? "Consolas" : "Segoe UI Variable, Segoe UI");
    }

    private void RawBytes_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is ToolComparison comparison && sender.Content is TextBlock readout)
        {
            readout.Text = comparison.DescribeByteDifference();
        }
    }

    private void InspectBefore_Click(object sender, RoutedEventArgs args)
    {
        if (this.Comparison(sender)?.BeforeValue is { } value)
        {
            this.InspectValueRequested?.Invoke(value);
        }
    }

    private void InspectAfter_Click(object sender, RoutedEventArgs args)
    {
        if (this.Comparison(sender)?.AfterValue is { } value)
        {
            this.InspectValueRequested?.Invoke(value);
        }
    }

    private void CopyBeforeBytes_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as MenuFlyoutItem)?.Tag is RegistryValue value)
        {
            this.CopyBytesRequested?.Invoke(value);
        }
    }

    private void CopyAfterBytes_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as MenuFlyoutItem)?.Tag is RegistryValue value)
        {
            this.CopyBytesRequested?.Invoke(value);
        }
    }

    internal void Show(
        string title,
        string path,
        string metadata,
        IEnumerable<ToolComparison>? comparisons = null,
        string limit = "",
        DateTimeOffset? timestamp = null,
        string warning = "")
    {
        this.Heading.Text = title;
        this.PathText.Text = path;
        this.PathText.Visibility = ToolView.TextVisibility(path);
        var timestampText = timestamp?.ToLocalTime().ToString("G") ?? string.Empty;
        this.Timestamp.Text = timestampText;
        var exactTimestamp = timestamp?.ToLocalTime().ToString("O") ?? string.Empty;
        ToolTipService.SetToolTip(this.Timestamp, exactTimestamp);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this.Timestamp, exactTimestamp);
        this.Timestamp.Visibility = ToolView.TextVisibility(timestampText);
        this.Metadata.Text = metadata;
        this.Metadata.Visibility = ToolView.TextVisibility(this.Metadata.Text);
        this.Warning.Message = warning;
        this.Warning.IsOpen = warning.Length > 0;
        if (this.Related.Content is ItemsControl related)
        {
            this.Related.Visibility = related.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var rows = comparisons?.ToArray() ?? [];
        if (!this.Comparisons.Items.Cast<ToolComparison>().SequenceEqual(rows))
        {
            this.Comparisons.Items.Clear();
            foreach (var comparison in rows)
            {
                this.Comparisons.Items.Add(comparison);
            }
        }

        this.Limit.Text = limit;
        this.Limit.Visibility = ToolView.TextVisibility(limit);
    }
}
