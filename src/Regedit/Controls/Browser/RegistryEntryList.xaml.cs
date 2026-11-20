// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Lists;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace JPSoftworks.Regedit.Controls.Browser;

public sealed partial class RegistryEntryList : UserControl
{
    internal event Action<RegistryEntry>? OpenInBackground;

    internal event Action<char>? FilterTyped;

    internal event Action<string>? RenameRejected;

    private bool _showPreview;
    private bool _syncingSelection;
    private bool _publishingLayout;
    private SettingsViewModel _preferences = null!;
    private RegistryFont? _dataFont;
    private FontFamily? _dataFamily;

    public BrowserViewModel ViewModel { get; private set; } = null!;

    private ListView EntriesList => this.Entries.ListView;

    public RegistryEntryList()
    {
        this.InitializeComponent();
        var list = this.EntriesList;
        AutomationProperties.SetName(list, "Registry keys and values");
        AutomationProperties.SetAutomationId(list, "EntriesList");
        list.CharacterReceived += this.Entries_CharacterReceived;
        list.DoubleTapped += this.Entries_DoubleTapped;
        list.PreviewKeyDown += this.Entries_KeyDown;
        list.SelectionChanged += this.Entries_SelectionChanged;
        this.Entries.ItemAutomationName = item => (item as RegistryEntry)?.AccessibleName;
        this.Entries.CanEditItem = (item, _) => item is RegistryEntry entry && this.ViewModel?.CanRenameEntry(entry) == true;
        this.Entries.FindColumn(nameof(RegistrySortColumn.Name))!.EditText = item => (item as RegistryEntry)?.Name;
        this.EntriesList.AddHandler(PointerPressedEvent, new PointerEventHandler(this.Entries_PointerPressed), true);
        MiddleClick.Attach(
            this.EntriesList, args => MiddleClick.Container<ListViewItem>(this.EntriesList, args)?.Content as RegistryEntry,
            entry => this.OpenInBackground?.Invoke(entry));
    }

    internal void Initialize(BrowserViewModel viewModel, SettingsViewModel preferences, MenuFlyout entries, MenuFlyout background)
    {
        this.ViewModel = viewModel;
        this._preferences = preferences;
        this.EntriesList.ItemContainerStyle = new Style(typeof(ListViewItem))
        {
            BasedOn = this.EntriesList.ItemContainerStyle, Setters = { new Setter(ContextFlyoutProperty, entries) }
        };
        this.EntriesList.ContextFlyout = background;
        // Value edits can also start from menus, so keep the viewport at the command boundary.
        foreach (var command in new[] { viewModel.EditValueCommand, viewModel.EditBinaryCommand })
        {
            command.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(command.IsRunning) && command is { IsRunning: true, ExecutionTask: { } edit })
                {
                    await this.KeepPlaceAsync(() => edit);
                }
            };
        }

        this.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(this.ViewModel.IsInspectorOpen) or nameof(this.ViewModel.IsPreviewCurrent))
            {
                this.SetPreview(this._showPreview);
            }

            if (args.PropertyName == nameof(this.ViewModel.Layout))
            {
                this.ApplyColumnLayout();
            }

            if (args.PropertyName is nameof(this.ViewModel.SelectedEntries) or nameof(this.ViewModel.IsUpdatingValue)
                && !this.ViewModel.IsUpdatingValue
                && !this._syncingSelection)
            {
                this._syncingSelection = true;
                try
                {
                    this.Entries.RestoreSelection(this.ViewModel.SelectedEntries);
                }
                finally
                {
                    this._syncingSelection = false;
                }

                this.SetPreview(this._showPreview);
            }

            if (args.PropertyName is nameof(this.ViewModel.SortColumn) or nameof(this.ViewModel.SortDescending))
            {
                this.UpdateSort();
            }

            if (args.PropertyName == nameof(this.ViewModel.VisibleEntryCount))
            {
                this.EmptyState.Visibility = this.ViewModel.VisibleEntryCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        };
        this.ApplyColumnLayout();
        this.UpdateSort();
    }

    // The registry list keeps its historical settings: visible order is global (ListColumns) and widths belong to
    // each tab (BrowserLayout). This adapts them to and from the control's ColumnLayout.
    private ColumnLayout CurrentLayout()
    {
        var visible = this._preferences.ListColumns;
        return new ColumnLayout(
            visible.Select(column => new ColumnState(column.ToString(), this.ViewModel.Layout.Width(column), true)).Concat(
                RegistryColumns.All.Where(column => !visible.Contains(column)).Select(column => new ColumnState(
                    column.ToString(), this.ViewModel.Layout.Width(column), false))).ToArray());
    }

    private void ApplyColumnLayout()
    {
        if (!this._publishingLayout && this.ViewModel is not null && this._preferences is not null)
        {
            this.Entries.Layout = this.CurrentLayout();
        }
    }

    private void UpdateSort()
    {
        this.Entries.SortColumnId = this.ViewModel.SortColumn.ToString();
        this.Entries.SortDescending = this.ViewModel.SortDescending;
    }

    private void Entries_LayoutChanged(object? sender, ColumnLayout layout)
    {
        this._publishingLayout = true;
        try
        {
            foreach (var state in layout.Columns)
            {
                if (RegistryColumns.TryParse(state.Id, out var column) && state.Width != this.ViewModel.Layout.Width(column))
                {
                    this.ViewModel.SetColumnWidth(column, state.Width);
                }
            }

            var visible = new List<RegistrySortColumn>();
            foreach (var state in layout.Visible)
            {
                if (RegistryColumns.TryParse(state.Id, out var column))
                {
                    visible.Add(column);
                }
            }

            this._preferences.SetListColumns(visible);
        }
        finally
        {
            this._publishingLayout = false;
        }
    }

    private void Entries_SortRequested(object? sender, string id)
    {
        if (RegistryColumns.TryParse(id, out var column) && this.ViewModel.SortCommand.CanExecute(column))
        {
            this.ViewModel.SortCommand.Execute(column);
        }
    }

    private void Entries_VisibleItemsChanged(object? sender, EventArgs args)
    {
        if (this.ViewModel is not null
            && this._preferences is not null
            && this._preferences.ListColumns.Any(RegistryColumns.NeedsKeyDetails))
        {
            _ = this.ViewModel.LoadVisibleDetailsAsync(this.Entries.VisibleItems.OfType<RegistryEntry>());
        }
    }

    private void DataCell_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        this.ApplyDataFont((TextBlock)sender, args.NewValue);
    }

    // The configured data font applies to value data only, not to key or parent rows.
    private void ApplyDataFont(TextBlock cell, object? item)
    {
        if (this._dataFont is { } font && item is RegistryEntry { IsKey: false, IsParent: false })
        {
            cell.FontFamily = this._dataFamily;
            cell.FontSize = font.Size;
        }
        else
        {
            cell.ClearValue(TextBlock.FontFamilyProperty);
            cell.ClearValue(TextBlock.FontSizeProperty);
        }
    }

    internal void ApplySettings(RegistryFont? dataFont, bool showPreview)
    {
        ListDensity.SetSpacing(this.EntriesList, this._preferences.ListRowPadding);
        this._dataFont = dataFont;
        this._dataFamily = dataFont is null ? null : new FontFamily(dataFont.Family);
        this.ViewModel.SetListColumns(this._preferences.ListColumns);
        this.ApplyColumnLayout();
        if (this.Entries.FindColumn(nameof(RegistrySortColumn.Data)) is { } data)
        {
            foreach (var row in this.Entries.RealizedRows)
            {
                if (row.CellFor(data) is TextBlock cell)
                {
                    this.ApplyDataFont(cell, row.DataContext);
                }
            }
        }

        this.Entries_VisibleItemsChanged(this, EventArgs.Empty);
        this.SetPreview(showPreview);
    }

    private void SetPreview(bool show)
    {
        this._showPreview = show;
        if (!show || this.ViewModel.IsInspectorOpen)
        {
            this.PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        this.PreviewPanel.IsHitTestVisible = this.ViewModel.IsPreviewCurrent;
        if (!this.ViewModel.IsPreviewCurrent)
        {
            return;
        }

        var entry = this.ViewModel.SelectedEntries.Count == 1 ? this.ViewModel.SelectedEntry : null;
        if (entry is not { IsKey: false, IsParent: false })
        {
            this.PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        this.PreviewPanel.Visibility = Visibility.Visible;
        this.PreviewName.Text = entry.NamePreview;
        this.PreviewData.Text = entry.DataPreview;
    }

    internal void FocusList(FocusState state)
    {
        this.EntriesList.Focus(state);
    }

    internal void SelectEntry(RegistryEntry? entry)
    {
        this.EntriesList.SelectedItem = entry;
        if (entry is not null)
        {
            this.EntriesList.ScrollIntoView(entry);
        }
    }

    internal void SelectContextItem(DependencyObject? target)
    {
        if (target is ListViewItem { Content: RegistryEntry item } && !this.EntriesList.SelectedItems.Contains(item))
        {
            this.SelectEntry(item);
        }
    }

    private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (this._syncingSelection || this.ViewModel.IsUpdatingValue)
        {
            return;
        }

        this._syncingSelection = true;
        try
        {
            this.ViewModel.SetSelection(this.EntriesList.SelectedItems.OfType<RegistryEntry>());
        }
        finally
        {
            this._syncingSelection = false;
        }

        this.SetPreview(this._showPreview);
    }

    private void Entries_EditCommitting(object? sender, ColumnEditCommittingEventArgs args)
    {
        if (args.Item is RegistryEntry entry && this.ViewModel.RenameEntryError(entry, args.NewText) is { } error)
        {
            args.Cancel = true;
            args.Error = error;
            this.RenameRejected?.Invoke(error);
        }
    }

    private async void Entries_EditEnded(object? sender, ColumnEditEndedEventArgs args)
    {
        if (args is { Committed: true, Item: RegistryEntry entry })
        {
            await this.KeepPlaceAsync(() => this.ViewModel.RenameEntryAsync(entry, args.Text));
        }
    }

    /// <summary>
    ///     Runs an edit that reloads the list, then puts the user back where they were: the same scroll position, the
    ///     selected row brought into view if it moved (a rename can re-sort it), and keyboard focus on that row.
    /// </summary>
    internal async Task KeepPlaceAsync(Func<Task> edit)
    {
        var scroll = ResultListViewport.FindScrollViewer(this.EntriesList);
        var offset = scroll?.VerticalOffset ?? 0;
        var location = this.ViewModel.Location;
        var navigation = this.ViewModel.NavigationVersion;
        await edit();
        if (!this.IsLoaded || this.ViewModel.Location != location || this.ViewModel.NavigationVersion != navigation)
        {
            return;
        }

        var list = this.EntriesList;
        list.UpdateLayout();
        scroll?.ChangeView(null, Math.Min(offset, scroll.ScrollableHeight), null, true);
        list.UpdateLayout();
        if (this.ViewModel.SelectedEntry is not { } selected)
        {
            list.Focus(FocusState.Keyboard);
            return;
        }

        list.ScrollIntoView(selected);
        list.UpdateLayout();
        if (list.ContainerFromItem(selected) is ListViewItem container)
        {
            container.Focus(FocusState.Keyboard);
        }
        else
        {
            list.Focus(FocusState.Keyboard);
        }
    }

    // F2 renames in place when the row allows it, otherwise (several rows, list hidden) through the dialog.
    private bool BeginInlineRename()
    {
        return this.ViewModel.SelectedEntries.Count == 1
               && this.ViewModel.SelectedEntry is { } entry
               && this.Entries.BeginEdit(entry, nameof(RegistrySortColumn.Name));
    }

    private void Entries_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (this.Entries.IsEditing)
        {
            return;
        }

        for (var source = args.OriginalSource as DependencyObject;
             source is not null && source != this.EntriesList;
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is ListViewItem)
            {
                var command = this.ViewModel.ActivateEntryCommand;
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }

                return;
            }
        }
    }

    private async void Entries_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        // The inline editor lives inside the list; its keys (Enter, Delete, Ctrl+C) belong to the text box.
        if (this.Entries.IsEditing)
        {
            return;
        }

        if (args.Key == VirtualKey.F2 && this.BeginInlineRename())
        {
            args.Handled = true;
            return;
        }

        var plainControl = RegistryKeyboardShortcuts.IsPlainControl(this.XamlRoot);
        if (plainControl && args.Key is VirtualKey.C or VirtualKey.V)
        {
            args.Handled = true;
            if (args.Key == VirtualKey.C)
            {
                if (this.ViewModel.CopyCommand.CanExecute(null))
                {
                    this.ViewModel.CopyCommand.Execute(null);
                }
            }
            else if (this.ViewModel.PasteCommand.CanExecute(null))
            {
                await this.ViewModel.PasteCommand.ExecuteAsync(null);
            }

            return;
        }

        var command = args.Key switch
        {
            VirtualKey.I when plainControl => this.ViewModel.InspectCommand,
            VirtualKey.Enter => this.ViewModel.ActivateEntryCommand,
            VirtualKey.Delete => this.ViewModel.DeleteCommand,
            VirtualKey.F2 => this.ViewModel.RenameCommand,
            _ => null
        };
        if (command is not null)
        {
            args.Handled = true;
            if (args.Key == VirtualKey.F2)
            {
                if (command.CanExecute(null))
                {
                    await this.KeepPlaceAsync(() => command.ExecuteAsync(null));
                }

                return;
            }

            if (command.CanExecute(null))
            {
                var listEdit = args.Key is VirtualKey.Delete;
                await command.ExecuteAsync(null);
                if (listEdit && this.IsLoaded && this.ViewModel.SelectedEntries.Count == 0)
                {
                    this.EntriesList.Focus(FocusState.Keyboard);
                }
            }
        }
    }

    private void Entries_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(this.EntriesList).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        for (var source = args.OriginalSource as DependencyObject;
             source is not null && source != this.EntriesList;
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is ListViewItem or ScrollBar)
            {
                return;
            }
        }

        args.Handled = this.EntriesList.Focus(FocusState.Pointer);
    }

    private void Entries_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (char.IsControl(args.Character)
            || char.IsWhiteSpace(args.Character)
            || FocusManager.GetFocusedElement(this.XamlRoot) is TextBox
            || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0
            || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0)
        {
            return;
        }

        args.Handled = true;
        this.FilterTyped?.Invoke(args.Character);
    }
}
