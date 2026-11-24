// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using JPSoftworks.Regedit.Controls.Lists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace JPSoftworks.ColumnList.Sample;

internal static partial class ComponentChecks
{
    private static async Task CheckSelectionBoxesAsync(MainPage page, Action<bool, string> check)
    {
        var tools = page.Tools;
        var list = tools.ListView;
        var source = tools.ItemsSource;
        var header = tools.SelectAllBox;
        var rowHeight = ((ListViewItem)list.ContainerFromIndex(0)).ActualHeight;
        list.SelectedItems.Clear();
        check(header.Visibility == Visibility.Collapsed, "Custom selection check boxes are opt-in");
        check(!page.Registry.ListView.IsMultiSelectCheckBoxEnabled, "A checkbox mode set in XAML hides native check boxes on first load");
        tools.SelectionCheckBoxes = SelectionCheckBoxMode.Overlay;
        await WaitAsync(() => tools.VisibleItems.Count > 1 && Aligned(tools));
        var first = FirstRow(tools);
        var box = first.Children.OfType<CheckBox>().Single();
        var originalTheme = page.RequestedTheme;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            page.RequestedTheme = theme;
            await WaitAsync(() => header.ActualTheme == theme && box.ActualTheme == theme && page.DragSelectionOption.ActualTheme == theme);
            var standard = page.DragSelectionOption;
            check(
                header.Template == standard.Template
                && box.Template == standard.Template
                && header.CornerRadius == standard.CornerRadius
                && box.CornerRadius == standard.CornerRadius,
                $"Header and row use the host's standard check box template and corner radius in {theme} theme");
        }

        page.RequestedTheme = originalTheme;
        check(
            Math.Abs(((ListViewItem)list.ContainerFromIndex(0)).ActualHeight - rowHeight) < 1,
            "Selection check boxes preserve the host's row density");
        check(
            box.Opacity == 1 && Grid.GetColumn(first.CellFor(tools.Columns[0])!) == 1,
            "Without a leading cell, the check box gets its own aligned column");
        Invoke(box);
        await WaitAsync(() => page.ToolSelection.Contains(first.DataContext));
        check(header.IsChecked is null, "Toggling a row through automation sets the header to mixed");
        Invoke(header);
        await WaitAsync(() => page.ToolSelection.Count == list.Items.Count && header.IsChecked == true);
        Invoke(header);
        await WaitAsync(() => page.ToolSelection.Count == 0 && header.IsChecked == false);
        check(true, "The mixed header selects all rows and the checked header clears the bound selection");

        list.SelectionMode = ListViewSelectionMode.Single;
        check(
            header.Visibility == Visibility.Collapsed && box.Visibility == Visibility.Collapsed,
            "Changing the native selection mode to Single hides custom selection controls");
        var single = list.Items[1];
        var expected = new[] { single };
        check(
            tools.PrepareItemDrag(single).SequenceEqual(expected) && Equals(list.SelectedItem, single),
            "A drag in single-selection mode selects its source");
        list.SelectionMode = ListViewSelectionMode.None;
        check(
            tools.PrepareItemDrag(single).SequenceEqual(expected)
            && list.SelectedIndex == -1
            && tools.PrepareItemDrag(new object()).Count == 0, "Drag payloads also support selection-free lists and ignore removed items");
        list.SelectionMode = ListViewSelectionMode.Multiple;
        check(
            header.Visibility == Visibility.Visible && !list.IsMultiSelectCheckBoxEnabled,
            "Multiple selection shows one set of check boxes");
        tools.ShowColumns = false;
        await WaitAsync(() => list.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: Grid grid }
                              && grid.Children.OfType<Border>().SingleOrDefault()?.Child is StackPanel);
        var container = (ListViewItem)list.ContainerFromIndex(0);
        var compact = (Grid)container.ContentTemplateRoot;
        var compactBox = compact.Children.OfType<CheckBox>().Single();
        var content = compact.Children.OfType<Border>().Single().Child;
        var rowPeer = FrameworkElementAutomationPeer.CreatePeerForElement(container);
        var boxPeer = FrameworkElementAutomationPeer.CreatePeerForElement(compactBox);
        var name = ((SampleEvent)list.Items[0]).Path;
        await WaitAsync(
            () => rowPeer.GetName() == name && boxPeer.GetName() == "Select " + name,
            () =>
                $"Expected '{name}', compact content '{AutomationProperties.GetName(content)}', row '{rowPeer.GetName()}', checkbox '{boxPeer.GetName()}'.");
        check(true, "Compact checkboxes retain the host template's accessible item name");
        AutomationProperties.SetName(content, string.Empty);
        tools.ItemAutomationName = item => (item as SampleEvent)?.Summary;
        name = ((SampleEvent)list.Items[0]).Summary;
        check(
            rowPeer.GetName() == name && boxPeer.GetName() == "Select " + name,
            "Compact rows and their checkboxes use the host's name resolver when the template supplies no name");
        tools.ItemAutomationName = null;
        Invoke(compactBox);
        await WaitAsync(() => page.ToolSelection.Contains(list.Items[0]));
        check(tools.HeaderRow.Visibility == Visibility.Collapsed, "A compact host template keeps its content and selectable check box");
        tools.ShowColumns = true;
        tools.ItemsSource = Array.Empty<object>();
        await WaitAsync(() => !header.IsEnabled && header.IsChecked == false);
        check(true, "An empty or replaced source clears and disables the header");
        tools.ItemsSource = source;
        tools.SelectionCheckBoxes = SelectionCheckBoxMode.Default;
        check(list.IsMultiSelectCheckBoxEnabled, "Removing the custom presentation restores the host's native check boxes");
        var nativeBoxes = ListViewBase.IsMultiSelectCheckBoxEnabledProperty;
        check(
            list.ReadLocalValue(nativeBoxes) == DependencyProperty.UnsetValue,
            "Returning to the native check boxes leaves the host's style in control");
        var setting = new ToggleSwitch { IsOn = true };
        list.SetBinding(
            nativeBoxes, new Binding { Source = setting, Path = new PropertyPath(nameof(ToggleSwitch.IsOn)), Mode = BindingMode.OneWay });
        tools.SelectionCheckBoxes = SelectionCheckBoxMode.Inline;
        setting.IsOn = false;
        setting.IsOn = true;
        await WaitAsync(
            () => !list.IsMultiSelectCheckBoxEnabled,
            () => $"Native check boxes are still visible in {tools.SelectionCheckBoxes} with {list.SelectionMode} selection.");
        check(true, "Custom check boxes stay active while the host's binding changes");
        setting.IsOn = false;
        tools.SelectionCheckBoxes = SelectionCheckBoxMode.Default;
        await WaitAsync(() => !list.IsMultiSelectCheckBoxEnabled);
        check(list.GetBindingExpression(nativeBoxes) is not null, "The native check boxes pick up the host's latest bound value");
        setting.IsOn = true;
        await WaitAsync(() => list.IsMultiSelectCheckBoxEnabled);
        check(true, "Host binding updates still reach the native check boxes after switching back");
        list.ClearValue(nativeBoxes);
        list.SelectionMode = ListViewSelectionMode.Extended;

        var registry = page.Registry;
        var layout = registry.EffectiveLayout;
        registry.SelectionCheckBoxes = SelectionCheckBoxMode.Inline;
        await WaitAsync(() => registry.VisibleItems.Count > 0 && Aligned(registry));
        var row = FirstRow(registry);
        check(
            Grid.GetColumn(row.Children.OfType<CheckBox>().Single()) == 0 && Grid.GetColumn(row.CellFor(registry.LeadingColumn!)!) == 1,
            "Inline selection places the check box before the host's leading cell");
        registry.SelectionCheckBoxes = SelectionCheckBoxMode.Overlay;
        check(
            Grid.GetColumn(row.CellFor(registry.LeadingColumn!)!) == 0 && registry.EffectiveLayout.IsEquivalentTo(layout),
            "Changing check box placement leaves the saved data columns intact");
        var leading = (Border)row.CellFor(registry.LeadingColumn!)!;
        var image = (FrameworkElement)leading.Child;
        image.Height = 76;
        registry.UpdateLayout();
        var tallRow = (ListViewItem)registry.ListView.ContainerFromItem(row.DataContext);
        var tallHeight = tallRow.ActualHeight;
        registry.ListView.SelectedItem = row.DataContext;
        await WaitAsync(() => leading.Opacity == 0);
        registry.UpdateLayout();
        check(
            tallHeight >= 76 && tallRow.ActualHeight == tallHeight && !leading.IsHitTestVisible,
            "An overlay checkbox preserves the height of a tall leading visual");
        image.ClearValue(FrameworkElement.HeightProperty);
        registry.ListView.SelectedItems.Clear();
    }

    private static async Task CheckDragSelectionAsync(MainPage page, Window window, Action<bool, string> check)
    {
        var control = page.Registry;
        var list = control.ListView;
        list.ScrollIntoView(list.Items[0], ScrollIntoViewAlignment.Leading);
        list.SelectedItems.Clear();
        await WaitAsync(() => control.VisibleItems.Contains(list.Items[0]));
        var rectangle = (FrameworkElement)control.FindName("SelectionLayer");

        (int X, int Y) Row(int index)
        {
            var name = Cell(control, "Name", index);
            var row = (ColumnRow)name.Parent;
            var slotWidth = row.ColumnDefinitions[Grid.GetColumn(name)].ActualWidth;
            return MouseInput.ToScreen(window, name, new Point(slotWidth - 2, name.ActualHeight / 2));
        }

        (int X, int Y) Label(int index)
        {
            var name = Cell(control, "Name", index);
            return MouseInput.ToScreen(window, name, new Point(name.ActualWidth / 2, name.ActualHeight / 2));
        }

        async Task StartDrag((int X, int Y) start, (int X, int Y) end)
        {
            await MouseInput.MoveAsync(start);
            await MouseInput.DownAsync();
            await MouseInput.GlideAsync(start, end, 5);
        }

        static async Task Click((int X, int Y) point)
        {
            await MouseInput.MoveAsync(point);
            await MouseInput.DownAsync();
            await MouseInput.UpAsync();
        }

        async Task ShiftDrag((int X, int Y) start, (int X, int Y) end)
        {
            MouseInput.SetKey(0x10, true);
            try
            {
                await StartDrag(start, end);
                await MouseInput.UpAsync();
            }
            finally
            {
                MouseInput.SetKey(0x10, false);
            }
        }

        bool Selected(params int[] indices) =>
            page.RegistrySelection.Count == indices.Length && indices.All(i => page.RegistrySelection.Contains(list.Items[i]));

        var first = (ColumnRow)((ListViewItem)list.ContainerFromIndex(1)).ContentTemplateRoot;
        var box = first.Children.OfType<CheckBox>().Single();
        var hover = Row(1);
        var near = Row(3);
        await MouseInput.MoveAsync(near);
        await MouseInput.GlideAsync(near, hover, 5);
        await WaitAsync(() => box.Opacity == 1 && first.CellFor(control.LeadingColumn!)!.Opacity == 0);
        check(true, "Hovering a row replaces its leading cell with the check box");
        var checkPoint = MouseInput.ToScreen(window, box, new Point(box.ActualWidth / 2, box.ActualHeight / 2));
        await Click(checkPoint);
        await MouseInput.MoveAsync(Row(3));
        check(
            Selected(1) && box.IsChecked == true && box.Opacity == 1,
            "Clicking the overlay selects the item and keeps its check visible after the mouse leaves");

        var across = (hover.X + 16, hover.Y);
        await StartDrag(hover, across);
        await MouseInput.GlideAsync(across, Row(3));
        await MouseInput.UpAsync();
        await StartDrag(Row(5), Row(7));
        await MouseInput.UpAsync();
        check(
            Selected(5, 6, 7)
            && box.IsChecked == false
            && box.Opacity == 0
            && first.CellFor(control.LeadingColumn!) is { Opacity: 1, IsHitTestVisible: true },
            "Replacing a rectangle restores the leading icon on the row where the first drag started");

        var outside = MouseInput.ToScreen(window, page, new Point(40, 4));
        await StartDrag(Row(3), Row(5));
        await MouseInput.GlideAsync(Row(5), outside);
        await MouseInput.UpAsync();
        check(
            rectangle.Visibility == Visibility.Collapsed && Selected(0, 1, 2, 3),
            "Releasing above the list commits the selection and dismisses the rectangle");

        await Click(Row(6));
        await MouseInput.MoveAsync(hover);
        await Click(checkPoint);
        var entries = (ObservableCollection<SampleEntry>)control.ItemsSource!;
        entries.Add(SampleData.Entries(1)[0]);
        await ShiftDrag(Row(2), Row(3));
        check(Selected(1, 2, 3, 6), "Appending an item preserves the checkbox Shift anchor and disjoint selection");

        await Click(Row(1));
        list.SelectedItem = list.Items[4];
        await ShiftDrag(Row(5), Row(6));
        check(Selected(4, 5, 6), "Replacing SelectedItem moves the anchor for a Shift rectangle");

        await StartDrag(Row(1), Row(3));
        entries.Add(SampleData.Entries(1)[0]);
        await MouseInput.GlideAsync(Row(3), Row(6), 5);
        check(
            rectangle.Visibility == Visibility.Visible && Selected(1, 2, 3, 4, 5, 6),
            "Appending an item while the mouse is held keeps the rectangle selecting");
        await MouseInput.UpAsync();
        entries.RemoveAt(entries.Count - 1);
        entries.RemoveAt(entries.Count - 1);

        var narrow = (ListViewItem)list.ContainerFromIndex(3);
        narrow.HorizontalAlignment = HorizontalAlignment.Left;
        narrow.Width = 76;
        list.UpdateLayout();
        var start = Row(1);
        var besideNarrow = (start.X, MouseInput.ToScreen(window, narrow, new Point(0, narrow.ActualHeight / 2)).Y);
        await StartDrag(start, besideNarrow);
        check(Selected(1, 2), "Ending beside a narrow row keeps intersected wider rows selected");
        await MouseInput.GlideAsync(besideNarrow, (start.X, Row(4).Y), 5);
        await MouseInput.UpAsync();
        check(Selected(1, 2, 4), "A rectangle crossing two wide rows skips a narrow row outside its bounds");

        var bottom = MouseInput.ToScreen(window, list, new Point(32, list.ActualHeight - 2));
        await StartDrag(start, (start.X, bottom.Y));
        await WaitAsync(() =>
            list.ContainerFromIndex(3) is null
            && control.VisibleItems.FirstOrDefault() is { } firstVisible
            && list.Items.IndexOf(firstVisible) > 4);
        check(
            !list.SelectedItems.Contains(list.Items[3]) && list.SelectedItems.Contains(list.Items[4]),
            "Auto-scrolling retains the horizontal bounds of a row that has been virtualized");
        var left = MouseInput.ToScreen(window, list, new Point(32, list.ActualHeight - 48));
        await MouseInput.GlideAsync((start.X, bottom.Y), left, 5);
        await MouseInput.UpAsync();
        check(list.SelectedItems.Contains(list.Items[3]), "Widening the rectangle includes a narrow row outside the viewport");
        narrow.ClearValue(FrameworkElement.WidthProperty);
        narrow.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        list.ScrollIntoView(list.Items[0], ScrollIntoViewAlignment.Leading);
        await WaitAsync(() => control.VisibleItems.Contains(list.Items[0]));
        list.UpdateLayout();

        await StartDrag(Row(2), Label(5));
        await WaitAsync(
            () => rectangle.Visibility == Visibility.Visible && Selected(2, 3, 4, 5),
            () =>
                $"Drag from {Row(2)} to {Label(5)}. Rectangle {rectangle.Visibility}, selected {string.Join(", ", list.SelectedItems.Select(list.Items.IndexOf))}.");
        check(true, "A drag from beside the name stays a rectangle when it crosses a draggable name");
        await MouseInput.UpAsync();
        check(rectangle.Visibility == Visibility.Collapsed && Selected(2, 3, 4, 5), "Releasing the mouse commits the selection once");

        await StartDrag(Row(3), Row(4));
        MouseInput.SetKey(0x11, true);
        try
        {
            await WaitAsync(() => Selected(2, 5));
            MouseInput.SetKey(0x11, false);
            await WaitAsync(() => Selected(3, 4));
            await MouseInput.PressKeyAsync(0x1B);
            await MouseInput.UpAsync();
            check(Selected(2, 3, 4, 5), "Pressing or releasing Ctrl updates a stationary rectangle against its original selection");
        }
        finally
        {
            MouseInput.SetKey(0x11, false);
        }

        MouseInput.SetKey(0x11, true);
        try
        {
            await StartDrag(Row(4), Row(6));
            check(Selected(2, 3, 6), "Ctrl-drag toggles intersected rows against the selection at mouse down");
            await MouseInput.GlideAsync(Row(6), Row(5), 3);
            await MouseInput.UpAsync();
            check(Selected(2, 3), "Retracting a Ctrl rectangle restores rows that leave it");
        }
        finally
        {
            MouseInput.SetKey(0x11, false);
        }

        await Click(Label(1));
        await WaitAsync(
            () => Selected(1),
            () =>
                $"Clicking the name should select row 1. Selected rows: {string.Join(", ", list.SelectedItems.Select(list.Items.IndexOf))}.");
        MouseInput.SetKey(0x10, true);
        try
        {
            await StartDrag(Row(3), Row(5));
            check(Selected(1, 2, 3, 4, 5), "Clicking a draggable name sets the anchor for a subsequent Shift rectangle");
            await MouseInput.PressKeyAsync(0x1B);
            await MouseInput.UpAsync();
            check(
                Selected(1) && rectangle.Visibility == Visibility.Collapsed,
                "Escape restores the original selection and removes the rectangle");
        }
        finally
        {
            MouseInput.SetKey(0x10, false);
        }

        list.SelectedItems.Add(list.Items[7]);
        MouseInput.SetKey(0x11, true);
        MouseInput.SetKey(0x10, true);
        try
        {
            await StartDrag(Row(3), Row(4));
            await MouseInput.UpAsync();
            check(Selected(1, 2, 3, 4, 7), "Ctrl+Shift extends from the anchor and retains disjoint selection");
        }
        finally
        {
            MouseInput.SetKey(0x10, false);
            MouseInput.SetKey(0x11, false);
        }

        var selectedNames = page.RegistrySelection.Cast<SampleEntry>().Select(entry => entry.Name).ToHashSet();
        await DropNameAsync(3);
        check(
            selectedNames.SetEquals(page.DroppedNames.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            && Selected(1, 2, 3, 4, 7), "Dragging a selected name to the Inspector copies the whole selection");
        control.IsDragSelectionEnabled = false;
        await DropNameAsync(6);
        control.IsDragSelectionEnabled = true;
        check(
            page.DroppedNames.Text == ((SampleEntry)list.Items[6]).Name && Selected(6),
            "Dragging an unselected name copies only that item and selects it");

        await Task.Delay(ColumnListView.SlowClickDelay);
        await Click(Label(6));
        await WaitAsync(() => control.IsEditing, () => "A slow click on the selected name did not start renaming.");
        await MouseInput.PressKeyAsync(0x1B);
        check(!control.IsEditing && Selected(6), "A slow click on a drag handle still edits the selected item");

        MouseInput.SetKey(0x11, true);
        try
        {
            await Click(Label(3));
            check(Selected(3, 6), "Ctrl-clicking a drag handle keeps native multi-selection");
        }
        finally
        {
            MouseInput.SetKey(0x11, false);
        }

        var grip = new Button { Content = "Drag", Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var gripRow = (ColumnRow)((ListViewItem)list.ContainerFromIndex(2)).ContentTemplateRoot;
        Grid.SetColumn(grip, Grid.GetColumn(Cell(control, "Type", 2)));
        gripRow.Children.Add(grip);
        var clicks = 0;
        var drags = 0;
        grip.Click += (_, _) => clicks++;
        grip.DragStarting += (_, args) =>
        {
            drags++;
            args.Cancel = true;
        };
        await WaitAsync(() => grip.ActualWidth > 0);
        var gripStart = MouseInput.ToScreen(window, grip, new Point(12, grip.ActualHeight / 2));
        var gripEnd = MouseInput.ToScreen(window, grip, new Point(48, grip.ActualHeight / 2));
        ColumnListView.SetIsDragHandle(grip, true);
        await Click(gripStart);
        await StartDrag(gripStart, gripEnd);
        await MouseInput.UpAsync();
        check(
            clicks == 1 && drags == 1 && rectangle.Visibility == Visibility.Collapsed,
            "A marked button clicks on release and starts only an item drag past the threshold");
        ColumnListView.SetIsDragHandle(grip, false);
        await StartDrag(gripStart, gripEnd);
        await MouseInput.UpAsync();
        check(
            clicks == 2 && drags == 1 && rectangle.Visibility == Visibility.Collapsed,
            "An unmarked button keeps its normal pointer behavior");
        gripRow.Children.Remove(grip);

        var edge = MouseInput.ToScreen(window, list, new Point(120, list.ActualHeight - 2));
        var initiallyVisible = list.Items.IndexOf(control.VisibleItems[^1]);
        await StartDrag(Row(2), edge);
        await WaitAsync(() => list.SelectedRanges.Count == 1 && list.SelectedRanges[0].LastIndex > initiallyVisible + 8);
        await MouseInput.UpAsync();
        check(
            list.SelectedRanges[0].FirstIndex == 2 && page.RegistrySelection.Count == list.SelectedRanges[0].Length,
            "A stationary pointer scrolls and selects virtualized rows while preserving the starting index");
        list.ScrollIntoView(list.Items[0], ScrollIntoViewAlignment.Leading);
        await WaitAsync(() => control.VisibleItems.Contains(list.Items[0]));

        control.ResizeColumn("Type", 1100);
        await WaitAsync(() => control.HeaderScroll.ScrollableWidth > 100);
        var right = MouseInput.ToScreen(window, list, new Point(control.HeaderScroll.ViewportWidth - 1, 100));
        await StartDrag(Row(2), right);
        await WaitAsync(() => control.HeaderScroll.HorizontalOffset > 80);
        await MouseInput.UpAsync();
        check(rectangle.Visibility == Visibility.Collapsed, "Dragging at the horizontal viewport edge scrolls wide columns");
        control.ResetLayout();
        control.HeaderScroll.ChangeView(0, null, null, true);
        await WaitAsync(() => control.HeaderScroll.HorizontalOffset == 0);

        var tools = page.Tools;
        var toolSource = tools.ItemsSource;
        tools.ItemsSource = tools.ListView.Items.Take(3).ToArray();
        try
        {
            await WaitAsync(() => tools.VisibleItems.Count == 3);
            var toolList = tools.ListView;
            var blank = MouseInput.ToScreen(window, toolList, new Point(110, toolList.ActualHeight - 12));
            var toolItem = (ListViewItem)toolList.ContainerFromIndex(1);
            var second = MouseInput.ToScreen(window, toolItem, new Point(85, toolItem.ActualHeight / 2));
            var topItem = (ListViewItem)toolList.ContainerFromIndex(0);
            var top = MouseInput.ToScreen(window, topItem, new Point(85, topItem.ActualHeight / 2));
            await Click(top);
            await Click(blank);
            await ShiftDrag(second, top);
            check(
                page.ToolSelection.ToHashSet().SetEquals(toolList.Items.Take(2)),
                "Clicking below the last item preserves the anchor for the next Shift rectangle");

            toolList.CanDragItems = true;
            await StartDrag(blank, second);
            await MouseInput.UpAsync();
            check(
                page.ToolSelection.ToHashSet().SetEquals(toolList.Items.Skip(1)),
                "Dragging up from empty space selects rows even when the host enables item drags");
            tools.IsDragSelectionEnabled = false;
            await StartDrag(blank, second);
            check(
                ((FrameworkElement)tools.FindName("SelectionLayer")).Visibility == Visibility.Collapsed,
                "A host can turn off rectangle selection");
            await MouseInput.UpAsync();
        }
        finally
        {
            tools.IsDragSelectionEnabled = true;
            tools.ListView.CanDragItems = false;
            tools.ItemsSource = toolSource;
        }

        var started = false;
        var over = false;
        var dropped = false;
        var target = tools.ListView;
        list.CanDragItems = target.AllowDrop = true;
        list.DragItemsStarting += DragStarting;
        target.DragOver += DragOver;
        target.Drop += Dropped;
        try
        {
            list.SelectedItem = list.Items[1];
            await StartDrag(Row(1), Row(4));
            var destination = MouseInput.ToScreen(window, target, new Point(120, 40));
            await MouseInput.GlideAsync(Row(4), destination);
            for (var move = 0; !over && move < 8; move++)
            {
                var next = (destination.X + (move % 2 == 0 ? 16 : -16), destination.Y);
                await MouseInput.GlideAsync(destination, next, 4);
                destination = next;
            }

            await WaitAsync(
                () => over, () => $"Host row drag started {started}, received drag-over {over}, rectangle {rectangle.Visibility}.");
            await MouseInput.UpAsync();
            await WaitAsync(() => dropped, () => "The native drop target did not receive the host payload.");
            check(
                rectangle.Visibility == Visibility.Collapsed, "A host can drag and drop its own item payload without starting a rectangle");
        }
        finally
        {
            list.CanDragItems = target.AllowDrop = false;
            list.DragItemsStarting -= DragStarting;
            target.DragOver -= DragOver;
            target.Drop -= Dropped;
        }

        async Task DropNameAsync(int index)
        {
            page.DroppedNames.Text = string.Empty;
            var name = Cell(control, "Name", index);
            var labelDragStarted = false;
            var accepted = false;
            void Starting(UIElement sender, DragStartingEventArgs args) => labelDragStarted = true;
            DragEventHandler over = (_, args) => accepted = args.AcceptedOperation == DataPackageOperation.Copy;
            name.DragStarting += Starting;
            page.DropTarget.AddHandler(UIElement.DragOverEvent, over, true);
            try
            {
                var beside = Row(index);
                await StartDrag(Label(index), beside);
                var targetPoint = MouseInput.ToScreen(window, page.DropTarget, new Point(34, page.DropTarget.ActualHeight - 12));
                await MouseInput.GlideAsync(beside, targetPoint);
                for (var attempt = 0; !accepted && attempt < 8; attempt++)
                {
                    var next = (targetPoint.X + (attempt % 2 == 0 ? 16 : -16), targetPoint.Y);
                    await MouseInput.GlideAsync(targetPoint, next, 4);
                    targetPoint = next;
                }
            }
            finally
            {
                await MouseInput.UpAsync();
                name.DragStarting -= Starting;
                page.DropTarget.RemoveHandler(UIElement.DragOverEvent, over);
            }

            await WaitAsync(
                () => page.DroppedNames.Text.Length > 0,
                () =>
                    $"No name received from row {index}. DragStarting {labelDragStarted}, target accepted {accepted}, rectangle {rectangle.Visibility}, selection {page.RegistrySelection.Count}.");
            check(
                rectangle.Visibility == Visibility.Collapsed && !control.IsEditing,
                $"Dragging the name of row {index} across its background keeps the item drag");
        }

        void DragStarting(object sender, DragItemsStartingEventArgs args)
        {
            started = true;
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetText("Column list sample");
        }

        void DragOver(object sender, DragEventArgs args)
        {
            over = true;
            args.AcceptedOperation = DataPackageOperation.Copy;
            args.Handled = true;
        }

        async void Dropped(object sender, DragEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Handled = true;
                args.AcceptedOperation = DataPackageOperation.Copy;
                dropped = args.DataView.Contains(StandardDataFormats.Text) && await args.DataView.GetTextAsync() == "Column list sample";
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
