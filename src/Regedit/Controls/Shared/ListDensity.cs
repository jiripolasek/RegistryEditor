// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Shared;

internal sealed partial class ListDensity : DependencyObject
{
    private static readonly DependencyProperty SpacingProperty = DependencyProperty.RegisterAttached(
        "Spacing", typeof(double), typeof(ListDensity), new PropertyMetadata(3d));

    internal static void SetSpacing(ListView list, double spacing)
    {
        if (list.ReadLocalValue(SpacingProperty) == DependencyProperty.UnsetValue)
        {
            list.ContainerContentChanging += ContainerChanged;
            list.Loaded += ListLoaded;
        }

        list.SetValue(SpacingProperty, spacing);
        ApplyRealized(list, spacing);
    }

    private static void ListLoaded(object sender, RoutedEventArgs args)
    {
        var list = (ListView)sender;
        ApplyRealized(list, (double)list.GetValue(SpacingProperty));
    }

    private static void ContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        ApplyContainer(args.ItemContainer, (double)sender.GetValue(SpacingProperty));
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, ContainerChanged);
        }
    }

    private static void ApplyRealized(DependencyObject element, double spacing)
    {
        if (element is ListViewItem item)
        {
            ApplyContainer(item, spacing);
            return;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            ApplyRealized(VisualTreeHelper.GetChild(element, index), spacing);
        }
    }

    private static void ApplyContainer(SelectorItem item, double spacing)
    {
        item.MinHeight = 22 + 2 * spacing;
        if (item.ContentTemplateRoot is Grid row)
        {
            row.Padding = new Thickness(0, spacing, 0, spacing);
            row.MinHeight = item.MinHeight;
        }
    }
}
