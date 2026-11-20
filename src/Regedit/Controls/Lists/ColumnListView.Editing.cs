// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace JPSoftworks.Regedit.Controls.Lists;

// Inline editing in the style of the Win32 list view: F2 or a slow second click on an already selected row opens a
// text box over the cell; Enter or leaving the box commits, Escape cancels. The host validates in EditCommitting.
public sealed partial class ColumnListView
{
    public event EventHandler<ColumnEditCommittingEventArgs>? EditCommitting;

    public event EventHandler<ColumnEditEndedEventArgs>? EditEnded;

    /// <summary>Matches the default Windows double-click time; a second click sooner than this is a double-click.</summary>
    internal static readonly TimeSpan SlowClickDelay = TimeSpan.FromMilliseconds(500);

    private EditSession? _edit;
    private DispatcherQueueTimer? _slowClickTimer;
    private DateTime _lastSelectionChange = DateTime.MinValue;
    private ScrollViewer? _itemsScroll;

    public bool EditOnF2 { get; set; } = true;

    public bool EditOnSlowClick { get; set; } = true;

    /// <summary>
    ///     Optional veto for editing an item in a column (item, column id); F2, slow click and
    ///     <see cref="BeginEdit" /> all respect it. Use it for read-only rows.
    /// </summary>
    public Func<object, string, bool>? CanEditItem { get; set; }

    public bool IsEditing => this._edit is not null;

    public object? EditingItem => this._edit?.Item;

    internal TextBox? Editor => this._edit?.Box;

    /// <summary>
    ///     Opens the inline editor for <paramref name="item" /> in <paramref name="columnId" /> (default: the first
    ///     visible editable column), scrolling it into view. Returns false when the column is not editable or visible.
    /// </summary>
    public bool BeginEdit(object item, string? columnId = null)
    {
        this.StopSlowClick();
        if (this._edit is not null && !this.CommitEdit())
        {
            return false;
        }

        var column = columnId is null
            ? this.Columns.FirstOrDefault(candidate => candidate.IsEditable && this.IsColumnVisible(candidate.Id))
            : this.FindColumn(columnId);
        if (!this.ShowColumns
            || column is not { IsEditable: true }
            || !this.IsColumnVisible(column.Id)
            || this.CanEditItem?.Invoke(item, column.Id) == false)
        {
            return false;
        }

        var row = this.RowFor(item);
        if (row is null)
        {
            this.Items.ScrollIntoView(item);
            this.Items.UpdateLayout();
            row = this.RowFor(item);
        }

        if (row is null)
        {
            return false;
        }

        var text = column.TextForEdit(item);
        var box = row.ShowEditor(column, text);
        this._edit = new EditSession(item, column, row, box, text);
        box.KeyDown += this.EditorKeyDown;
        box.LostFocus += this.EditorLostFocus;
        box.Loaded += (_, _) =>
        {
            if (ReferenceEquals(this._edit?.Box, box))
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        };
        return true;
    }

    /// <summary>
    ///     Confirms the open edit. Returns false when the host rejected the text; the editor then stays open.
    /// </summary>
    public bool CommitEdit(bool refocusOnRejection = true)
    {
        if (this._edit is not { } edit)
        {
            return true;
        }

        var text = edit.Box.Text;
        if (text == edit.Original)
        {
            this.FinishEdit(edit, false);
            return true;
        }

        var args = new ColumnEditCommittingEventArgs(edit.Item, edit.Column.Id, edit.Original, text);
        this.EditCommitting?.Invoke(this, args);
        if (args.Cancel)
        {
            ShowError(edit.Box, args.Error);
            if (refocusOnRejection)
            {
                edit.Box.Focus(FocusState.Programmatic);
                edit.Box.SelectAll();
            }

            return false;
        }

        this.FinishEdit(edit, true);
        return true;
    }

    public void CancelEdit()
    {
        if (this._edit is { } edit)
        {
            this.FinishEdit(edit, false);
        }
    }

    private void InitializeEditing()
    {
        this.Items.KeyDown += this.ItemsKeyDown;
        this.Items.Loaded += (_, _) =>
        {
            if (this._itemsScroll is null && FindScrollViewer(this.Items) is { } scroll)
            {
                this._itemsScroll = scroll;
                scroll.ViewChanged += this.ItemsViewChanged;
            }
        };
        this.Items.AddHandler(TappedEvent, new TappedEventHandler(this.ItemsTapped), true);
        this.Items.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler((_, _) => this.StopSlowClick()), true);
        this.Items.SelectionChanged += (_, _) =>
        {
            this._lastSelectionChange = DateTime.UtcNow;
            this.StopSlowClick();
        };
    }

    private ColumnRow? RowFor(object item)
    {
        if (this._rows.FirstOrDefault(row => Equals(row.DataContext, item)) is { } registered)
        {
            return registered;
        }

        // A row scrolled into view this frame is laid out before its Loaded event registers it.
        if (this.Items.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: ColumnRow row })
        {
            this.Register(row);
            return row;
        }

        return null;
    }

    private void FinishEdit(EditSession edit, bool committed)
    {
        this._edit = null;
        edit.Box.KeyDown -= this.EditorKeyDown;
        edit.Box.LostFocus -= this.EditorLostFocus;
        var hadFocus = edit.Box.FocusState != FocusState.Unfocused;
        edit.Row.HideEditor();
        if (committed)
        {
            edit.Row.RefreshText();
        }

        if (hadFocus && this.Items.ContainerFromItem(edit.Item) is Control container)
        {
            container.Focus(FocusState.Programmatic);
        }

        this.EditEnded?.Invoke(this, new ColumnEditEndedEventArgs(edit.Item, edit.Column.Id, committed, edit.Box.Text));
    }

    // Called when layout or recycling makes the open editor invalid.
    private void AbandonEditIf(Func<EditSession, bool> stale)
    {
        if (this._edit is { } edit && stale(edit))
        {
            this.FinishEdit(edit, false);
        }
    }

    private void EditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Enter:
                this.CommitEdit();
                args.Handled = true;
                break;
            case VirtualKey.Escape:
                this.CancelEdit();
                args.Handled = true;
                break;
            default:
                // Keep list keyboard handling (type-ahead, selection keys) away from the text box.
                args.Handled = args.Key is not VirtualKey.Tab;
                break;
        }
    }

    private void EditorLostFocus(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(this._edit?.Box, sender))
        {
            return;
        }

        // Leaving the box commits like the Win32 control; rejected text is abandoned instead of trapping focus.
        if (!this.CommitEdit(refocusOnRejection: false))
        {
            this.CancelEdit();
        }
    }

    private void ItemsKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.F2 || !this.EditOnF2 || this._edit is not null)
        {
            return;
        }

        var item = FocusManager.GetFocusedElement(this.XamlRoot) is ListViewItem focused
            ? this.Items.ItemFromContainer(focused)
            : this.Items.SelectedItem;
        if (item is not null && this.BeginEdit(item))
        {
            args.Handled = true;
        }
    }

    private void ItemsTapped(object sender, TappedRoutedEventArgs args)
    {
        this.StopSlowClick();
        if (!this.EditOnSlowClick
            || this._edit is not null
            || this.Items.SelectedItems.Count != 1
            || DateTime.UtcNow - this._lastSelectionChange < SlowClickDelay
            || ModifierDown())
        {
            return;
        }

        var row = FindRow(args.OriginalSource as DependencyObject);
        var item = row?.DataContext;
        if (row is null
            || item is null
            || !Equals(this.Items.SelectedItem, item)
            || row.ColumnAt(args.GetPosition(row).X) is not { IsEditable: true } column)
        {
            return;
        }

        this._slowClickTimer ??= this.DispatcherQueue.CreateTimer();
        this._slowClickTimer.Interval = SlowClickDelay;
        this._slowClickTimer.IsRepeating = false;
        this._slowClickTimer.Tick += Fire;
        this._slowClickTimer.Start();

        void Fire(DispatcherQueueTimer timer, object state)
        {
            timer.Tick -= Fire;
            if (Equals(this.Items.SelectedItem, item))
            {
                this.BeginEdit(item, column.Id);
            }
        }
    }

    private void StopSlowClick()
    {
        if (this._slowClickTimer is { IsRunning: true } timer)
        {
            timer.Stop();
        }

        // Ticks are attached per click; recreate the timer so no stale handler survives.
        this._slowClickTimer = null;
    }

    // Like the Win32 control, scrolling the edited row out of view dismisses the edit (commit, or cancel if rejected).
    private void ItemsViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        this.QueueVisibleChanged();
        if (this._edit is not { } edit || this._itemsScroll is not { } scroll)
        {
            return;
        }

        try
        {
            var top = edit.Row.TransformToVisual(scroll).TransformPoint(default).Y;
            if (top + edit.Row.ActualHeight > 0 && top < scroll.ViewportHeight)
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            // Recycling can detach the row before its Unloaded event runs.
        }

        if (!this.CommitEdit(refocusOnRejection: false))
        {
            this.CancelEdit();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static ColumnRow? FindRow(DependencyObject? element)
    {
        for (; element is not null and not Microsoft.UI.Xaml.Controls.ListView; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ColumnRow row)
            {
                return row;
            }

            if (element is TextBox)
            {
                return null;
            }
        }

        return null;
    }

    private static bool ModifierDown()
    {
        return Down(VirtualKey.Control) || Down(VirtualKey.Shift) || Down(VirtualKey.Menu);

        static bool Down(VirtualKey key)
        {
            return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        }
    }

    private static void ShowError(TextBox box, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return;
        }

        var tip = new ToolTip { Content = error, Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom };
        ToolTipService.SetToolTip(box, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(box, error);
        tip.IsOpen = true;
    }

    private sealed record EditSession(object Item, ListColumn Column, ColumnRow Row, TextBox Box, string Original);
}
