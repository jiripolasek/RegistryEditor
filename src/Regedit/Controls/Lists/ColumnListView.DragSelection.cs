// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace JPSoftworks.Regedit.Controls.Lists;

public sealed partial class ColumnListView
{
    public static readonly DependencyProperty IsDragSelectionEnabledProperty = DependencyProperty.Register(
        nameof(IsDragSelectionEnabled), typeof(bool), typeof(ColumnListView),
        new PropertyMetadata(true, (owner, _) => ((ColumnListView)owner).EndSelectionDrag()));

    public static readonly DependencyProperty IsDragHandleProperty = DependencyProperty.RegisterAttached(
        "IsDragHandle", typeof(bool), typeof(ColumnListView), new PropertyMetadata(false));

    private SelectionDrag? _selectionDrag;
    private DispatcherQueueTimer? _selectionScrollTimer;
    private int _selectionAnchor = -1;

    /// <summary>Allows mouse rectangle selection in multiple and extended selection modes.</summary>
    public bool IsDragSelectionEnabled
    {
        get => (bool)this.GetValue(IsDragSelectionEnabledProperty);
        set => this.SetValue(IsDragSelectionEnabledProperty, value);
    }

    public static bool GetIsDragHandle(DependencyObject element) => (bool)element.GetValue(IsDragHandleProperty);

    /// <summary>Marks the part of an item template that raises DragStarting after a mouse drag.</summary>
    public static void SetIsDragHandle(DependencyObject element, bool value) => element.SetValue(IsDragHandleProperty, value);

    /// <summary>At drag start, keeps a selected group or selects the pressed item and returns it alone.</summary>
    public IReadOnlyList<object> PrepareItemDrag(object item)
    {
        if (!this.Items.Items.Contains(item))
        {
            return [];
        }

        if (this.SupportsMultipleSelection && this.Items.SelectedItems.Contains(item))
        {
            return this.Items.SelectedItems.ToArray();
        }

        if (this.Items.SelectionMode != ListViewSelectionMode.None)
        {
            this.Items.SelectedItem = item;
            this._selectionAnchor = this.Items.SelectedIndex;
        }

        return new[] { item };
    }

    private void InitializeDragSelection()
    {
        this.Items.AddHandler(PointerPressedEvent, new PointerEventHandler(this.SelectionPressed), true);
        this.Items.PointerExited += this.SelectionPointerExited;
        this.Items.AddHandler(PointerMovedEvent, new PointerEventHandler(this.SelectionMoved), true);
        this.Items.AddHandler(PointerReleasedEvent, new PointerEventHandler(this.SelectionReleased), true);
        this.Items.AddHandler(PointerCanceledEvent, new PointerEventHandler(this.SelectionCanceled), true);
        this.Items.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(this.SelectionCaptureLost), true);
        this.Items.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && this._selectionDrag is { Active: true } drag)
            {
                this.ApplySelectionBand(drag, [], VirtualKeyModifiers.Shift);
                this.EndSelectionDrag();
                args.Handled = true;
            }
            else
            {
                this.SelectionModifierChanged(args.Key, true);
            }
        };
        this.Items.PreviewKeyUp += (_, args) => this.SelectionModifierChanged(args.Key, false);
        this.HorizontalScroll.ViewChanged += (_, _) => this.UpdateSelectionDrag();
    }

    private void SelectionPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this.Items);
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse
            || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || this.Items.Items.Count == 0)
        {
            return;
        }

        this.EndSelectionDrag();
        this.StopSlowClick();
        ListViewItem? item = null;
        FrameworkElement? handle = null;
        var rowIndex = -1;
        for (var source = args.OriginalSource as DependencyObject;
             source is not null && source != this.Items;
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is UIElement { CanDrag: true })
            {
                return;
            }

            if (source is ListViewItem row)
            {
                rowIndex = this.Items.IndexFromContainer(row);
                if (rowIndex < 0 || this.Items.CanDragItems || this.Items.CanReorderItems)
                {
                    return;
                }

                item = row;
                break;
            }

            if (source is FrameworkElement element && GetIsDragHandle(element))
            {
                handle ??= element;
            }
            else if (source is ButtonBase or ScrollBar or Thumb or TextBox or RichEditBox or PasswordBox
                     or Microsoft.UI.Xaml.Documents.Hyperlink or Control { IsTabStop: true } and not ScrollViewer
                     or TextBlock { IsTextSelectionEnabled: true } or RichTextBlock { IsTextSelectionEnabled: true })
            {
                return;
            }
        }

        var scroll = this._itemsScroll;
        if (handle is null && (!this.IsDragSelectionEnabled || !this.SupportsMultipleSelection || scroll is null))
        {
            return;
        }

        var position = point.Position;
        var viewport = new Point(position.X - this.HorizontalScroll.HorizontalOffset, position.Y);
        var origin = new Point(position.X, position.Y + (scroll?.VerticalOffset ?? 0));
        var anchor = this._selectionAnchor >= 0 ? this._selectionAnchor : this.Items.SelectedIndex;
        var start = handle is null ? this.RowsAt(position.Y) : (rowIndex, rowIndex + 1);
        if (start is not { } startRows)
        {
            return;
        }

        this._selectionDrag = new SelectionDrag(
            args.Pointer, item, origin, viewport, startRows, args.KeyModifiers, anchor,
            handle is null ? this.Items.SelectedRanges.ToArray() : [], handle);
    }

    private async void SelectionMoved(object sender, PointerRoutedEventArgs args)
    {
        if (this._selectionDrag is not { } drag || drag.Pointer.PointerId != args.Pointer.PointerId)
        {
            return;
        }

        var point = args.GetCurrentPoint(this.Items);
        if (!point.Properties.IsLeftButtonPressed)
        {
            this.EndSelectionDrag();
            return;
        }

        drag.Position = new Point(point.Position.X - this.HorizontalScroll.HorizontalOffset, point.Position.Y);
        drag.Modifiers = args.KeyModifiers;
        if (!drag.Active)
        {
            if (Math.Abs(drag.Position.X - drag.PressPosition.X) < ColumnHeaderRow.DragThreshold
                && Math.Abs(drag.Position.Y - drag.PressPosition.Y) < ColumnHeaderRow.DragThreshold)
            {
                return;
            }

            // Let the row or grip keep pointer capture until the gesture crosses the drag threshold.
            drag.Item?.ReleasePointerCapture(args.Pointer);
            if (drag.Handle is { } handle)
            {
                this.EndSelectionDrag();
                handle.ReleasePointerCapture(args.Pointer);
                args.Handled = true;
                if (handle.IsLoaded && Equals(drag.PressedItem, drag.Item?.Content))
                {
                    try
                    {
                        await handle.StartDragAsync(args.GetCurrentPoint(null));
                    }
                    catch (Exception ex) when (ex is OperationCanceledException || ex.HResult == unchecked((int)0x800704C7))
                    {
                        // WinUI can report a cancelled DragStarting event as ERROR_CANCELLED.
                    }
                }

                return;
            }

            drag.Active = true;
            this.Items.Focus(FocusState.Pointer);
            this.SelectionLayer.Visibility = Visibility.Visible;
            if (this._selectionScrollTimer is null)
            {
                this._selectionScrollTimer = this.DispatcherQueue.CreateTimer();
                this._selectionScrollTimer.Interval = TimeSpan.FromMilliseconds(32);
                this._selectionScrollTimer.Tick += this.ScrollDuringSelection;
            }

            this._selectionScrollTimer.Start();
        }

        this.UpdateSelectionDrag();
        args.Handled = true;
    }

    private void SelectionPointerExited(object sender, PointerRoutedEventArgs args)
    {
        // Let the row clear its hover state before the list captures the pointer.
        if (this._selectionDrag is { Active: true } drag
            && drag.Pointer.PointerId == args.Pointer.PointerId
            && args.GetCurrentPoint(this.Items).Properties.IsLeftButtonPressed
            && !this.Items.CapturePointer(args.Pointer))
        {
            this.EndSelectionDrag();
        }
    }

    private void SelectionReleased(object sender, PointerRoutedEventArgs args)
    {
        if (this._selectionDrag is not { } drag || drag.Pointer.PointerId != args.Pointer.PointerId)
        {
            return;
        }

        if (drag.Active)
        {
            var position = args.GetCurrentPoint(this.Items).Position;
            drag.Position = new Point(position.X - this.HorizontalScroll.HorizontalOffset, position.Y);
            this.UpdateSelectionDrag();
            args.Handled = true;
        }

        if (ReferenceEquals(this._selectionDrag, drag) && !drag.Modifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            if (drag.LastRanges is { Count: > 0 } ranges)
            {
                this._selectionAnchor = Math.Clamp(drag.StartRows.First, ranges[0].First, ranges[^1].After - 1);
            }
            else if (!drag.Active && drag.Item is { } item)
            {
                this._selectionAnchor = this.Items.IndexFromContainer(item);
            }
        }

        this.EndSelectionDrag();
    }

    private void SelectionCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (this._selectionDrag?.Pointer.PointerId == args.Pointer.PointerId)
        {
            this.EndSelectionDrag();
        }
    }

    private void SelectionModifierChanged(VirtualKey key, bool down)
    {
        var modifier = key switch
        {
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => VirtualKeyModifiers.Control,
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => VirtualKeyModifiers.Shift,
            _ => VirtualKeyModifiers.None
        };
        if (modifier != VirtualKeyModifiers.None && this._selectionDrag is { Active: true } drag)
        {
            drag.Modifiers = down ? drag.Modifiers | modifier : drag.Modifiers & ~modifier;
            this.UpdateSelectionDrag();
        }
    }

    private void SelectionCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, this.Items))
        {
            this.SelectionCanceled(sender, args);
        }
    }

    private void EndSelectionDrag()
    {
        var drag = this._selectionDrag;
        this._selectionDrag = null;
        if (drag is { Active: true })
        {
            this._selectionScrollTimer?.Stop();
            this.Items.ReleasePointerCapture(drag.Pointer);
            this.SelectionLayer.Visibility = Visibility.Collapsed;
        }
    }

    private void ScrollDuringSelection(DispatcherQueueTimer timer, object args)
    {
        if (this._selectionDrag is not { Active: true } drag || this._itemsScroll is not { } scroll)
        {
            return;
        }

        var horizontal = this.HorizontalScroll;
        var x = Math.Clamp(
            horizontal.HorizontalOffset + EdgeStep(drag.Position.X, horizontal.ViewportWidth), 0, horizontal.ScrollableWidth);
        var y = Math.Clamp(scroll.VerticalOffset + EdgeStep(drag.Position.Y, scroll.ViewportHeight), 0, scroll.ScrollableHeight);
        if (x != horizontal.HorizontalOffset)
        {
            horizontal.ChangeView(x, null, null, true);
        }

        if (y != scroll.VerticalOffset)
        {
            scroll.ChangeView(null, y, null, true);
        }

        static double EdgeStep(double position, double extent)
        {
            var zone = Math.Min(32, extent / 2);
            return Math.Clamp(position - zone, -32, 0) + Math.Clamp(position - (extent - zone), 0, 32);
        }
    }

    private void UpdateSelectionDrag()
    {
        if (this._selectionDrag is not { Active: true } drag || this._itemsScroll is not { } scroll)
        {
            return;
        }

        var horizontal = this.HorizontalScroll;
        var x = horizontal.HorizontalOffset + Math.Clamp(drag.Position.X, 0, horizontal.ViewportWidth);
        var y = Math.Clamp(drag.Position.Y, 0, scroll.ViewportHeight);
        var originX = Math.Clamp(drag.Origin.X, horizontal.HorizontalOffset, horizontal.HorizontalOffset + horizontal.ViewportWidth);
        var originY = Math.Clamp(drag.Origin.Y - scroll.VerticalOffset, 0, scroll.ViewportHeight);
        Canvas.SetLeft(this.SelectionRectangle, Math.Min(originX, x));
        Canvas.SetTop(this.SelectionRectangle, Math.Min(originY, y));
        this.SelectionRectangle.Width = Math.Max(1, Math.Abs(x - originX));
        this.SelectionRectangle.Height = Math.Max(1, Math.Abs(y - originY));

        if (this.RowsAt(y, drag) is not { } end)
        {
            return;
        }

        var first = Math.Min(drag.StartRows.First, end.First);
        var after = Math.Max(drag.StartRows.After, end.After);
        var ranges = drag.Intersections(first, after, Math.Min(drag.Origin.X, x), Math.Max(drag.Origin.X, x));
        if (ranges.Count > 0 && drag.Modifiers.HasFlag(VirtualKeyModifiers.Shift) && drag.Anchor >= 0)
        {
            ranges[0] = (Math.Min(ranges[0].First, drag.Anchor), ranges[0].After);
            ranges[^1] = (ranges[^1].First, Math.Max(ranges[^1].After, drag.Anchor + 1));
        }

        if (drag.LastRanges is null || drag.LastModifiers != drag.Modifiers || !drag.LastRanges.SequenceEqual(ranges))
        {
            drag.LastRanges = ranges;
            drag.LastModifiers = drag.Modifiers;
            this.ApplySelectionBand(drag, ranges, drag.Modifiers);
        }
    }

    // A point inside a row spans one index. A point in empty space is the gap before the next row.
    private (int First, int After)? RowsAt(double y, SelectionDrag? drag = null)
    {
        if (this.Items.ItemsPanelRoot is not { } panel)
        {
            return null;
        }

        if (drag is not null)
        {
            drag.PanelBounds = panel.TransformToVisual(this.Items).TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
        }

        // Native AOT can expose the virtualizing panel as Panel. Its realized children still map to native item indices.
        var previous = -1;
        var next = int.MaxValue;
        var hit = -1;
        foreach (var item in panel.Children)
        {
            var index = this.Items.IndexFromContainer(item);
            if (index < 0 || item.RenderSize.Height <= 0)
            {
                continue;
            }

            var bounds = item.TransformToVisual(this.Items).TransformBounds(new Rect(default, item.RenderSize));
            if (drag is not null)
            {
                if (bounds.Left == drag.PanelBounds.Left && bounds.Right == drag.PanelBounds.Right)
                {
                    drag.CustomBounds.Remove(index);
                }
                else
                {
                    drag.CustomBounds[index] = (bounds.Left, bounds.Right);
                }
            }

            if (y < bounds.Top)
            {
                next = Math.Min(next, index);
            }
            else if (y >= bounds.Bottom)
            {
                previous = Math.Max(previous, index);
            }
            else if (hit < 0)
            {
                hit = index;
            }
            else
            {
                // A horizontal line must identify at most one row in a vertically stacked list.
                return null;
            }
        }

        if (hit >= 0)
        {
            return (hit, hit + 1);
        }

        var gap = next == int.MaxValue ? previous + 1 : next;
        return previous < 0 && next == int.MaxValue ? null : (gap, gap);
    }

    // Only change the native ranges where the desired selection differs, including virtualized rows between the endpoints.
    private void ApplySelectionBand(SelectionDrag drag, IReadOnlyList<(int First, int After)> ranges, VirtualKeyModifiers modifiers)
    {
        var extend = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        var toggle = !extend && modifiers.HasFlag(VirtualKeyModifiers.Control);
        var current = this.Items.SelectedRanges.ToArray();
        var count = this.Items.Items.Count;
        var currentIndex = 0;
        var originalIndex = 0;
        var hitIndex = 0;
        for (var index = 0; index < count && ReferenceEquals(this._selectionDrag, drag);)
        {
            var next = count;
            var selected = IsSelectedAt(current, ref currentIndex, index, ref next);
            var previous = (extend || toggle) && IsSelectedAt(drag.OriginalSelection, ref originalIndex, index, ref next);
            while (hitIndex < ranges.Count && index >= ranges[hitIndex].After)
            {
                hitIndex++;
            }

            var inside = hitIndex < ranges.Count && index >= ranges[hitIndex].First;
            if (hitIndex < ranges.Count)
            {
                next = Math.Min(next, inside ? ranges[hitIndex].After : ranges[hitIndex].First);
            }

            var wanted = toggle ? previous ^ inside : previous || inside;
            if (selected != wanted)
            {
                var range = new ItemIndexRange(index, (uint)(next - index));
                if (wanted)
                {
                    this.Items.SelectRange(range);
                }
                else
                {
                    this.Items.DeselectRange(range);
                }
            }

            index = next;
        }

        static bool IsSelectedAt(ItemIndexRange[] ranges, ref int run, int index, ref int next)
        {
            while (run < ranges.Length && ranges[run].LastIndex < index)
            {
                run++;
            }

            if (run == ranges.Length)
            {
                return false;
            }

            var selected = ranges[run].FirstIndex <= index;
            next = Math.Min(next, selected ? ranges[run].LastIndex + 1 : ranges[run].FirstIndex);
            return selected;
        }
    }

    private sealed class SelectionDrag(
        Pointer pointer,
        ListViewItem? item,
        Point origin,
        Point pressPosition,
        (int First, int After) startRows,
        VirtualKeyModifiers modifiers,
        int anchor,
        ItemIndexRange[] originalSelection,
        FrameworkElement? handle)
    {
        internal readonly Pointer Pointer = pointer;
        internal readonly ListViewItem? Item = item;
        internal readonly FrameworkElement? Handle = handle;
        internal readonly object? PressedItem = item?.Content;
        internal readonly Point Origin = origin;
        internal readonly Point PressPosition = pressPosition;
        internal readonly (int First, int After) StartRows = startRows;
        internal readonly int Anchor = anchor;
        internal readonly ItemIndexRange[] OriginalSelection = originalSelection;
        internal readonly SortedDictionary<int, (double Left, double Right)> CustomBounds = [];
        internal VirtualKeyModifiers Modifiers = modifiers;
        internal Point Position = pressPosition;
        internal Rect PanelBounds = Rect.Empty;
        internal List<(int First, int After)>? LastRanges;
        internal VirtualKeyModifiers LastModifiers;
        internal bool Active;

        // Keep measured exceptions as rows scroll out. Stretched rows share the panel's horizontal bounds.
        internal List<(int First, int After)> Intersections(int first, int after, double left, double right)
        {
            var ranges = new List<(int First, int After)>();
            var hitsPanel = left < this.PanelBounds.Right && right >= this.PanelBounds.Left;
            var start = first;
            foreach (var (index, bounds) in this.CustomBounds)
            {
                if (index >= after)
                {
                    break;
                }

                var hitsRow = bounds.Right > bounds.Left && left < bounds.Right && right >= bounds.Left;
                if (index < first || hitsRow == hitsPanel)
                {
                    continue;
                }

                if (!hitsPanel)
                {
                    ranges.Add((index, index + 1));
                }
                else if (start < index)
                {
                    ranges.Add((start, index));
                }

                start = index + 1;
            }

            if (hitsPanel && start < after)
            {
                ranges.Add((start, after));
            }

            return ranges;
        }
    }
}
