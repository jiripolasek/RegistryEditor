// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Shared;

internal static class ListContextMenu
{
    internal static void Attach(ListView list, Func<MenuFlyout> create)
    {
        list.ContextRequested += (_, args) =>
        {
            var source = args.OriginalSource as DependencyObject;
            while (source is not null && source != list && source is not ListViewItem)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            var hasPosition = args.TryGetPosition(list, out var point);
            if (source is ListViewItem item && !list.SelectedItems.Contains(item.Content))
            {
                list.SelectedItem = item.Content;
            }
            else if (source is not ListViewItem && hasPosition)
            {
                list.SelectedItems.Clear();
            }

            var menu = create();
            if (hasPosition)
            {
                menu.ShowAt(list, new FlyoutShowOptions { Position = point });
            }
            else
            {
                menu.ShowAt(
                    list.SelectedItem is { } selected
                        ? list.ContainerFromItem(selected) as FrameworkElement ?? list
                        : list);
            }

            args.Handled = true;
        };
    }

    internal static void Add(
        MenuFlyout menu,
        string label,
        Action execute,
        bool enabled = true,
        Symbol? icon = null,
        string? shortcut = null,
        IconSource? iconSource = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = label,
            IsEnabled = enabled,
            KeyboardAcceleratorTextOverride = shortcut ?? string.Empty,
            Icon = iconSource is not null ? new IconSourceElement { IconSource = iconSource } :
                icon is { } symbol ? new FontIcon { Glyph = SymbolGlyphs.Get(symbol) } : null
        };
        item.Click += (_, _) => execute();
        menu.Items.Add(item);
    }
}
