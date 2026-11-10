// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Shared;

internal static class ResultListViewport
{
    internal static Action? Capture(ListView list, IReadOnlyList<InvestigationRow> rows)
    {
        if (list.ItemsPanelRoot is not { } panel || rows.Count == 0)
        {
            return null;
        }

        if (FindScrollViewer(list) is not { } scroll || scroll.VerticalOffset == 0)
        {
            return null;
        }

        var offset = scroll.VerticalOffset;
        InvestigationRow? anchor = null;
        var top = scroll.ViewportHeight;
        // Native AOT can project ItemsPanelRoot as Panel, so locate the anchor among its realized children.
        foreach (var child in panel.Children)
        {
            var y = child.TransformToVisual(scroll).TransformPoint(default).Y;
            if (y < top && y + child.RenderSize.Height > 0 && list.ItemFromContainer(child) is InvestigationRow row)
            {
                anchor = row;
                top = y;
            }
        }

        UIElement? FindContainer(InvestigationRow item)
        {
            return list.ItemsPanelRoot?.Children.FirstOrDefault(child => ReferenceEquals(
                list.ItemFromContainer(child), item));
        }

        return () =>
        {
            list.UpdateLayout();
            var target = offset;
            if (anchor is not null && rows.FirstOrDefault(row => SameItem(row, anchor)) is { } surviving)
            {
                var container = FindContainer(surviving);
                if (container is null)
                {
                    list.ScrollIntoView(surviving, ScrollIntoViewAlignment.Leading);
                    list.UpdateLayout();
                    container = FindContainer(surviving);
                }

                if (container is not null)
                {
                    target = scroll.VerticalOffset + container.TransformToVisual(scroll).TransformPoint(default).Y -
                             top;
                }
            }

            scroll.ChangeView(null, Math.Clamp(target, 0, scroll.ScrollableHeight), null, true);
        };
    }

    internal static ScrollViewer? FindScrollViewer(ListView list)
    {
        DependencyObject? parent = list.ItemsPanelRoot;
        while (parent is not null && parent is not ScrollViewer)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        return parent as ScrollViewer;
    }

    private static bool SameItem(InvestigationRow row, InvestigationRow anchor)
    {
        return anchor.Source switch
        {
            SavedSearch or RegistryWorkspace => row.Title.Equals(anchor.Title, StringComparison.OrdinalIgnoreCase),
            JournalEntry entry => row.Source is JournalEntry other && other.Id == entry.Id,
            _ => ReferenceEquals(row.Source, anchor.Source)
        };
    }
}
