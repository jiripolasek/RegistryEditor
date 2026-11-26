// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Lists;

public sealed partial class ColumnListView
{
    private event Action? CompactAutomationNameChanged;

    private bool _updatingSelectAll;

    private bool SupportsMultipleSelection => this.Items.SelectionMode is ListViewSelectionMode.Extended or ListViewSelectionMode.Multiple;

    internal SelectionCheckBoxMode EffectiveCheckBoxes
    {
        get
        {
            if (!this.SupportsMultipleSelection)
            {
                return SelectionCheckBoxMode.Default;
            }

            var hasLeadingCell = this.LeadingColumn is not null && (this.ShowColumns || this.CompactItemTemplate is null);
            return this.SelectionCheckBoxes == SelectionCheckBoxMode.Overlay && !hasLeadingCell
                ? SelectionCheckBoxMode.Inline
                : this.SelectionCheckBoxes;
        }
    }

    private void UpdateCheckBoxPresentation()
    {
        var enabled = this.EffectiveCheckBoxes != SelectionCheckBoxMode.Default;
        VisualStateManager.GoToState(this, enabled ? "CustomCheckBoxes" : "NativeCheckBoxes", false);
        this.SelectAllBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        this.UpdateSelectAll();
    }

    private void UpdateSelectAll()
    {
        if (this.SelectAllBox.Visibility != Visibility.Visible)
        {
            return;
        }

        var count = this.Items.Items.Count;
        var selected = this.Items.SelectedItems.Count;
        this._updatingSelectAll = true;
        try
        {
            this.SelectAllBox.IsEnabled = count > 0;
            this.SelectAllBox.IsChecked = selected == 0 ? false : selected == count ? true : null;
        }
        finally
        {
            this._updatingSelectAll = false;
        }
    }

    private void SelectAllToggled(object sender, RoutedEventArgs args)
    {
        if (this._updatingSelectAll || !this.SupportsMultipleSelection || this.Items.Items.Count == 0)
        {
            return;
        }

        if (this.Items.SelectedItems.Count == this.Items.Items.Count)
        {
            this.Items.DeselectRange(new ItemIndexRange(0, (uint)this.Items.Items.Count));
        }
        else
        {
            this.Items.SelectAll();
        }
    }

    internal CheckBox CreateRowCheckBox()
    {
        var box = new CheckBox { IsTabStop = false };
        AutomationProperties.SetName(box, "Select item");
        box.Loaded += this.SelectionCheckBoxLoaded;
        box.Click += this.RowCheckBoxClicked;
        box.DoubleTapped += this.RowCheckBoxDoubleTapped;
        return box;
    }

    private void RowCheckBoxClicked(object sender, RoutedEventArgs args)
    {
        if (FindRowContainer((CheckBox)sender) is { } container)
        {
            this._selectionAnchor = this.Items.IndexFromContainer(container);
        }
    }

    private void RowCheckBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        args.Handled = true;
    }

    private void SelectionCheckBoxLoaded(object sender, RoutedEventArgs args)
    {
        var box = (CheckBox)sender;
        // Keep the host's implicit CheckBox style and fit its unlabeled glyph into the cell.
        box.MinWidth = 0;
        box.MinHeight = 0;
        box.Padding = default;
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        if (box != this.SelectAllBox && FindRowContainer(box) is { } container)
        {
            box.SetBinding(
                ToggleButton.IsCheckedProperty,
                new Binding { Source = container, Path = new PropertyPath(nameof(ListViewItem.IsSelected)), Mode = BindingMode.TwoWay });
        }
    }

    private static ListViewItem? FindRowContainer(CheckBox box)
    {
        for (var parent = VisualTreeHelper.GetParent(box); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ListViewItem container)
            {
                return container;
            }
        }

        return null;
    }

    private void CompactContentLoaded(object sender, RoutedEventArgs args)
    {
        var host = (Border)sender;
        var row = (Grid)host.Parent;
        var box = (CheckBox)row.Children[0];
        FrameworkElement? content = null;
        long nameToken = 0;
        INotifyPropertyChanged? item = null;

        void UpdateName()
        {
            var name = content is null ? null : AutomationProperties.GetName(content);
            if (string.IsNullOrEmpty(name))
            {
                name = this.ItemAutomationName?.Invoke(row.DataContext) ?? row.DataContext?.ToString();
            }

            AutomationProperties.SetName(row, name ?? string.Empty);
            AutomationProperties.SetName(box, string.IsNullOrEmpty(name) ? "Select item" : "Select " + name);
        }

        void LoadTemplate(DependencyObject owner, DependencyProperty property)
        {
            content?.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, nameToken);
            content = this.CompactItemTemplate?.LoadContent() as FrameworkElement;
            host.Child = content;
            if (content is not null)
            {
                nameToken = content.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, (_, _) => UpdateName());
            }

            UpdateName();
        }

        void ItemChanged(object? source, PropertyChangedEventArgs change) => UpdateName();

        void ObserveItem(object? value)
        {
            if (item is not null)
            {
                item.PropertyChanged -= ItemChanged;
            }

            item = value as INotifyPropertyChanged;
            if (item is not null)
            {
                item.PropertyChanged += ItemChanged;
            }
        }

        void ContextChanged(FrameworkElement element, DataContextChangedEventArgs change)
        {
            ObserveItem(change.NewValue);
            UpdateName();
        }

        ObserveItem(row.DataContext);
        row.DataContextChanged += ContextChanged;
        this.CompactAutomationNameChanged += UpdateName;
        var templateToken = this.RegisterPropertyChangedCallback(CompactItemTemplateProperty, LoadTemplate);
        LoadTemplate(this, CompactItemTemplateProperty);
        host.Unloaded += Unloaded;

        void Unloaded(object source, RoutedEventArgs e)
        {
            host.Unloaded -= Unloaded;
            this.UnregisterPropertyChangedCallback(CompactItemTemplateProperty, templateToken);
            this.CompactAutomationNameChanged -= UpdateName;
            row.DataContextChanged -= ContextChanged;
            ObserveItem(null);
            content?.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, nameToken);
        }
    }
}
