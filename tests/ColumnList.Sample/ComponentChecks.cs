// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Text.Json;
using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Models.Lists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace JPSoftworks.ColumnList.Sample;

internal static class ComponentChecks
{
    internal static async Task RunAsync(MainPage page, Window window, string output)
    {
        var passed = new List<string>();
        object? largeList = null;
        string? failure = null;
        var registry = page.Registry;
        var tools = page.Tools;

        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }

            passed.Add(name);
        }

        try
        {
            await WaitAsync(() => registry.RealizedRows.Count > 5 && tools.RealizedRows.Count > 5);
            await Task.Delay(150);

            var liveEntry = page.Entries[0];
            var liveRow = registry.RealizedRows.Single(row => ReferenceEquals(row.DataContext, liveEntry));
            var originalName = liveEntry.Name;
            var originalAutomationName = registry.ItemAutomationName;
            registry.ItemAutomationName = item => (item as SampleEntry)?.Name;
            registry.ListView.SelectedItem = liveEntry;
            var selectionChanges = 0;

            void SelectionChanged(object sender, SelectionChangedEventArgs args)
            {
                selectionChanges++;
            }

            registry.ListView.SelectionChanged += SelectionChanged;
            liveEntry.Name = "Updated in place";
            Check(
                AutomationProperties.GetName(liveRow) == liveEntry.Name && selectionChanges == 0 &&
                ReferenceEquals(registry.ListView.SelectedItem, liveEntry) &&
                registry.RealizedRows.Any(row => ReferenceEquals(row, liveRow)),
                "An in-place row update changes its accessible name without replacing the row or changing selection");
            var liveSource = (System.Collections.ObjectModel.ObservableCollection<SampleEntry>)registry.ItemsSource!;
            liveSource.Move(0, 3);
            registry.RestoreSelection((object[])[liveEntry]);
            Check(
                ReferenceEquals(registry.ListView.SelectedItem, liveEntry),
                "Selection restoration retains an updated row after its sort position changes");
            liveSource.Move(3, 0);
            liveEntry.Name = originalName;
            registry.ListView.SelectionChanged -= SelectionChanged;
            registry.ItemAutomationName = originalAutomationName;

            // Keyboard focus must reach a row without ever landing on the list container.
            var focusedList = false;

            void ListGotFocus(object sender, RoutedEventArgs args)
            {
                focusedList |= ReferenceEquals(args.OriginalSource, registry.ListView);
            }

            registry.ListView.GotFocus += ListGotFocus;
            var focusHeader = registry.HeaderRow.LabelFor(registry.FindColumn("Name")!)!;
            focusHeader.Focus(FocusState.Keyboard);
            registry.ListView.SelectedItem = null;
            registry.ListView.Focus(FocusState.Keyboard);
            await Task.Delay(50);
            Check(
                !focusedList && FocusManager.GetFocusedElement(page.XamlRoot) is ListViewItem initialRow &&
                ReferenceEquals(initialRow.Content, page.Entries[0]),
                "Keyboard focus enters the first row without focusing the list container");
            Check(
                registry.ListView.SelectedItems.Count == 0,
                "Entering an unselected extended list preserves its selection");
            var focusedRow = (ListViewItem)FocusManager.GetFocusedElement(page.XamlRoot);
            Check(
                focusedRow.FocusVisualPrimaryThickness == new Thickness(1) &&
                focusedRow.FocusVisualSecondaryThickness == new Thickness(0),
                "List rows have one logical pixel of focus border");
            Check(
                (await FocusManager.TryMoveFocusAsync(
                    FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = page })).Succeeded &&
                FocusManager.GetFocusedElement(page.XamlRoot) is not ListViewItem &&
                !ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), registry.ListView),
                "Tab leaves the populated list without an extra container stop");
            Check(
                (await FocusManager.TryMoveFocusAsync(
                    FocusNavigationDirection.Previous, new FindNextElementOptions { SearchRoot = page })).Succeeded &&
                FocusManager.GetFocusedElement(page.XamlRoot) is ListViewItem,
                "Shift+Tab returns to a populated list row");

            focusHeader.Focus(FocusState.Keyboard);
            registry.ListView.SelectedItem = page.Entries[^1];
            focusedList = false;
            registry.ListView.Focus(FocusState.Keyboard);
            await WaitAsync(
                () => FocusManager.GetFocusedElement(page.XamlRoot) is ListViewItem row && ReferenceEquals(
                    registry.ListView.ItemFromContainer(row), page.Entries[^1]),
                () => $"focus={FocusManager.GetFocusedElement(page.XamlRoot)} " +
                      $"index={registry.ListView.IndexFromContainer(FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject)} " +
                      $"selection={registry.ListView.SelectedIndex} listFocused={focusedList}");
            Check(
                !focusedList && FocusManager.GetFocusedElement(page.XamlRoot) is ListViewItem selected &&
                ReferenceEquals(registry.ListView.ItemFromContainer(selected), page.Entries[^1]),
                "Keyboard entry realizes and focuses an off-screen selected row without a container flash");

            registry.ListView.GotFocus -= ListGotFocus;
            focusHeader.Focus(FocusState.Keyboard);
            var originalSource = registry.ItemsSource;
            registry.ItemsSource = Array.Empty<SampleEntry>();
            registry.UpdateLayout();
            Check(
                registry.ListView.Focus(FocusState.Keyboard) && ReferenceEquals(
                    FocusManager.GetFocusedElement(page.XamlRoot), registry.ListView) && registry.ListView.IsTabStop,
                "An empty list remains keyboard focusable");
            Check(
                (await FocusManager.TryMoveFocusAsync(
                    FocusNavigationDirection.Previous, new FindNextElementOptions { SearchRoot = page })).Succeeded,
                "Shift+Tab can leave the empty list for its column headers");
            Check(
                (await FocusManager.TryMoveFocusAsync(
                    FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = page })).Succeeded &&
                ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), registry.ListView),
                "Tab reaches the empty list after its column headers");
            focusHeader.Focus(FocusState.Programmatic);
            registry.ItemsSource = originalSource;
            registry.ListView.ScrollIntoView(page.Entries[0]);
            await WaitAsync(() => registry.VisibleItems.Contains(page.Entries[0]));

            // Alignment and sizing.
            Check(
                registry.HeaderRow.LabelFor(registry.FindColumn("Size")!)!.Visibility == Visibility.Collapsed &&
                registry.EffectiveLayout.Columns.Count == 5,
                "Columns declared in XAML load with their default visibility");
            await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
            Check(Aligned(registry), "Header labels line up with row cells");
            var originalStyle = registry.ListView.ItemContainerStyle;
            var originalPadding = registry.HeaderRow.Padding.Left;
            registry.ListView.ItemContainerStyle = new Style(typeof(ListViewItem))
            {
                BasedOn = originalStyle,
                Setters = { new Setter(Control.PaddingProperty, new Thickness(24, 0, 32, 0)) }
            };
            await WaitAsync(() => registry.HeaderRow.Padding.Left > originalPadding + 10 && Aligned(registry));
            Check(!Overflows(registry), "Changing container padding remeasures header alignment and fill widths");
            registry.ListView.ItemContainerStyle = originalStyle;
            await WaitAsync(() => Math.Abs(registry.HeaderRow.Padding.Left - originalPadding) < 1 && Aligned(registry));
            Check(
                Math.Abs(SurfaceWidth(registry) - ViewportWidth(registry)) < 1 && !Overflows(registry),
                "Fill columns use the viewport without horizontal overflow");
            var toolWidths = Enumerable.Range(0, 3)
                .Select(index => tools.HeaderRow.ColumnDefinitions[index].Width.Value).ToArray();
            Check(
                Math.Abs(toolWidths[1] / toolWidths[0] - 3) < 0.1 && Math.Abs(toolWidths[2] - toolWidths[0]) < 1.5,
                "Fill weights split the tool list 2:6:2");

            // Resizing.
            var changes = page.LayoutChanges;
            registry.ResizeColumn("Type", 250);
            await WaitAsync(() => RowWidth(registry, "Type") == 250);
            Check(
                registry.CurrentWidth("Type") == 250 && page.LayoutChanges == changes + 1 &&
                registry.Layout is ColumnLayout bound && bound.Find("Type")!.Width == 250,
                "Resizing updates header, rows, the bound layout and raises LayoutChanged once");
            registry.ResizeColumn("Type", 1);
            Check(registry.CurrentWidth("Type") == 60, "Resizing stops at the column minimum");
            registry.ResizeColumn("Type", 140);
            var nameBefore = registry.CurrentWidth("Name");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 820));
            await WaitAsync(() => registry.CurrentWidth("Name") < nameBefore - 50);
            await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
            Check(
                registry.CurrentWidth("Type") == 140 && !Overflows(registry),
                "Window resizing reflows fill columns and keeps fixed widths");
            registry.ResizeColumn("Data", 1400);
            await WaitAsync(() => SurfaceWidth(registry) > ViewportWidth(registry) + 200);
            await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
            Check(Aligned(registry), "Wide layouts scroll horizontally with header and rows together");
            registry.ResetLayout();
            await WaitAsync(() => Math.Abs(SurfaceWidth(registry) - ViewportWidth(registry)) < 1);

            // Reordering.
            registry.MoveColumn("Data", "Name");
            await WaitAsync(() => Grid.GetColumn(Cell(registry, "Data")) == 1);
            Check(
                Grid.GetColumn(registry.HeaderRow.LabelFor(registry.FindColumn("Data")!)!) == 1 &&
                Grid.GetColumn(Cell(registry, "Name")) == 2 && Grid.GetColumn(Cell(registry, "Icon")) == 0,
                "Moving a column reorders header and cells after the leading column");
            await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
            var typeLabel = registry.HeaderRow.LabelFor(registry.FindColumn("Type")!)!;
            var dropX = typeLabel.TransformToVisual(registry.HeaderRow).TransformPoint(default).X +
                        typeLabel.ActualWidth / 2;
            Check(
                registry.HeaderRow.ColumnAt(dropX)?.Id == "Type" && registry.HeaderRow.ColumnAt(-50) is null,
                "Header hit-testing finds the drop target under the pointer");
            registry.MoveColumnBy("Data", 1);
            Check(
                registry.EffectiveLayout.Columns.Select(column => column.Id).Take(2).SequenceEqual((string[])["Name", "Data"]),
                "Move right swaps with the next visible column");

            // Drag feedback, driven through the same pipeline the pointer handlers use.
            await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
            var header = registry.HeaderRow;
            var dataColumn = registry.FindColumn("Data")!;
            var dataLabel = header.LabelFor(dataColumn)!;
            var lastWriteLabel = header.LabelFor(registry.FindColumn("LastWrite")!)!;

            double LeftOf(FrameworkElement element)
            {
                return element.TransformToVisual(header).TransformPoint(default).X;
            }

            double MarkerX()
            {
                return LeftOf(header.InsertionMarker) + header.InsertionMarker.ActualWidth / 2;
            }

            var middle = header.ActualHeight / 2;
            header.StartColumnDrag(dataColumn, LeftOf(dataLabel) + 10);
            await Task.Delay(50);
            Check(
                header.IsDragging && header.DragGhost.Visibility == Visibility.Visible && dataLabel.Opacity < 1 &&
                Math.Abs(LeftOf(header.DragGhost) - LeftOf(dataLabel)) < 1,
                $"Starting a drag shows a ghost under the pointer and dims the original header " +
                $"(dragging={header.IsDragging} ghost={header.DragGhost.Visibility} opacity={dataLabel.Opacity} " +
                $"ghostX={LeftOf(header.DragGhost):F1} labelX={LeftOf(dataLabel):F1} " +
                $"canvasLeft={Canvas.GetLeft(header.DragGhost):F1} " +
                $"overlayX={LeftOf((FrameworkElement)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(header.DragGhost)):F1} " +
                $"ghostW={header.DragGhost.ActualWidth:F1})");
            var endX = LeftOf(lastWriteLabel) + lastWriteLabel.ActualWidth - 5;
            header.MoveColumnDrag(endX, middle);
            await Task.Delay(50);
            Check(
                header.DragTarget == (true, null) && header.InsertionMarker.Visibility == Visibility.Visible &&
                Math.Abs(
                    MarkerX() - (LeftOf(lastWriteLabel) + lastWriteLabel.ActualWidth + registry.ColumnSpacing / 2)) <
                2 && Math.Abs(LeftOf(header.DragGhost) - (endX - 10)) < 1,
                "Past the last header's midpoint the marker shows the end gap and the ghost follows");
            header.MoveColumnDrag(LeftOf(dataLabel) + 5, middle);
            Check(
                !header.DragTarget.Valid && header.InsertionMarker.Visibility == Visibility.Collapsed,
                "Hovering the column's own gap shows no insertion marker");
            header.MoveColumnDrag(endX, header.ActualHeight + 200);
            Check(!header.DragTarget.Valid, "Dragging far below the header disables the drop");
            header.MoveColumnDrag(endX, middle);
            Check(header.DropColumnDrag(), "Dropping in a valid gap moves the column");
            await WaitAsync(() => header.DragGhost.Visibility == Visibility.Collapsed);
            Check(
                registry.EffectiveLayout.Visible.Select(column => column.Id)
                    .SequenceEqual((string[])["Name", "Type", "LastWrite", "Data"]) && dataLabel.Opacity == 1 &&
                header.InsertionMarker.Visibility == Visibility.Collapsed,
                "The drop lands in the marked gap and clears the drag visuals");
            var beforeCancel = registry.EffectiveLayout;
            header.StartColumnDrag(dataColumn, LeftOf(dataLabel) + 10);
            header.MoveColumnDrag(LeftOf(header.LabelFor(registry.FindColumn("Name")!)!) + 2, middle);
            Check(header.DragTarget == (true, "Name"), "The marker tracks the gap before the hovered header");
            header.CancelColumnDrag();
            Check(
                !header.IsDragging && !header.DropColumnDrag() &&
                ReferenceEquals(registry.EffectiveLayout, beforeCancel) &&
                header.DragGhost.Visibility == Visibility.Collapsed,
                "Escape cancels the drag without moving the column");
            Check(
                header.MoveByKeyboard(dataColumn, -1) &&
                registry.EffectiveLayout.Visible.Select(column => column.Id)
                    .SequenceEqual((string[])["Name", "Type", "Data", "LastWrite"]) &&
                !header.MoveByKeyboard(registry.FindColumn("Name")!, -1),
                "The reorder shortcut moves the focused header's column and stops at the edge");
            Check(
                registry.KeyboardReorderModifiers == (Windows.System.VirtualKeyModifiers.Control |
                                                      Windows.System.VirtualKeyModifiers.Shift),
                "The reorder shortcut defaults to Ctrl+Shift, leaving Alt+arrows to host navigation");

            // Visibility through the header menu.
            var menu = registry.HeaderRow.Menu;
            await OpenAsync(menu, typeLabel);
            Check(
                MenuItem(menu, "ToggleColumnName") is { IsEnabled: false } &&
                MenuItem(menu, "ToggleColumnSize") is ToggleMenuFlyoutItem { IsChecked: false, IsEnabled: true } &&
                MenuItem(menu, "MoveColumnLeft") is { IsEnabled: true },
                "The header menu reflects visibility, hide guards and move availability");
            Invoke(MenuItem(menu, "ToggleColumnType")!);
            await WaitAsync(() => !menu.IsOpen && Cell(registry, "Type").Visibility == Visibility.Collapsed);
            Check(
                typeLabel.Visibility == Visibility.Collapsed && !registry.IsColumnVisible("Type"),
                "Hiding a column from the header menu collapses its header and cells");
            registry.ToggleColumn("Size");
            await WaitAsync(() => Cell(registry, "Size").Visibility == Visibility.Visible);
            Check(
                ((TextBlock)Cell(registry, "Size")).Text.EndsWith(" B", StringComparison.Ordinal),
                "Showing a column creates its cells lazily from CellText");
            tools.ToggleColumn("Subject");
            tools.ToggleColumn("Summary");
            Check(
                !tools.CanToggleColumn("Path") && tools.EffectiveLayout.Visible.Count() == 1,
                "The last visible column cannot be hidden");
            tools.ResetLayout();

            // Auto-fit.
            registry.ResetLayout();
            await WaitAsync(() => registry.IsColumnVisible("Type"));
            registry.ResizeColumn("Name", 80);
            await WaitAsync(() => RowWidth(registry, "Name") == 80);
            var nameCell = (TextBlock)Cell(registry, "Name", 0);
            nameCell.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var needed = nameCell.DesiredSize.Width;
            nameCell.InvalidateMeasure();
            registry.AutoFitColumn("Name");
            await WaitAsync(() => RowWidth(registry, "Name") > 80);
            Check(
                registry.CurrentWidth("Name") >= needed && registry.CurrentWidth("Name") <= needed + 40 &&
                registry.EffectiveLayout.Find("Name")!.Width == registry.CurrentWidth("Name"),
                "Auto-fit sizes a column to its widest realized cell");

            // Sorting.
            Invoke(Button(registry, "NameSortButton"));
            await WaitAsync(() => registry.SortColumnId == "Name");
            var nameLabel = registry.HeaderRow.LabelFor(registry.FindColumn("Name")!)!;
            var nameSortIcon = registry.HeaderRow.SortIconFor(registry.FindColumn("Name")!)!;
            var typeSortIcon = registry.HeaderRow.SortIconFor(registry.FindColumn("Type")!)!;
            Check(
                page.LastSortRequest == "Name" &&
                nameSortIcon is { Visibility: Visibility.Visible, Glyph: ColumnHeaderRow.SortAscendingGlyph } &&
                typeSortIcon.Visibility == Visibility.Collapsed &&
                AutomationProperties.GetItemStatus(nameLabel) == "Sorted ascending" &&
                AutomationProperties.GetItemStatus(typeLabel) == "Not sorted",
                "Clicking a sortable header requests sorting and shows the ascending chevron");
            Invoke(Button(registry, "NameSortButton"));
            await WaitAsync(() => registry.SortDescending);
            Check(
                nameSortIcon is { Visibility: Visibility.Visible, Glyph: ColumnHeaderRow.SortDescendingGlyph } &&
                page.Entries[0].Name == page.Entries.Max(entry => entry.Name),
                "Clicking again sorts descending and flips the chevron");
            Check(
                Button(tools, "SubjectHeader") is { IsTabStop: true } &&
                AutomationProperties.GetItemStatus(Button(tools, "SubjectHeader")) == string.Empty,
                "Non-sortable headers expose no sort state but stay focusable for keyboard reordering");

            // Multiselect.
            var hostNotifications = 0;
            var listNotifications = 0;

            void CountHost(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
            {
                hostNotifications++;
            }

            void CountList(object sender, SelectionChangedEventArgs args)
            {
                listNotifications++;
            }

            page.RegistrySelection.CollectionChanged += CountHost;
            registry.ListView.SelectionChanged += CountList;
            var selectAllTimer = Stopwatch.StartNew();
            registry.ListView.SelectAll();
            await WaitAsync(() => page.RegistrySelection.Count == MainPage.EntryCount);
            selectAllTimer.Stop();
            Check(
                hostNotifications == 1,
                $"Select all reaches the host selection in one notification ({selectAllTimer.ElapsedMilliseconds} ms)");
            page.RegistrySelection.Clear();
            await WaitAsync(() => registry.ListView.SelectedItems.Count == 0);
            listNotifications = 0;
            var restoreTimer = Stopwatch.StartNew();
            registry.RestoreSelection(page.Entries.Take(5000));
            restoreTimer.Stop();
            Check(
                registry.ListView.SelectedItems.Count == 5000 && registry.ListView.SelectedRanges.Count == 1 &&
                listNotifications <= 1 && page.RegistrySelection.Count == 0,
                $"Restoring 5,000 selected rows uses one range without changing the host selection ({restoreTimer.ElapsedMilliseconds} ms)");
            listNotifications = 0;
            registry.RestoreSelection(page.Entries);
            Check(
                registry.ListView.SelectedItems.Count == MainPage.EntryCount && listNotifications <= 1,
                "Restoring every selected row uses SelectAll instead of individual additions");
            listNotifications = 0;
            registry.RestoreSelection(page.Entries.Take(3));
            Check(
                registry.ListView.SelectedItems.Count == 3 && listNotifications <= 2,
                "Restoring a filtered selection removes rows that no longer belong to it");
            registry.RestoreSelection([]);
            Check(registry.ListView.SelectedItems.Count == 0, "Restoring an empty selection clears the list selection");
            listNotifications = 0;
            page.RegistrySelection.ReplaceAll(page.Entries.Skip(100).Take(5000));
            await WaitAsync(() => registry.ListView.SelectedItems.Count == 5000);
            Check(
                listNotifications <= 2 && registry.ListView.SelectedRanges.Count == 1,
                $"A large host selection is applied as index ranges ({listNotifications} list notifications)");
            page.RegistrySelection.ReplaceAll(page.Entries.Where((_, index) => index % 2 == 0));
            await WaitAsync(() => registry.ListView.SelectedItems.Count == MainPage.EntryCount / 2);
            Check(
                registry.ListView.SelectedItems.Contains(page.Entries[0]) &&
                !registry.ListView.SelectedItems.Contains(page.Entries[1]),
                "Scattered host selections select exactly the requested rows");
            hostNotifications = 0;
            registry.ListView.SelectAll();
            await WaitAsync(() => page.RegistrySelection.Count == MainPage.EntryCount);
            page.RegistrySelection.ReplaceAll(page.RegistrySelection.ToArray());
            Check(hostNotifications == 1, "Replacing the selection with equal contents raises nothing");
            page.RegistrySelection.CollectionChanged -= CountHost;
            registry.ListView.SelectionChanged -= CountList;
            page.RegistrySelection.Clear();
            await WaitAsync(() => registry.ListView.SelectedItems.Count == 0);
            foreach (var entry in page.Entries.Take(3))
            {
                page.RegistrySelection.Add(entry);
            }

            await WaitAsync(() => registry.ListView.SelectedItems.Count == 3);
            Check(
                page.Entries.Take(3).All(registry.ListView.SelectedItems.Contains),
                "Host selection changes select the matching rows");
            Invoke(Button(registry, "TypeSortButton"));
            await WaitAsync(() => registry.SortColumnId == "Type");
            Check(
                registry.ListView.SelectedItems.Count == 3 && page.RegistrySelection.Count == 3,
                "Selection survives a host re-sort");
            registry.ListView.SelectedItems.Remove(registry.ListView.SelectedItems[0]);
            await WaitAsync(() => page.RegistrySelection.Count == 2);
            Check(true, "Deselecting a row updates the host selection");

            // Inline editing.
            registry.ListView.ScrollIntoView(page.Entries[0]);
            var target = page.Entries[5];
            var original = target.Name;
            var nameColumn = registry.FindColumn("Name")!;
            var ended = new List<ColumnEditEndedEventArgs>();
            registry.EditEnded += (_, args) => ended.Add(args);
            Check(!registry.BeginEdit(target, "Type"), "Columns that are not editable do not open an editor");
            registry.CanEditItem = (item, _) => !ReferenceEquals(item, target);
            Check(
                !registry.BeginEdit(target) && !registry.IsEditing,
                "CanEditItem keeps read-only rows from opening an editor");
            registry.CanEditItem = null;
            Check(registry.BeginEdit(target), "BeginEdit opens the first editable column");
            await WaitAsync(
                () => registry.Editor is { FocusState: not FocusState.Unfocused } box &&
                      box.SelectionLength == original.Length,
                () => $"focus={registry.Editor?.FocusState} selection={registry.Editor?.SelectionLength}");
            var editor = registry.Editor!;
            var editedRow = RowOf(registry, target);
            Check(
                editor.Text == original && ReferenceEquals(registry.EditingItem, target) &&
                Grid.GetColumn(editor) == Grid.GetColumn(editedRow.CellFor(nameColumn)!) &&
                editedRow.CellFor(nameColumn)!.Opacity == 0,
                "The editor covers the cell with the current name selected and focused");
            editor.Text = "  ";
            Check(
                !registry.CommitEdit() && registry.IsEditing && editor.FocusState != FocusState.Unfocused &&
                AutomationProperties.GetHelpText(editor) == "A name cannot be empty.",
                "Rejected text keeps the editor open with the host's error");
            editor.Text = page.Entries[6].Name;
            Check(!registry.CommitEdit() && registry.IsEditing, "The host can reject duplicate names");
            editor.Text = "Renamed value";
            Check(
                registry.CommitEdit() && !registry.IsEditing && target.Name == "Renamed value" &&
                ended[^1] is { Committed: true, ColumnId: "Name" } && !editedRow.Children.Contains(editor),
                "Committing applies the host rename and removes the editor");
            await WaitAsync(() => ((TextBlock)RowOf(registry, target).CellFor(nameColumn)!).Text == "Renamed value");
            Check(RowOf(registry, target).CellFor(nameColumn)!.Opacity == 1, "The row shows the renamed value");
            registry.BeginEdit(target);
            registry.Editor!.Text = "Discarded";
            registry.CancelEdit();
            Check(
                target.Name == "Renamed value" && ended[^1] is { Committed: false } && !registry.IsEditing,
                "Cancelling leaves the item unchanged");
            registry.BeginEdit(target);
            Check(
                registry.BeginEdit(page.Entries[7]) && ended[^1] is { Committed: false } &&
                ReferenceEquals(registry.EditingItem, page.Entries[7]),
                "Starting another edit closes the unchanged one");
            registry.CancelEdit();
            registry.BeginEdit(target);
            await WaitAsync(() => registry.Editor is { FocusState: not FocusState.Unfocused });
            registry.Editor!.Text = "Committed on focus loss";
            registry.ListView.Focus(FocusState.Programmatic);
            await WaitAsync(() => !registry.IsEditing);
            Check(target.Name == "Committed on focus loss", "Moving focus away commits the edit");
            foreach (var focusTarget in new Control[]
                     {
                         (TextBox)page.FindName("LayoutText"),
                         tools.HeaderRow.LabelFor(tools.FindColumn("Subject")!)!
                     })
            {
                registry.BeginEdit(target);
                await WaitAsync(() => registry.Editor is { FocusState: not FocusState.Unfocused });
                var rejectedEditor = registry.Editor!;
                rejectedEditor.Text = page.Entries[6].Name;
                Check(focusTarget.Focus(FocusState.Programmatic), "The control outside the editor accepts focus");
                await WaitAsync(() => !registry.IsEditing);
                Check(
                    ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), focusTarget) &&
                    target.Name == "Committed on focus loss" && !ended[^1].Committed &&
                    !string.IsNullOrEmpty(AutomationProperties.GetHelpText(rejectedEditor)),
                    "A duplicate name rejected on blur leaves focus outside the list and preserves the original name");
            }

            registry.BeginEdit(target);
            await WaitAsync(() => registry.Editor is { FocusState: not FocusState.Unfocused });
            registry.Editor!.Text = "Committed on scroll";
            registry.ListView.ScrollIntoView(page.Entries[^1]);
            await WaitAsync(() => !registry.IsEditing);
            Check(target.Name == "Committed on scroll", "Scrolling the edited row out of view commits the edit");
            registry.ListView.ScrollIntoView(page.Entries[0]);
            await WaitAsync(() => registry.RealizedRows.Any(row => ReferenceEquals(row.DataContext, page.Entries[0])));
            registry.BeginEdit(page.Entries[0]);
            registry.Editor!.Text = string.Empty;
            registry.ListView.ScrollIntoView(page.Entries[^1]);
            await WaitAsync(() => !registry.IsEditing);
            Check(page.Entries[0].Name.Length > 0, "Scrolling away from rejected text cancels the edit");
            registry.DismissDetachedEditor(target, nameColumn, "Committed after detaching");
            Check(
                !registry.IsEditing && target.Name == "Committed after detaching" && ended[^1].Committed,
                "A detached edited row commits without crashing during ViewChanged");
            registry.DismissDetachedEditor(target, nameColumn, string.Empty);
            Check(
                !registry.IsEditing && target.Name == "Committed after detaching" && !ended[^1].Committed,
                "A detached edited row cancels rejected text without crashing");
            registry.ListView.ScrollIntoView(page.Entries[0]);
            var toolEvent = (SampleEvent)tools.ListView.Items[0];
            Check(tools.BeginEdit(toolEvent, "Summary"), "Text columns can be edited");
            tools.Editor!.Text = "Edited summary";
            tools.CommitEdit();
            await WaitAsync(() => ((TextBlock)RowOf(tools, toolEvent).CellFor(tools.FindColumn("Summary")!)!).Text ==
                                  "Edited summary");
            Check(toolEvent.Summary == "Edited summary", "Committed text cells refresh from CellText");
            tools.BeginEdit(toolEvent, "Summary");
            tools.ToggleColumn("Summary");
            Check(!tools.IsEditing, "Hiding the edited column abandons the edit");
            tools.ToggleColumn("Summary");

            // Virtualization.
            var stopwatch = Stopwatch.StartNew();
            var beats = 0;
            var heartbeat = registry.DispatcherQueue.CreateTimer();
            heartbeat.Interval = TimeSpan.FromMilliseconds(16);
            heartbeat.Tick += (_, _) => beats++;
            heartbeat.Start();
            for (var index = 0; index < MainPage.EntryCount; index += 1000)
            {
                registry.ListView.ScrollIntoView(page.Entries[index]);
                await Task.Delay(30);
            }

            registry.ListView.ScrollIntoView(page.Entries[^1]);
            await WaitAsync(() => registry.ListView.ContainerFromIndex(MainPage.EntryCount - 1) is ListViewItem);
            heartbeat.Stop();
            var realized = registry.RealizedRows.Count;
            largeList = new { realized, elapsedMs = stopwatch.ElapsedMilliseconds, beats };
            Check(realized is > 0 and < 250, "Scrolling 10,000 rows realizes a bounded number of rows");
            Check(beats >= stopwatch.ElapsedMilliseconds / 16 / 4, "The UI thread stays responsive while scrolling");
            await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
            Check(Aligned(registry), "Recycled rows keep the current column arrangement");
            var visibleChanges = 0;
            registry.VisibleItemsChanged += (_, _) => visibleChanges++;
            registry.ListView.ScrollIntoView(page.Entries[MainPage.EntryCount - 40]);
            await WaitAsync(() => visibleChanges > 0);
            registry.ListView.ScrollIntoView(page.Entries[^1]);
            await Task.Delay(100);
            var visible = registry.VisibleItems;
            var lastIndex = MainPage.EntryCount - 1;
            Check(
                visible.Count > 0 && visible.Count < registry.RealizedRows.Count &&
                ReferenceEquals(visible[^1], page.Entries[lastIndex]) &&
                visible.All(item => registry.ListView.ContainerFromItem(item) is ListViewItem container &&
                                    InViewport(registry.ListView, container)),
                $"VisibleItems lists only on-screen rows ({visible.Count} of {registry.RealizedRows.Count} realized)");
            Check(
                visible.Select(item => registry.ListView.Items.IndexOf(item)).SequenceEqual(
                    Enumerable.Range(lastIndex - visible.Count + 1, visible.Count)),
                "VisibleItems are ordered top to bottom");
            Check(visibleChanges > 0, "Scrolling raises VisibleItemsChanged");

            // Density from the shared helper.
            ListDensity.SetSpacing(registry.ListView, 10);
            await WaitAsync(() => FirstRow(registry).Padding.Top == 10);
            Check(FirstRow(registry).MinHeight >= 42, "ListDensity applies to generated rows");
            ListDensity.SetSpacing(registry.ListView, 3);

            // Accessible row names come from the host.
            registry.ItemAutomationName = item => "Row " + (item as SampleEntry)?.Name;
            var namedRow = FirstRow(registry);
            Check(
                AutomationProperties.GetName(namedRow) == "Row " + (namedRow.DataContext as SampleEntry)?.Name,
                "ItemAutomationName names realized rows for screen readers");
            registry.ListView.ScrollIntoView(page.Entries[5000]);
            await WaitAsync(() => registry.VisibleItems.Contains(page.Entries[5000]));
            var reboundRow = RowOf(registry, page.Entries[5000]);
            Check(
                AutomationProperties.GetName(reboundRow) == "Row " + page.Entries[5000].Name,
                "Recycled rows take the accessible name of their new item");
            registry.ListView.ScrollIntoView(page.Entries[0]);

            // Compact presentation.
            tools.ShowColumns = false;
            await WaitAsync(() =>
                tools.ListView.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: StackPanel });
            Check(
                tools.HeaderRow.Visibility == Visibility.Collapsed && tools.RealizedRows.Count == 0,
                "Hiding columns swaps to the compact template and hides the header");
            tools.ShowColumns = true;
            await WaitAsync(() =>
                tools.ListView.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: ColumnRow });
            await WaitAsync(() => Aligned(tools), () => Misalignment(tools));
            Check(tools.HeaderRow.Visibility == Visibility.Visible, "Showing columns restores aligned column rows");

            if (App.InputTests)
            {
                await RunMouseChecksAsync(page, window, Check);
            }

            // Persistence.
            var stored = new Dictionary<string, ColumnLayout>();
            var saves = 0;
            var persistence = new ColumnLayoutPersistence(
                registry.DispatcherQueue, stored.GetValueOrDefault, (id, layout) =>
                {
                    stored[id] = layout;
                    saves++;
                });

            ColumnListView NewList(string id)
            {
                var list = new ColumnListView();
                list.Columns.Add(new ListColumn { Id = "Name", DefaultWidth = 150 });
                list.Columns.Add(new ListColumn { Id = "Data", DefaultWidth = 200 });
                persistence.Attach(list, id);
                return list;
            }

            var first = NewList("Shared");
            var second = NewList("Shared");
            var other = NewList("Other");
            first.ResizeColumn("Name", 222);
            second.ToggleColumn("Data");
            var late = NewList("Shared");
            Check(
                saves == 0 &&
                new[] { first, second, late }.All(list =>
                    list.CurrentWidth("Name") == 222 && !list.IsColumnVisible("Data")) && other.IsColumnVisible("Data"),
                "Lists sharing an id see pending widths and visibility before the debounced save");
            other.ResizeColumn("Name", 333);
            persistence.Flush();
            Check(
                saves == 2 && stored["Shared"].Find("Name")!.Width == 222 && !stored["Shared"].Find("Data")!.Visible &&
                stored["Other"].Find("Name")!.Width == 333,
                "Closing before the debounce expires flushes the latest layout for each list id");
            persistence.Flush();
            await Task.Delay(450);
            Check(
                saves == 2 && NewList("Shared").EffectiveLayout.IsEquivalentTo(first.EffectiveLayout),
                "Flushing stops the timer and later lists restore the saved layout without duplicate writes");
            first.ResizeColumn("Name", 244);
            await WaitAsync(() => saves == 3);
            Check(
                stored["Shared"].Find("Name")!.Width == 244,
                "Settling a column drag saves its latest shared layout after the debounce");
            registry.ResizeColumn("Type", 222);
            registry.MoveColumn("LastWrite", "Name");
            registry.ToggleColumn("Data");
            var saved = registry.EffectiveLayout;
            page.SaveLayout();
            registry.Layout = null;
            Check(
                registry.EffectiveLayout.IsEquivalentTo(ColumnLayout.Default(registry.Specs)),
                "Clearing the layout restores defaults");
            page.ReloadLayout();
            await WaitAsync(() => !registry.IsColumnVisible("Data"));
            Check(
                registry.EffectiveLayout.IsEquivalentTo(saved) &&
                Grid.GetColumn(registry.HeaderRow.LabelFor(registry.FindColumn("LastWrite")!)!) == 1 &&
                registry.CurrentWidth("Type") == 222, "A saved layout survives a JSON round trip");
            registry.Layout = new ColumnLayout((ColumnState[])[new ColumnState("Bogus", 10, true), new ColumnState("Type", -5, true)]);
            Check(
                registry.EffectiveLayout.Columns.Select(column => column.Id)
                    .SequenceEqual((string[])["Name", "Type", "Data", "LastWrite", "Size"]) &&
                registry.CurrentWidth("Type") == 140,
                "Stale saved layouts are normalized against the declared columns");
            File.Delete(page.LayoutFile);
            registry.ResetLayout();
            await CommandIconChecks.RunAsync(window, Check);
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }
        finally
        {
            if (App.InputTests)
            {
                await MouseInput.UpAsync();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(
            output,
            JsonSerializer.Serialize(
                new { passed, largeList, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
    }

    private static ColumnRow FirstRow(ColumnListView list)
    {
        // Recycled containers stay in the tree, parked far outside the viewport; only consider visible rows.
        return list.RealizedRows.Where(row => row.ActualHeight > 0)
            .Select(row => (Row: row, Position: row.TransformToVisual(list).TransformPoint(default))).Where(entry =>
                entry.Position.X > -list.ActualWidth && entry.Position.X < list.ActualWidth && entry.Position.Y >= 0 &&
                entry.Position.Y < list.ActualHeight).OrderBy(entry => entry.Position.Y).First().Row;
    }

    // Real cursor and button input: pointer capture, Click suppression and routing exactly as a user produces them.
    private static async Task RunMouseChecksAsync(MainPage page, Window window, Action<bool, string> check)
    {
        await MouseInput.UpAsync();
        var registry = page.Registry;
        var header = registry.HeaderRow;
        registry.ListView.ScrollIntoView(page.Entries[0]);
        registry.ResetLayout();
        registry.HeaderScroll!.ChangeView(0, null, null, true);
        await WaitAsync(() => Aligned(registry), () => Misalignment(registry));
        await MouseInput.FocusAsync(window);
        await Task.Delay(200);

        (int X, int Y) At(string id, double fraction)
        {
            var label = header.LabelFor(registry.FindColumn(id)!)!;
            return MouseInput.ToScreen(
                window, label, new Windows.Foundation.Point(label.ActualWidth * fraction, label.ActualHeight / 2));
        }

        var sortBefore = (registry.SortColumnId, registry.SortDescending);
        var start = At("Type", 0.5);
        await MouseInput.MoveAsync(start);
        await MouseInput.DownAsync();
        var end = At("LastWrite", 0.9);
        await MouseInput.GlideAsync(start, end);
        await Task.Delay(100);
        check(
            header.IsDragging && header.DragGhost.Visibility == Visibility.Visible &&
            header.InsertionMarker.Visibility == Visibility.Visible && header.DragTarget == (true, null),
            "Mouse: dragging a header shows the ghost and the end-gap marker");
        await MouseInput.UpAsync();
        await WaitAsync(() => !header.IsDragging);
        check(
            registry.EffectiveLayout.Visible.Select(column => column.Id)
                .SequenceEqual((string[])["Name", "Data", "LastWrite", "Type"]) &&
            (registry.SortColumnId, registry.SortDescending) == sortBefore &&
            header.DragGhost.Visibility == Visibility.Collapsed, "Mouse: releasing drops the column without sorting");

        var name = At("Name", 0.5);
        await MouseInput.MoveAsync(name);
        await MouseInput.DownAsync();
        await MouseInput.GlideAsync(name, (name.X + 60, name.Y), 6);
        await MouseInput.GlideAsync((name.X + 60, name.Y), name, 6);
        await MouseInput.UpAsync();
        check(
            (registry.SortColumnId, registry.SortDescending) == sortBefore &&
            registry.EffectiveLayout.Visible.First().Id == "Name",
            "Mouse: a drag released over its own header neither moves nor sorts");

        var layout = registry.EffectiveLayout;
        var data = At("Data", 0.5);
        await MouseInput.MoveAsync(data);
        await MouseInput.DownAsync();
        await MouseInput.GlideAsync(data, At("Name", 0.1));
        await MouseInput.PressKeyAsync(0x1B);
        check(!header.IsDragging && header.DragGhost.Visibility == Visibility.Collapsed, "Mouse: Escape ends the drag");
        await MouseInput.UpAsync();
        check(
            registry.EffectiveLayout.IsEquivalentTo(layout) &&
            (registry.SortColumnId, registry.SortDescending) == sortBefore,
            "Mouse: releasing after Escape changes nothing");

        var type = At("Type", 0.5);
        await MouseInput.MoveAsync(type);
        await MouseInput.DownAsync();
        await MouseInput.UpAsync();
        await WaitAsync(() => registry.SortColumnId == "Type");
        check(true, "Mouse: a plain click on a header still sorts");
        registry.ResetLayout();
    }

    private static bool InViewport(FrameworkElement list, FrameworkElement element)
    {
        var origin = element.TransformToVisual(list).TransformPoint(default);
        return origin.Y + element.ActualHeight > 0 && origin.Y < list.ActualHeight && origin.X > -element.ActualWidth &&
               origin.X < list.ActualWidth;
    }

    private static ColumnRow RowOf(ColumnListView list, object item)
    {
        return list.RealizedRows.First(row => ReferenceEquals(row.DataContext, item));
    }

    private static FrameworkElement Cell(ColumnListView list, string id, int? index = null)
    {
        var column = list.LeadingColumn?.Id == id ? list.LeadingColumn : list.FindColumn(id)!;
        var row = index is { } at
            ? (ColumnRow)((ListViewItem)list.ListView.ContainerFromIndex(at)).ContentTemplateRoot
            : FirstRow(list);
        return row.CellFor(column) ?? throw new InvalidOperationException($"No {id} cell.");
    }

    private static double RowWidth(ColumnListView list, string id)
    {
        var column = list.FindColumn(id)!;
        var slot = list.EffectiveLayout.Visible.Select(state => state.Id).ToList().IndexOf(id) +
                   (list.LeadingColumn is null ? 0 : 1);
        var row = FirstRow(list);
        return row.CellFor(column) is null || slot >= row.ColumnDefinitions.Count
            ? -1
            : row.ColumnDefinitions[slot].Width.Value;
    }

    private static double SurfaceWidth(ColumnListView list)
    {
        return ((FrameworkElement)list.FindName("Surface")).ActualWidth;
    }

    private static double ViewportWidth(ColumnListView list)
    {
        return ((FrameworkElement)list.FindName("HorizontalScroll")).ActualWidth;
    }

    private static bool Overflows(ColumnListView list)
    {
        var row = FirstRow(list);
        var last = row.Children.OfType<FrameworkElement>().Where(cell => cell.Visibility == Visibility.Visible)
            .MaxBy(Grid.GetColumn)!;
        return last.TransformToVisual(row).TransformPoint(default).X + last.ActualWidth > row.ActualWidth + 1;
    }

    private static bool Aligned(ColumnListView list)
    {
        return Misalignment(list) is null;
    }

    private static string? Misalignment(ColumnListView list)
    {
        var surface = (FrameworkElement)list.FindName("Surface");
        var row = FirstRow(list);
        foreach (var column in list.Columns.Where(column => list.IsColumnVisible(column.Id)))
        {
            var label = list.HeaderRow.LabelFor(column)!;
            if (row.CellFor(column) is not { } cell || label.ActualWidth <= 0)
            {
                return $"{column.Id} has no laid-out cell";
            }

            var labelX = label.TransformToVisual(surface).TransformPoint(default).X;
            var cellSlotX = row.TransformToVisual(surface).TransformPoint(default).X +
                            SlotLeft(row, Grid.GetColumn(cell));
            if (Math.Abs(labelX - cellSlotX) > 1 ||
                Math.Abs(label.ActualWidth - row.ColumnDefinitions[Grid.GetColumn(cell)].ActualWidth) > 1)
            {
                return $"{column.Id}: header x={labelX:F1} w={label.ActualWidth:F1}, cell x={cellSlotX:F1} " +
                       $"w={row.ColumnDefinitions[Grid.GetColumn(cell)].ActualWidth:F1}";
            }
        }

        return null;
    }

    private static double SlotLeft(ColumnRow row, int slot)
    {
        var left = row.Padding.Left;
        for (var index = 0; index < slot; index++)
        {
            left += row.ColumnDefinitions[index].ActualWidth + row.ColumnSpacing;
        }

        return left;
    }

    private static Button Button(DependencyObject parent, string id)
    {
        return Find<Button>(parent, id) ?? throw new InvalidOperationException($"Button not found: {id}");
    }

    private static T? Find<T>(DependencyObject parent, string id)
        where T : FrameworkElement
    {
        if (parent is T match && AutomationProperties.GetAutomationId(match) == id)
        {
            return match;
        }

        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (Find<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i), id) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static MenuFlyoutItemBase? MenuItem(MenuFlyout menu, string id)
    {
        return menu.Items.FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == id);
    }

    private static async Task OpenAsync(FlyoutBase menu, FrameworkElement target)
    {
        var opened = new TaskCompletionSource();

        void Opened(object? sender, object args)
        {
            opened.TrySetResult();
        }

        menu.Opened += Opened;
        menu.ShowAt(target);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        menu.Opened -= Opened;
    }

    private static void Invoke(UIElement element)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
            return;
        }

        if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
        {
            toggle.Toggle();
            return;
        }

        throw new InvalidOperationException("The element has no invoke or toggle pattern.");
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string?>? detail = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(detail?.Invoke() ?? "The expected component state did not appear.");
            }

            await Task.Delay(20);
        }
    }
}
