// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Models.Lists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace JPSoftworks.Regedit.Controls.Lists;

/// <summary>
///     A virtualized, multi-select list with declared columns that users can resize, auto-fit, reorder, hide and sort.
///     The control never sorts or persists anything itself: it raises <see cref="SortRequested" /> and
///     <see cref="LayoutChanged" /> and the host owns the data order and where <see cref="Layout" /> is saved.
/// </summary>
public sealed partial class ColumnListView : UserControl
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(object), typeof(ColumnListView),
        new PropertyMetadata(null, (owner, _) => ((ColumnListView)owner).LayoutPropertyChanged()));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(ColumnListView),
        new PropertyMetadata(null, (owner, args) => ((ColumnListView)owner).Items.ItemsSource = args.NewValue));

    public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register(
        nameof(SelectionMode), typeof(ListViewSelectionMode), typeof(ColumnListView),
        new PropertyMetadata(
            ListViewSelectionMode.Extended,
            (owner, args) => ((ColumnListView)owner).Items.SelectionMode = (ListViewSelectionMode)args.NewValue));

    public static readonly DependencyProperty SelectedItemsProperty = DependencyProperty.Register(
        nameof(SelectedItems), typeof(object), typeof(ColumnListView),
        new PropertyMetadata(null, (owner, args) => ((ColumnListView)owner).SelectedItemsChanged(args.OldValue)));

    public static readonly DependencyProperty SortColumnIdProperty = DependencyProperty.Register(
        nameof(SortColumnId), typeof(string), typeof(ColumnListView),
        new PropertyMetadata(null, (owner, _) => ((ColumnListView)owner).UpdateSort()));

    public static readonly DependencyProperty SortDescendingProperty = DependencyProperty.Register(
        nameof(SortDescending), typeof(bool), typeof(ColumnListView),
        new PropertyMetadata(false, (owner, _) => ((ColumnListView)owner).UpdateSort()));

    public static readonly DependencyProperty ShowColumnsProperty = DependencyProperty.Register(
        nameof(ShowColumns), typeof(bool), typeof(ColumnListView),
        new PropertyMetadata(true, (owner, _) => ((ColumnListView)owner).UpdatePresentation()));

    public static readonly DependencyProperty CompactItemTemplateProperty = DependencyProperty.Register(
        nameof(CompactItemTemplate), typeof(DataTemplate), typeof(ColumnListView),
        new PropertyMetadata(null, (owner, _) => ((ColumnListView)owner).UpdatePresentation()));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ColumnListView),
        new PropertyMetadata(8d, (owner, _) => ((ColumnListView)owner).Arrange()));

    private const double DefaultLeadingWidth = 24;
    private const double FitPadding = 12;

    // Selection changes larger than this are applied in bulk (ranges, ReplaceAll) instead of item by item.
    private const int BatchThreshold = 32;

    private readonly HashSet<ColumnRow> _rows = [];
    private readonly HashSet<ListColumn> _declared = [];
    private ColumnSpec[] _specs = [];
    private ColumnLayout _layout = new([]);
    private ColumnArrangement _arrangement = ColumnArrangement.Empty;
    private ListColumn? _leading;
    private INotifyCollectionChanged? _observedSelection;
    private int _columnsVersion;
    private bool _publishing;
    private bool _syncingSelection;
    private bool _chromeQueued;
    private bool _visibleQueued;

    // Horizontal space the list takes around each row (item margins, padding, scroll bar), refined from a real row.
    private double _rowInset = 40;

    public ColumnListView()
    {
        this.InitializeComponent();
        this.Columns.CollectionChanged += (_, _) => this.RebuildColumns();
        this.HorizontalScroll.SizeChanged += (_, _) => this.Arrange();
        this.Items.SelectionChanged += this.ItemsSelectionChanged;
        this.Items.GettingFocus += this.ItemsGettingFocus;
        // An empty list has no remembered row to restore when tabbing into it.
        this.Items.Items.VectorChanged += (_, _) => this.Items.TabFocusNavigation = this.Items.Items.Count == 0
            ? KeyboardNavigationMode.Local
            : KeyboardNavigationMode.Once;
        this.Items.ItemTemplate = this.RowTemplate;
        this.Items.RegisterPropertyChangedCallback(
            ListView.ItemContainerStyleProperty, (_, _) => this.InvalidateChrome());
        this.Loaded += (_, _) => this.InvalidateChrome();
        this.InitializeEditing();
        this.RebuildColumns();
    }

    /// <summary>Raised when a sortable header is clicked; the host re-sorts and sets <see cref="SortColumnId" />.</summary>
    public event EventHandler<string>? SortRequested;

    /// <summary>Raised after the user changes widths, order or visibility. Hosts may debounce saving during drags.</summary>
    public event EventHandler<ColumnLayout>? LayoutChanged;

    /// <summary>
    ///     Raised (coalesced, at low priority) when <see cref="VisibleItems" /> may have changed: rows realized, recycled,
    ///     rebound or scrolled. Use it to load details for on-screen items lazily.
    /// </summary>
    public event EventHandler? VisibleItemsChanged;

    public ObservableCollection<ListColumn> Columns { get; } = [];

    /// <summary>Optional fixed column before all others (for example an icon). It is not resizable or hideable.</summary>
    public ListColumn? LeadingColumn
    {
        get => this._leading;
        set
        {
            this._leading = value;
            this.RebuildColumns();
        }
    }

    /// <summary>The saved layout (a <see cref="ColumnLayout" />). Null or stale values are normalized to the columns.</summary>
    public object? Layout
    {
        get => this.GetValue(LayoutProperty);
        set => this.SetValue(LayoutProperty, value);
    }

    public object? ItemsSource
    {
        get => this.GetValue(ItemsSourceProperty);
        set => this.SetValue(ItemsSourceProperty, value);
    }

    public ListViewSelectionMode SelectionMode
    {
        get => (ListViewSelectionMode)this.GetValue(SelectionModeProperty);
        set => this.SetValue(SelectionModeProperty, value);
    }

    /// <summary>
    ///     A host-owned <see cref="IList" /> kept in sync with the selection in both directions. When it implements
    ///     <see cref="INotifyCollectionChanged" />, host changes select the matching rows.
    /// </summary>
    public object? SelectedItems
    {
        get => this.GetValue(SelectedItemsProperty);
        set => this.SetValue(SelectedItemsProperty, value);
    }

    public string? SortColumnId
    {
        get => (string?)this.GetValue(SortColumnIdProperty);
        set => this.SetValue(SortColumnIdProperty, value);
    }

    public bool SortDescending
    {
        get => (bool)this.GetValue(SortDescendingProperty);
        set => this.SetValue(SortDescendingProperty, value);
    }

    /// <summary>False hides the header and shows <see cref="CompactItemTemplate" /> rows, e.g. in a narrow pane.</summary>
    public bool ShowColumns
    {
        get => (bool)this.GetValue(ShowColumnsProperty);
        set => this.SetValue(ShowColumnsProperty, value);
    }

    public DataTemplate? CompactItemTemplate
    {
        get => (DataTemplate?)this.GetValue(CompactItemTemplateProperty);
        set => this.SetValue(CompactItemTemplateProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)this.GetValue(ColumnSpacingProperty);
        set => this.SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The inner list, for row context menus, density, scrolling helpers and item events.</summary>
    public ListView ListView => this.Items;

    public ColumnHeaderRow HeaderRow => this.Header;

    /// <summary>
    ///     Modifiers that, with Left/Right on a focused column header, move that column. Defaults to Ctrl+Shift;
    ///     <see cref="Windows.System.VirtualKeyModifiers.None" /> disables the shortcut. Avoid Alt: hosts commonly
    ///     use Alt+Left/Right for back and forward navigation.
    /// </summary>
    public Windows.System.VirtualKeyModifiers KeyboardReorderModifiers { get; set; }
        = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift;

    /// <summary>
    ///     The accessible name of each row (what Narrator reads for the item). Without it the list item falls back to
    ///     the item's <see cref="object.ToString" />.
    /// </summary>
    public Func<object?, string?>? ItemAutomationName
    {
        get;
        set
        {
            field = value;
            foreach (var row in this._rows)
            {
                row.UpdateAutomationName();
            }
        }
    }

    internal ScrollViewer HeaderScroll => this.HorizontalScroll;

    /// <summary>The normalized layout currently shown, even when <see cref="Layout" /> is unset.</summary>
    public ColumnLayout EffectiveLayout => this._layout;

    public IReadOnlyList<ColumnSpec> Specs => this._specs;

    /// <summary>
    ///     Items whose rows intersect the viewport, top to bottom. Excludes the realization buffer and recycled
    ///     containers the list keeps parked outside the viewport.
    /// </summary>
    public IReadOnlyList<object> VisibleItems
    {
        get
        {
            var visible = new List<(double Top, object Item)>();
            foreach (var row in this._rows)
            {
                if (row.DataContext is { } item && this.ViewportTop(row) is { } top)
                {
                    visible.Add((top, item));
                }
            }

            return visible.OrderBy(entry => entry.Top).Select(entry => entry.Item).ToArray();
        }
    }

    internal IReadOnlyCollection<ColumnRow> RealizedRows => this._rows;

    private DataTemplate RowTemplate => (DataTemplate)this.Resources["ColumnRowTemplate"];

    public ListColumn? FindColumn(string id)
    {
        return this.Columns.FirstOrDefault(column => column.Id == id);
    }

    /// <summary>The width the column is drawn at now, including widths computed for fill columns.</summary>
    public double CurrentWidth(string id)
    {
        var slot = this.FindColumn(id) is { } column ? this._arrangement.SlotOf(column) : -1;
        return slot >= 0 ? this._arrangement.Widths[slot] : this._layout.Find(id)?.Width ?? 0;
    }

    public bool IsColumnVisible(string id)
    {
        return this._layout.Find(id)?.Visible ?? false;
    }

    public bool CanToggleColumn(string id)
    {
        return this._layout.CanToggle(id, this._specs);
    }

    public bool CanMoveColumn(string id, int direction)
    {
        return this.IsColumnVisible(id) && this._layout.Neighbour(id, direction, this._specs) is not null;
    }

    public void ResizeColumn(string id, double width)
    {
        if (this._specs.FirstOrDefault(spec => spec.Id == id) is { } spec)
        {
            this.Commit(this._layout.Resize(spec, width));
        }
    }

    public void MoveColumn(string id, string targetId)
    {
        this.Commit(this._layout.Move(id, targetId, this._specs));
    }

    /// <summary>Drops a column into the gap before <paramref name="beforeId" />; null means after the last column.</summary>
    public void MoveColumnBefore(string id, string? beforeId)
    {
        this.Commit(this._layout.MoveBefore(id, beforeId, this._specs));
    }

    public bool CanMoveColumnBefore(string id, string? beforeId)
    {
        return !ReferenceEquals(this._layout.MoveBefore(id, beforeId, this._specs), this._layout);
    }

    public void MoveColumnBy(string id, int direction)
    {
        this.Commit(this._layout.MoveBy(id, direction, this._specs));
    }

    public void ToggleColumn(string id)
    {
        this.Commit(this._layout.Toggle(id, this._specs));
    }

    public void ResetLayout()
    {
        this.Commit(ColumnLayout.Default(this._specs));
    }

    /// <summary>Sizes a column to its header and the widest realized cell; off-screen rows are not measured.</summary>
    public void AutoFitColumn(string id)
    {
        if (this.FindColumn(id) is { } column && this.FitWidth(column) is { } width)
        {
            this.ResizeColumn(id, width);
        }
    }

    public void AutoFitAllColumns()
    {
        var layout = this._layout;
        foreach (var column in this._arrangement.Slots.Where(column => !ReferenceEquals(column, this._leading)))
        {
            if (this.FitWidth(column) is { } width)
            {
                layout = layout.Resize(column.ToSpec(), width);
            }
        }

        this.Commit(layout);
    }

    internal void RequestSort(string id)
    {
        this.SortRequested?.Invoke(this, id);
    }

    internal void Register(ColumnRow row)
    {
        if (!this._rows.Add(row))
        {
            return;
        }

        row.SetOwner(this);
        row.DataContextChanged += this.RowDataContextChanged;
        row.Apply(this._arrangement);
        row.SizeChanged += this.MeasureChrome;
        this.QueueChromeMeasure();

        this.QueueVisibleChanged();
    }

    internal void Unregister(ColumnRow row)
    {
        if (this._rows.Remove(row))
        {
            this.AbandonEditIf(edit => ReferenceEquals(edit.Row, row));
            row.DataContextChanged -= this.RowDataContextChanged;
            row.SizeChanged -= this.MeasureChrome;
            this.QueueVisibleChanged();
        }
    }

    private double? FitWidth(ListColumn column)
    {
        if (!column.CanResize || this._arrangement.SlotOf(column) < 0)
        {
            return null;
        }

        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var width = 0d;
        if (this.Header.LabelFor(column) is { } label)
        {
            label.Measure(unbounded);
            width = label.DesiredSize.Width;
            label.InvalidateMeasure();
        }

        foreach (var row in this._rows)
        {
            if (row.CellFor(column) is { Visibility: Visibility.Visible } cell)
            {
                cell.Measure(unbounded);
                width = Math.Max(width, cell.DesiredSize.Width + cell.Margin.Left + cell.Margin.Right);
                cell.InvalidateMeasure();
            }
        }

        return Math.Ceiling(width + FitPadding);
    }

    private void Commit(ColumnLayout next)
    {
        if (ReferenceEquals(next, this._layout) || next.IsEquivalentTo(this._layout))
        {
            return;
        }

        this._layout = next;
        this._publishing = true;
        try
        {
            this.Layout = next;
        }
        finally
        {
            this._publishing = false;
        }

        this.Arrange();
        this.LayoutChanged?.Invoke(this, next);
    }

    private void LayoutPropertyChanged()
    {
        if (this._publishing)
        {
            return;
        }

        this._layout = (this.Layout as ColumnLayout ?? ColumnLayout.Default(this._specs)).Normalize(this._specs);
        this.Arrange();
    }

    private void RebuildColumns()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in this.Columns)
        {
            if (string.IsNullOrEmpty(column.Id) || !ids.Add(column.Id))
            {
                throw new InvalidOperationException($"Column ids must be unique and non-empty: '{column.Id}'.");
            }
        }

        this._columnsVersion++;
        this._declared.Clear();
        this._declared.UnionWith(this.Columns);
        if (this._leading is not null)
        {
            this._declared.Add(this._leading);
        }

        this._specs = this.Columns.Select(column => column.ToSpec()).ToArray();
        this.Header.Rebuild(
            this, this.Columns, (Style)this.Resources["ColumnHeaderButtonStyle"],
            (Style)this.Resources["ColumnHeaderSortIconStyle"]);
        this._layout = (this.Layout as ColumnLayout ?? ColumnLayout.Default(this._specs)).Normalize(this._specs);
        this.Arrange();
    }

    private void Arrange()
    {
        if (this.Header is null)
        {
            return;
        }

        var viewport = this.HorizontalScroll.ActualWidth;
        if (!this.ShowColumns)
        {
            this.Surface.Width = viewport > 0 ? viewport : double.NaN;
            return;
        }

        var spacing = Math.Max(0, this.ColumnSpacing);
        var byId = this.Columns.ToDictionary(column => column.Id, StringComparer.Ordinal);
        var slots = new List<ListColumn>();
        var widths = new List<double>();
        if (this._leading is not null)
        {
            slots.Add(this._leading);
            widths.Add(this._leading.DefaultWidth > 0 ? this._leading.DefaultWidth : DefaultLeadingWidth);
        }

        var visible = this._layout.Visible.Where(column => byId.ContainsKey(column.Id)).ToArray();
        var slotCount = slots.Count + visible.Length;
        var fixedWidth = widths.Sum() + spacing * Math.Max(0, slotCount - 1);
        var computed = ColumnSizing.Compute(this._layout, this._specs, viewport - this._rowInset - fixedWidth);
        for (var index = 0; index < visible.Length; index++)
        {
            slots.Add(byId[visible[index].Id]);
            widths.Add(computed[index]);
        }

        this._arrangement = new ColumnArrangement(
            this._columnsVersion, this._declared.ToArray(), slots, widths, spacing);
        this.AbandonEditIf(edit => this._arrangement.SlotOf(edit.Column) < 0);
        this.Header.Apply(this._arrangement, this.SortColumnId, this.SortDescending);
        foreach (var row in this._rows)
        {
            row.Apply(this._arrangement);
        }

        this.Surface.Width = Math.Max(viewport, widths.Sum() + spacing * Math.Max(0, slotCount - 1) + this._rowInset);
    }

    // The row's top relative to the list when it intersects the viewport, otherwise null.
    private double? ViewportTop(ColumnRow row)
    {
        if (row.ActualHeight <= 0 || row.ActualWidth <= 0 || this.Items.ActualHeight <= 0)
        {
            return null;
        }

        Point origin;
        try
        {
            origin = row.TransformToVisual(this.Items).TransformPoint(default);
        }
        catch (ArgumentException)
        {
            // The row left the visual tree between recycling and its Unloaded event.
            return null;
        }

        var inside = origin.Y + row.ActualHeight > 0 && origin.Y < this.Items.ActualHeight &&
                     origin.X + row.ActualWidth > 0 && origin.X < this.Items.ActualWidth;
        return inside ? origin.Y : null;
    }

    private void UpdateSort()
    {
        this.Header.Apply(this._arrangement, this.SortColumnId, this.SortDescending);
    }

    private void UpdatePresentation()
    {
        var columns = this.ShowColumns;
        if (!columns)
        {
            this.CancelEdit();

            // Column rows leave with the old template; parked recycled containers may never raise Unloaded.
            foreach (var row in this._rows.ToArray())
            {
                this.Unregister(row);
            }
        }

        this.Header.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
        this.Items.ItemTemplate
            = columns || this.CompactItemTemplate is null ? this.RowTemplate : this.CompactItemTemplate;
        this.Arrange();
        this.InvalidateChrome();
    }

    internal void InvalidateChrome()
    {
        this.QueueChromeMeasure();
    }

    // Aligns the header with the first laid-out row and learns how much width the list chrome takes per row.
    private void MeasureChrome(object sender, SizeChangedEventArgs args)
    {
        this.QueueChromeMeasure();
    }

    private void QueueChromeMeasure()
    {
        if (this._chromeQueued || !this.ShowColumns)
        {
            return;
        }

        this._chromeQueued = this.DispatcherQueue.TryEnqueue(() =>
        {
            this._chromeQueued = false;
            var row = this._rows.FirstOrDefault(candidate => this.ViewportTop(candidate) is not null);
            if (row is null || this.Surface.ActualWidth <= 0 || !this.ShowColumns)
            {
                return;
            }

            double left;
            try
            {
                left = row.TransformToVisual(this.Surface).TransformPoint(default).X;
            }
            catch (ArgumentException)
            {
                return;
            }

            var inset = this.Surface.ActualWidth - row.ActualWidth;
            if (left < 0 || inset < 0)
            {
                return;
            }

            this.Header.Padding = new Thickness(left, 0, Math.Max(0, inset - left), 0);
            if (Math.Abs(inset - this._rowInset) > 0.5)
            {
                this._rowInset = inset;
                this.Arrange();
            }
        });
    }

    private void RowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        this.AbandonEditIf(edit => ReferenceEquals(edit.Row, sender) && !Equals(args.NewValue, edit.Item));
        this.QueueVisibleChanged();
    }

    private void QueueVisibleChanged()
    {
        if (this._visibleQueued || this.VisibleItemsChanged is null)
        {
            return;
        }

        this._visibleQueued = this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                this._visibleQueued = false;
                this.VisibleItemsChanged?.Invoke(this, EventArgs.Empty);
            });
    }

    private void SelectedItemsChanged(object? previous)
    {
        if (this._observedSelection is not null)
        {
            this._observedSelection.CollectionChanged -= this.BoundSelectionChanged;
        }

        this._observedSelection = this.SelectedItems as INotifyCollectionChanged;
        if (this._observedSelection is not null)
        {
            this._observedSelection.CollectionChanged += this.BoundSelectionChanged;
        }

        if (!ReferenceEquals(previous, this.SelectedItems))
        {
            this.PushSelection();
        }
    }

    private void BoundSelectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!this._syncingSelection)
        {
            this.PushSelection();
        }
    }

    private void PushSelection()
    {
        if (this.SelectedItems is IList source)
        {
            this.RestoreSelection(source.Cast<object>());
        }
    }

    internal void RestoreSelection(IEnumerable<object> source)
    {
        this._syncingSelection = true;
        try
        {
            var wanted = new HashSet<object>(source.Cast<object>());
            var current = this.Items.SelectedItems;
            var removals = current.Count(item => !wanted.Contains(item));
            var additions = wanted.Count - (current.Count - removals);
            if (removals + additions > BatchThreshold && this.SelectRanges(wanted))
            {
                return;
            }

            for (var index = current.Count - 1; index >= 0; index--)
            {
                if (!wanted.Remove(current[index]))
                {
                    current.RemoveAt(index);
                }
            }

            foreach (var item in source)
            {
                if (wanted.Remove(item))
                {
                    current.Add(item);
                }
            }
        }
        finally
        {
            this._syncingSelection = false;
        }
    }

    // Applies a large selection as a few index ranges instead of one SelectionChanged per item.
    private bool SelectRanges(HashSet<object> wanted)
    {
        if (this.Items.SelectionMode is not (ListViewSelectionMode.Extended or ListViewSelectionMode.Multiple))
        {
            return false;
        }

        var items = this.Items.Items;
        var count = items.Count;
        var matched = 0;
        var runs = new List<ItemIndexRange>();
        var runStart = -1;
        for (var index = 0; index <= count; index++)
        {
            var selected = index < count && wanted.Contains(items[index]);
            if (selected)
            {
                matched++;
                if (runStart < 0)
                {
                    runStart = index;
                }
            }
            else if (runStart >= 0)
            {
                runs.Add(new ItemIndexRange(runStart, (uint)(index - runStart)));
                runStart = -1;
            }
        }

        if (matched == count && count > 0)
        {
            this.Items.SelectAll();
            return true;
        }

        if (this.Items.SelectedItems.Count > 0 && count > 0)
        {
            this.Items.DeselectRange(new ItemIndexRange(0, (uint)count));
        }

        foreach (var run in runs)
        {
            this.Items.SelectRange(run);
        }

        return true;
    }

    private void ItemsGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.NewFocusedElement != this.Items || args.FocusState != FocusState.Keyboard ||
            this.Items.Items.Count == 0)
        {
            return;
        }

        // Redirect before focus is drawn, retaining the list itself as the empty-list tab stop.
        var item = this.Items.SelectedItem ?? this.Items.Items[0];
        this.Items.ScrollIntoView(item);
        this.Items.UpdateLayout();
        if (this.Items.ContainerFromItem(item) is ListViewItem container)
        {
            args.TrySetNewFocusedElement(container);
        }
        else if (args.TryCancel())
        {
            // Virtualized rows may need a layout pass; keep the old focus until the row exists.
            var previous = args.OldFocusedElement;

            void FocusRow()
            {
                if (this.Items.IsLoaded && this.Items.Items.Contains(item) && ReferenceEquals(
                        FocusManager.GetFocusedElement(this.XamlRoot), previous))
                {
                    this.Items.UpdateLayout();
                    if (this.Items.ContainerFromItem(item) is ListViewItem realized)
                    {
                        realized.Focus(FocusState.Keyboard);
                    }
                    else
                    {
                        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusRow);
                    }
                }
            }

            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusRow);
        }
    }

    private void ItemsSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (this._syncingSelection || this.SelectedItems is not IList { IsReadOnly: false, IsFixedSize: false } target)
        {
            return;
        }

        this._syncingSelection = true;
        try
        {
            if (args.AddedItems.Count + args.RemovedItems.Count > BatchThreshold)
            {
                // One notification for select all, range selection and large deselection.
                if (target is IRangeList range)
                {
                    range.ReplaceAll(this.Items.SelectedItems);
                    return;
                }

                target.Clear();
                foreach (var item in this.Items.SelectedItems)
                {
                    target.Add(item);
                }

                return;
            }

            foreach (var item in args.RemovedItems)
            {
                target.Remove(item);
            }

            foreach (var item in args.AddedItems)
            {
                if (!target.Contains(item))
                {
                    target.Add(item);
                }
            }
        }
        finally
        {
            this._syncingSelection = false;
        }
    }
}
