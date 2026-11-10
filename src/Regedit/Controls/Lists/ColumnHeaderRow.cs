// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace JPSoftworks.Regedit.Controls.Lists;

/// <summary>
///     Header of a <see cref="ColumnListView" />: a sort button per column, a resize grip on each column's trailing
///     edge, drag-and-drop reordering with a ghost and an insertion marker, a configurable shortcut (default
///     Ctrl+Shift+Left/Right) to move the focused column,
///     and a context menu for visibility, order, fitting and reset.
/// </summary>
public sealed partial class ColumnHeaderRow : Grid
{
    internal const double KeyboardStep = 20;
    internal const double DragThreshold = 6;
    private const double AutoScrollZone = 32;
    private const double AutoScrollMaxStep = 24;

    // How far above or below the header a drop still counts; beyond it the drag shows as "no drop".
    private const double DropBand = 24;

    // Segoe Fluent Icons ChevronUpSmall / ChevronDownSmall, as File Explorer uses for its sort indicator.
    internal const string SortAscendingGlyph = "";
    internal const string SortDescendingGlyph = "";

    private readonly Dictionary<ListColumn, (Button Label, ResizeThumb Grip, FontIcon SortIcon)> _headers = [];
    private readonly MenuFlyout _menu;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _ghost;
    private readonly TextBlock _ghostText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    private readonly Rectangle _marker = new()
    {
        Width = 2, RadiusX = 1, RadiusY = 1, Visibility = Visibility.Collapsed
    };

    private ColumnListView? _owner;
    private ColumnArrangement _arrangement = ColumnArrangement.Empty;
    private DragState? _drag;
    private DispatcherQueueTimer? _autoScroll;
    private double _autoScrollStep;

    public ColumnHeaderRow()
    {
        this.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        this._menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        this._menu.Opening += (_, _) => this.FillMenu();
        this.ContextFlyout = this._menu;

        this._ghost = new Border
        {
            Child = this._ghostText,
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Visibility = Visibility.Collapsed
        };
        this._overlay.Children.Add(this._ghost);
        this._overlay.Children.Add(this._marker);
        SetColumnSpan(this._overlay, 1000);
        Canvas.SetZIndex(this._overlay, 10);

        // The pressed header button captures the pointer, so moves and the release arrive through it already handled.
        this.AddHandler(PointerMovedEvent, new PointerEventHandler(this.HeaderMoved), true);
        this.AddHandler(PointerReleasedEvent, new PointerEventHandler(this.HeaderReleased), true);
        this.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => this.CancelColumnDrag()), true);
        this.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(this.HeaderCaptureLost), true);
        this.AddHandler(KeyDownEvent, new KeyEventHandler(this.HeaderKeyDown), true);
    }

    internal MenuFlyout Menu => this._menu;

    internal bool IsDragging => this._drag is { Started: true, Cancelled: false };

    internal FrameworkElement DragGhost => this._ghost;

    internal FrameworkElement InsertionMarker => this._marker;

    /// <summary>The column the current drag would drop before (null = after the last column), when the drop is valid.</summary>
    internal (bool Valid, string? BeforeId) DragTarget =>
        this._drag is { } drag ? (drag.Valid, drag.BeforeId) : (false, null);

    internal Button? LabelFor(ListColumn column)
    {
        return this._headers.TryGetValue(column, out var header) ? header.Label : null;
    }

    internal ResizeThumb? GripFor(ListColumn column)
    {
        return this._headers.TryGetValue(column, out var header) ? header.Grip : null;
    }

    internal FontIcon? SortIconFor(ListColumn column)
    {
        return this._headers.TryGetValue(column, out var header) ? header.SortIcon : null;
    }

    internal void Rebuild(
        ColumnListView owner,
        IEnumerable<ListColumn> columns,
        Style? labelStyle,
        Style? sortIconStyle)
    {
        this.CancelColumnDrag();
        this._drag = null;
        this._owner = owner;
        this.Children.Clear();
        this._headers.Clear();
        foreach (var column in columns)
        {
            var sortIcon = new FontIcon { Style = sortIconStyle, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(sortIcon, AccessibilityView.Raw);
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = column.Header, VerticalAlignment = VerticalAlignment.Center },
                    sortIcon
                }
            };
            var label = new Button
            {
                Style = labelStyle,
                Tag = column,
                Content = content,
                ContextFlyout = this._menu,
                HorizontalContentAlignment = column.HeaderAlignment,
                IsTabStop = column.Sortable || column.CanReorder
            };
            AutomationProperties.SetName(label, column.Name);
            AutomationProperties.SetAutomationId(label, column.Id + (column.Sortable ? "SortButton" : "Header"));

            label.Click += (_, _) =>
            {
                // A drag that ends over its own header must not also sort.
                if (column.Sortable && this._drag is not { Started: true })
                {
                    owner.RequestSort(column.Id);
                }
            };
            label.AddHandler(
                PointerPressedEvent, new PointerEventHandler((_, args) =>
                {
                    var point = args.GetCurrentPoint(this);
                    this._drag = point.Properties.IsLeftButtonPressed && column.CanReorder
                        ? new DragState(column, point.PointerId, point.Position.X)
                        : null;
                }), true);

            var grip = new ResizeThumb
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = column.CanResize ? Visibility.Visible : Visibility.Collapsed
            };
            Canvas.SetZIndex(grip, 1);
            AutomationProperties.SetName(grip, "Resize " + column.Name + " column");
            AutomationProperties.SetAutomationId(grip, column.Id + "Resize");
            grip.DragDelta += (_, args) => owner.ResizeColumn(
                column.Id, owner.CurrentWidth(column.Id) + args.HorizontalChange);
            grip.KeyDown += (_, args) =>
            {
                if (args.Key is VirtualKey.Left or VirtualKey.Right)
                {
                    owner.ResizeColumn(
                        column.Id,
                        owner.CurrentWidth(column.Id) + (args.Key == VirtualKey.Left ? -KeyboardStep : KeyboardStep));
                    args.Handled = true;
                }
                else if (args.Key == VirtualKey.Enter)
                {
                    owner.AutoFitColumn(column.Id);
                    args.Handled = true;
                }
            };
            grip.AddHandler(
                DoubleTappedEvent, new DoubleTappedEventHandler((_, args) =>
                {
                    owner.AutoFitColumn(column.Id);
                    args.Handled = true;
                }), true);

            this.Children.Add(label);
            this.Children.Add(grip);
            this._headers.Add(column, (label, grip, sortIcon));
        }

        this.Children.Add(this._overlay);
        this._arrangement = ColumnArrangement.Empty;
    }

    internal void Apply(ColumnArrangement arrangement, string? sortColumnId, bool sortDescending)
    {
        this._arrangement = arrangement;
        this.ColumnSpacing = arrangement.Spacing;
        ColumnRow.SetWidths(this, arrangement.Widths);
        var gripMargin = new Thickness(0, 0, -(arrangement.Spacing / 2) - 4, 0);
        foreach (var (column, (label, grip, sortIcon)) in this._headers)
        {
            var slot = arrangement.SlotOf(column);
            label.Visibility = slot < 0 ? Visibility.Collapsed : Visibility.Visible;
            grip.Visibility = slot < 0 || !column.CanResize ? Visibility.Collapsed : Visibility.Visible;
            if (slot < 0)
            {
                continue;
            }

            SetColumn(label, slot);
            SetColumn(grip, slot);
            grip.Margin = gripMargin;
            label.TabIndex = slot;
            grip.TabIndex = arrangement.Slots.Count + slot;
            var sorted = column.Sortable && column.Id == sortColumnId;
            sortIcon.Visibility = sorted ? Visibility.Visible : Visibility.Collapsed;
            sortIcon.Glyph = sortDescending ? SortDescendingGlyph : SortAscendingGlyph;
            AutomationProperties.SetItemStatus(
                label,
                !column.Sortable ? string.Empty :
                sorted ? sortDescending ? "Sorted descending" : "Sorted ascending" : "Not sorted");
        }
    }

    /// <summary>Returns the visible column whose header spans <paramref name="x" /> (header coordinates).</summary>
    internal ListColumn? ColumnAt(double x)
    {
        foreach (var (column, label) in this.VisibleLabels())
        {
            var left = label.TransformToVisual(this).TransformPoint(default).X;
            if (x >= left && x <= left + label.ActualWidth)
            {
                return column;
            }
        }

        return null;
    }

    // The drag pipeline below is driven by pointer events and, in the self-test, called directly.

    internal void StartColumnDrag(ListColumn column, double x)
    {
        this._drag = new DragState(column, 0, x) { Started = false };
        this.BeginVisuals(this._drag, x);
    }

    internal void MoveColumnDrag(double x, double y)
    {
        if (this._drag is { Started: true, Cancelled: false } drag)
        {
            this.UpdateDrag(drag, x, y);
        }
    }

    internal bool DropColumnDrag()
    {
        var drag = this._drag;
        this._drag = null;
        this.EndVisuals();
        if (drag is not { Started: true, Cancelled: false, Valid: true } || this._owner is null)
        {
            return false;
        }

        this._owner.MoveColumnBefore(drag.Column.Id, drag.BeforeId);
        return true;
    }

    /// <summary>Ends the drag visuals without moving; the pending release is still swallowed so it cannot sort.</summary>
    internal void CancelColumnDrag()
    {
        if (this._drag is { } drag)
        {
            drag.Cancelled = true;
        }

        this.EndVisuals();
    }

    /// <summary>The reorder shortcut on a focused header moves its column; returns true when the key was used.</summary>
    internal bool MoveByKeyboard(ListColumn column, int direction)
    {
        if (this._owner is not { } owner || !column.CanReorder || !owner.CanMoveColumn(column.Id, direction))
        {
            return false;
        }

        owner.MoveColumnBy(column.Id, direction);
        return true;
    }

    private void HeaderMoved(object sender, PointerRoutedEventArgs args)
    {
        if (this._drag is not { Cancelled: false } drag || args.Pointer.PointerId != drag.Pointer)
        {
            return;
        }

        var point = args.GetCurrentPoint(this).Position;
        if (!drag.Started)
        {
            if (Math.Abs(point.X - drag.StartX) < DragThreshold)
            {
                return;
            }

            this.BeginVisuals(drag, drag.StartX);
        }

        if (this._owner?.HeaderScroll is { } scroll)
        {
            drag.LastInScroll = args.GetCurrentPoint(scroll).Position;
            this.UpdateAutoScroll(scroll, drag.LastInScroll.X);
        }

        this.UpdateDrag(drag, point.X, point.Y);
    }

    private void HeaderReleased(object sender, PointerRoutedEventArgs args)
    {
        if (this._drag is not { } drag || args.Pointer.PointerId != drag.Pointer)
        {
            return;
        }

        if (drag.Started)
        {
            this.DropColumnDrag();
            args.Handled = true;
        }
        else
        {
            this._drag = null;
        }
    }

    // The header button releases its capture while handling the release, which can arrive here before the
    // release itself; only treat it as a cancel when no release followed in the same input dispatch.
    private void HeaderCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        var drag = this._drag;
        if (drag is null)
        {
            return;
        }

        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(this._drag, drag))
            {
                this.CancelColumnDrag();
                this._drag = null;
            }
        });
    }

    private void HeaderKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && this.IsDragging)
        {
            this.CancelColumnDrag();
            args.Handled = true;
            return;
        }

        if (args.Key is VirtualKey.Left or VirtualKey.Right &&
            this._owner is { KeyboardReorderModifiers: not VirtualKeyModifiers.None } owner &&
            CurrentModifiers() == owner.KeyboardReorderModifiers &&
            (args.OriginalSource as Button)?.Tag is ListColumn column &&
            this.MoveByKeyboard(column, args.Key == VirtualKey.Left ? -1 : 1))
        {
            args.Handled = true;
        }
    }

    private static VirtualKeyModifiers CurrentModifiers()
    {
        static bool Down(VirtualKey key)
        {
            return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        }

        return (Down(VirtualKey.Control) ? VirtualKeyModifiers.Control : VirtualKeyModifiers.None) |
               (Down(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None) |
               (Down(VirtualKey.Menu) ? VirtualKeyModifiers.Menu : VirtualKeyModifiers.None) |
               (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)
                   ? VirtualKeyModifiers.Windows
                   : VirtualKeyModifiers.None);
    }

    private void BeginVisuals(DragState drag, double x)
    {
        if (this.LabelFor(drag.Column) is not { } label)
        {
            return;
        }

        drag.Started = true;
        var left = label.TransformToVisual(this).TransformPoint(default).X;
        drag.GrabOffset = Math.Clamp(x - left, 0, label.ActualWidth);
        this._ghostText.Text = drag.Column.Header;
        this._ghostText.HorizontalAlignment = drag.Column.HeaderAlignment;
        this._ghost.Width = label.ActualWidth;
        this._ghost.Height = label.ActualHeight;
        this._ghost.Background = ThemeBrush("SolidBackgroundFillColorTertiaryBrush");
        this._ghost.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush");
        this._ghostText.Foreground = ThemeBrush("TextFillColorPrimaryBrush");
        this._marker.Fill = ThemeBrush("AccentFillColorDefaultBrush");
        this._marker.Height = Math.Max(0, this.ActualHeight - this.Padding.Top - this.Padding.Bottom);
        this._ghost.Visibility = Visibility.Visible;
        label.Opacity = 0.4;
        this.UpdateDrag(drag, x, this.ActualHeight / 2);
    }

    private void UpdateDrag(DragState drag, double x, double y)
    {
        var overlayOrigin = this.TransformToVisual(this._overlay).TransformPoint(default);
        Canvas.SetLeft(this._ghost, x + overlayOrigin.X - drag.GrabOffset);
        Canvas.SetTop(this._ghost, 0);

        string? before = null;
        double? markerX = null;
        var spacing = this._arrangement.Spacing;
        var labels = this.VisibleLabels().ToArray();
        foreach (var (column, label) in labels)
        {
            var left = label.TransformToVisual(this).TransformPoint(default).X;
            if (x < left + label.ActualWidth / 2)
            {
                before = column.Id;
                markerX = left - spacing / 2;
                break;
            }
        }

        if (markerX is null && labels.Length > 0)
        {
            var last = labels[^1].Label;
            markerX = last.TransformToVisual(this).TransformPoint(default).X + last.ActualWidth + spacing / 2;
        }

        var inBand = y >= -DropBand && y <= this.ActualHeight + DropBand;
        drag.BeforeId = before;
        drag.Valid = inBand && this._owner is { } owner && owner.CanMoveColumnBefore(drag.Column.Id, before);
        this._ghost.Opacity = drag.Valid || !inBand ? 0.9 : 0.6;
        if (drag.Valid && markerX is { } markerLeft)
        {
            Canvas.SetLeft(this._marker, markerLeft + overlayOrigin.X - this._marker.Width / 2);
            Canvas.SetTop(this._marker, 0);
            this._marker.Visibility = Visibility.Visible;
        }
        else
        {
            this._marker.Visibility = Visibility.Collapsed;
        }
    }

    private void EndVisuals()
    {
        this.StopAutoScroll();
        this._ghost.Visibility = Visibility.Collapsed;
        this._marker.Visibility = Visibility.Collapsed;
        foreach (var (label, _, _) in this._headers.Values)
        {
            label.Opacity = 1;
        }
    }

    // Scrolls a list wider than its viewport while the pointer rests near either edge.
    private void UpdateAutoScroll(ScrollViewer scroll, double xInScroll)
    {
        var step = xInScroll < AutoScrollZone ? -AutoScrollMaxStep * (AutoScrollZone - xInScroll) / AutoScrollZone :
            xInScroll > scroll.ViewportWidth - AutoScrollZone ? AutoScrollMaxStep *
            (xInScroll - (scroll.ViewportWidth - AutoScrollZone)) / AutoScrollZone : 0;
        this._autoScrollStep = Math.Clamp(step, -AutoScrollMaxStep, AutoScrollMaxStep);
        if (this._autoScrollStep == 0 || scroll.ScrollableWidth <= 0)
        {
            this.StopAutoScroll();
            return;
        }

        if (this._autoScroll is null)
        {
            this._autoScroll = this.DispatcherQueue.CreateTimer();
            this._autoScroll.Interval = TimeSpan.FromMilliseconds(16);
            this._autoScroll.Tick += (_, _) => this.AutoScrollTick(scroll);
            this._autoScroll.Start();
        }
    }

    private void AutoScrollTick(ScrollViewer scroll)
    {
        if (this._drag is not { Started: true, Cancelled: false } drag)
        {
            this.StopAutoScroll();
            return;
        }

        var offset = Math.Clamp(scroll.HorizontalOffset + this._autoScrollStep, 0, scroll.ScrollableWidth);
        scroll.ChangeView(offset, null, null, true);

        // The header moved under a stationary pointer; re-evaluate the drop target.
        var point = scroll.TransformToVisual(this).TransformPoint(drag.LastInScroll);
        this.UpdateDrag(drag, point.X - (offset - scroll.HorizontalOffset), point.Y);
    }

    private void StopAutoScroll()
    {
        this._autoScroll?.Stop();
        this._autoScroll = null;
        this._autoScrollStep = 0;
    }

    private IEnumerable<(ListColumn Column, Button Label)> VisibleLabels()
    {
        foreach (var column in this._arrangement.Slots)
        {
            if (this.LabelFor(column) is { Visibility: Visibility.Visible } label)
            {
                yield return (column, label);
            }
        }
    }

    private static Brush? ThemeBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
    }

    private void FillMenu()
    {
        var owner = this._owner;
        this._menu.Items.Clear();
        if (owner is null)
        {
            return;
        }

        foreach (var column in owner.Columns)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = column.Header,
                IsChecked = owner.IsColumnVisible(column.Id),
                IsEnabled = owner.CanToggleColumn(column.Id)
            };
            AutomationProperties.SetAutomationId(item, "ToggleColumn" + column.Id);
            item.Click += (_, _) => owner.ToggleColumn(column.Id);
            this._menu.Items.Add(item);
        }

        if ((this._menu.Target as Button)?.Tag is ListColumn target)
        {
            this._menu.Items.Add(new MenuFlyoutSeparator());
            this.AddItem(
                "Move column left", "MoveColumnLeft", owner.CanMoveColumn(target.Id, -1),
                () => owner.MoveColumnBy(target.Id, -1));
            this.AddItem(
                "Move column right", "MoveColumnRight", owner.CanMoveColumn(target.Id, 1),
                () => owner.MoveColumnBy(target.Id, 1));
            this.AddItem("Size column to fit", "FitColumn", target.CanResize, () => owner.AutoFitColumn(target.Id));
        }

        this._menu.Items.Add(new MenuFlyoutSeparator());
        this.AddItem("Size all columns to fit", "FitAllColumns", true, owner.AutoFitAllColumns);
        this.AddItem("Reset columns", "ResetColumns", true, owner.ResetLayout);
    }

    private void AddItem(string text, string id, bool enabled, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        this._menu.Items.Add(item);
    }

    private sealed class DragState(ListColumn column, uint pointer, double startX)
    {
        public ListColumn Column { get; } = column;

        public uint Pointer { get; } = pointer;

        public double StartX { get; } = startX;

        public bool Started { get; set; }

        public bool Cancelled { get; set; }

        public double GrabOffset { get; set; }

        public bool Valid { get; set; }

        public string? BeforeId { get; set; }

        public Point LastInScroll { get; set; }
    }
}
