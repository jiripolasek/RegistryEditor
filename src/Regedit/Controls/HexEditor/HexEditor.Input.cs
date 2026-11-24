// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace JPSoftworks.Controls;

public partial class HexEditor
{
    private const int MaxClipboardBytes = 1024 * 1024;
    private uint? _dragPointer;
    private double _panRemainder;
    private object? _offsetRequest;
    private long? _menuOffset;

    /// <summary>Optional host prompt. Receives the current and last valid offsets. Return null to cancel.</summary>
    public Func<long, long, Task<long?>>? OffsetPromptAsync { get; set; }

    /// <summary>Optionally prepares text pasted in the hex pane. Return hexadecimal text to paste or null to cancel.</summary>
    public Func<string, Task<string?>>? PrepareHexPasteAsync { get; set; }

    /// <summary>Optional decoder for additional hex clipboard formats, invoked after the paste prompt.</summary>
    public HexPasteReader? ReadCustomHexPaste { get; set; }

    private static bool IsDown(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void Editor_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        // AltGr layouts deliver printable characters with both Ctrl and Alt down.
        if (this.OffsetPanel.Visibility == Visibility.Visible || ctrl != alt)
        {
            return;
        }

        try
        {
            if (this._buffer.Type(args.Character, this._ascii))
            {
                args.Handled = true;
                this.PublishEdit();
            }
        }
        catch (Exception ex) when (IsSourceError(ex) || ex is OverflowException)
        {
            this.ShowStatus(ex.Message);
        }
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (this.OffsetPanel.Visibility == Visibility.Visible || IsDown(VirtualKey.Menu))
        {
            return;
        }

        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var caret = this._buffer.Caret;
        var row = caret - (caret % HexEditorRow.BytesPerRow);
        switch (args.Key)
        {
            case VirtualKey.F6 when !ctrl:
                this._ascii = !this._ascii;
                this._buffer.MoveTo(caret, extend: true);
                break;
            case VirtualKey.A when ctrl:
                this._buffer.Select(0, this._buffer.Length);
                break;
            case VirtualKey.C when ctrl && shift:
                this.CopyOffset(this._buffer.Caret);
                args.Handled = true;
                return;
            case VirtualKey.C when ctrl:
                this.Copy(this._ascii);
                args.Handled = true;
                return;
            case VirtualKey.V when ctrl:
                _ = this.PasteAsync(overwrite: shift);
                args.Handled = true;
                return;
            case VirtualKey.Z or VirtualKey.Y when ctrl:
                this.ReplayHistory(redo: args.Key == VirtualKey.Y || shift);
                args.Handled = true;
                return;
            case VirtualKey.G when ctrl:
                _ = this.GoToOffsetAsync();
                args.Handled = true;
                return;
            case VirtualKey.Escape when this._offsetTip.IsOpen:
                this.ClearHover();
                args.Handled = true;
                return;
            case VirtualKey.Back or VirtualKey.Delete when !ctrl:
                if (this._buffer.Delete(args.Key == VirtualKey.Back))
                {
                    this.PublishEdit();
                }

                args.Handled = true;
                return;
            case VirtualKey.Home:
                this._buffer.MoveTo(ctrl ? 0 : row, extend: shift);
                break;
            case VirtualKey.End:
                this._buffer.MoveTo(ctrl ? this._buffer.LastPosition : row + HexEditorRow.BytesPerRow - 1, extend: shift);
                break;
            default:
                var page = (long)this._visibleRows * HexEditorRow.BytesPerRow;
                var delta = args.Key switch
                {
                    VirtualKey.Left => -1,
                    VirtualKey.Right => 1,
                    VirtualKey.Up => -HexEditorRow.BytesPerRow,
                    VirtualKey.Down => HexEditorRow.BytesPerRow,
                    VirtualKey.PageUp => -page,
                    VirtualKey.PageDown => page,
                    _ => 0L
                };
                if (ctrl || delta == 0)
                {
                    return;
                }

                this._buffer.MoveBy(delta, extend: shift);
                break;
        }

        args.Handled = true;
        this.Refresh();
        this.RevealColumn();
    }

    private void GoTo_Click(object sender, RoutedEventArgs args) => _ = this.GoToOffsetAsync();

    /// <summary>Opens the host's offset prompt or the built-in hexadecimal input.</summary>
    public async Task GoToOffsetAsync()
    {
        if (!this.IsLoaded || !this.IsEnabled || this.RowCount == 0 || this._offsetRequest is not null)
        {
            return;
        }

        this.ClearHover();
        if (this.OffsetPromptAsync is not { } prompt)
        {
            this.OffsetPanel.Visibility = Visibility.Visible;
            this.OffsetBox.Text = this._buffer.Caret.ToString("X", CultureInfo.InvariantCulture);
            this.OffsetBox.Focus(FocusState.Keyboard);
            this.OffsetBox.SelectAll();
            this.RequestSelectionLayout();
            return;
        }

        var request = this._offsetRequest = new object();
        try
        {
            var offset = await prompt(this._buffer.Caret, this._buffer.LastPosition);
            if (this._offsetRequest == request && this.IsEnabled && offset.HasValue)
            {
                this.ApplyOffset(offset.Value);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (this._offsetRequest == request)
            {
                this.ShowStatus(ex.Message);
            }
        }
        finally
        {
            if (this._offsetRequest == request)
            {
                this._offsetRequest = null;
            }
        }
    }

    private void OffsetPanel_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            this.CloseOffsetPanel();
        }
        else if (args.Key == VirtualKey.Enter && this.OffsetBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = true;
            this.SubmitOffset();
        }
    }

    private void SubmitOffset_Click(object sender, RoutedEventArgs args) => this.SubmitOffset();

    private void CloseOffset_Click(object sender, RoutedEventArgs args) => this.CloseOffsetPanel();

    private void SubmitOffset()
    {
        var text = this.OffsetBox.Text.AsSpan().Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        this.ApplyOffset(long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset) ? offset : -1);
    }

    private void ApplyOffset(long offset)
    {
        if (offset < 0 || offset > this._buffer.LastPosition || this.RowCount == 0)
        {
            this.ShowStatus($"Enter a hexadecimal offset from 0 to {this._buffer.LastPosition:X}.");
            return;
        }

        this._buffer.MoveTo(offset);
        this.CloseOffsetPanel();
    }

    private void ResetOffsetPrompt()
    {
        this._offsetRequest = null;
        this.OffsetPanel.Visibility = Visibility.Collapsed;
    }

    private void CloseOffsetPanel()
    {
        this.ResetOffsetPrompt();
        this.Focus(FocusState.Programmatic);
        this.Refresh();
        this.RevealColumn();
    }

    private void RevealColumn()
    {
        this._revealColumnAfterLayout = true;
        this.RequestSelectionLayout();
    }

    private void BringActiveCharacterIntoView()
    {
        if (this.Viewport.ScrollableWidth > 0 && this.GetCharacterBounds(this._buffer.Caret) is { } cell)
        {
            var width = Math.Max(this._caretSettings.CaretWidth, Math.Ceiling(this.CellWidth));
            var offset = this.Viewport.HorizontalOffset;
            var left = cell.Row.TransformToVisual(this.Viewport).TransformBounds(cell.Bounds).X + offset;
            if (!this._ascii && this._buffer.Caret < this._buffer.Length && this.Viewport.ViewportWidth >= 2 * width)
            {
                if (this._buffer.LowNibble)
                {
                    left -= this.CellWidth;
                }

                width += this.CellWidth;
            }

            if (left < offset || left + width > offset + this.Viewport.ViewportWidth)
            {
                var target = left < offset ? left : Math.Min(left, left + width - this.Viewport.ViewportWidth);
                this.Viewport.ChangeView(target, null, null, disableAnimation: true);
            }
        }
    }

    private (long Offset, bool Ascii)? HitTestByte(Point point, bool exact)
    {
        if (point.Y < 0 || point.Y >= this.Rows.ActualHeight || point.X < 0)
        {
            return null;
        }

        var index = (int)(point.Y / this.RowHeight);
        if (index >= this.Rows.Children.Count || this.Rows.Children[index] is not RichTextBlock row || row.Tag is not long start)
        {
            return null;
        }

        var local = this.Rows.TransformToVisual(row).TransformPoint(point);
        var cell = this.CellWidth;
        if (cell <= 0 || local.X < 0 || local.X >= cell * this.ColumnRuler.Text.Length)
        {
            return null;
        }

        var character = (int)(local.X / cell);
        if (!HexEditorRow.TryGetColumn(character, this._digits, out var column, out var ascii)
            || (exact && !ascii && (character - HexEditorRow.HexStart(this._digits)) % 3 == 2))
        {
            return null;
        }

        var offset = start + column;
        return (exact ? offset < this._buffer.Length : offset <= this._buffer.LastPosition) ? (offset, ascii) : null;
    }

    private bool SelectAt(Point point, bool extend)
    {
        if (this.HitTestByte(point, exact: false) is not { } hit)
        {
            return false;
        }

        this._ascii = hit.Ascii;
        this._buffer.MoveTo(hit.Offset, extend);
        this.Focus(FocusState.Pointer);
        this.Refresh();
        return true;
    }

    private void Rows_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse
            && args.GetCurrentPoint(this.Rows).Properties.IsLeftButtonPressed
            && this.SelectAt(args.GetCurrentPoint(this.Rows).Position, IsDown(VirtualKey.Shift)))
        {
            if (this.Rows.CapturePointer(args.Pointer))
            {
                this._dragPointer = args.Pointer.PointerId;
            }

            args.Handled = true;
        }
    }

    private void Rows_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (this._dragPointer == args.Pointer.PointerId)
        {
            var y = args.GetCurrentPoint(this.Rows).Position.Y;
            if (y < 0 || y >= this.Rows.ActualHeight)
            {
                this.ScrollRows(y < 0 ? -1 : 1);
                var edge = y < 0 ? this._firstRow : this._firstRow + this.Rows.Children.Count - 1;
                this._buffer.MoveTo((edge * HexEditorRow.BytesPerRow) + (this._buffer.Caret % HexEditorRow.BytesPerRow), extend: true);
                this.Refresh();
            }
            else
            {
                this.SelectAt(args.GetCurrentPoint(this.Rows).Position, extend: true);
            }
        }

        if (args.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            this._hoverPoint = this.Rows.TransformToVisual(this.Viewport).TransformPoint(args.GetCurrentPoint(this.Rows).Position);
            this.UpdateHover();
        }
    }

    private void Rows_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (args.PointerDeviceType != PointerDeviceType.Mouse)
        {
            this.SelectAt(args.GetPosition(this.Rows), extend: false);
        }
    }

    private void Editor_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsHorizontalMouseWheel && !IsDown(VirtualKey.Shift) && this.MaxFirstRow > 0)
        {
            this.Pan(-point.Properties.MouseWheelDelta / 120.0 * 3 * this.RowHeight);
            args.Handled = true;
        }
    }

    private void Pan(double distance)
    {
        this._panRemainder += distance;
        var height = this.RowHeight;
        var rows = (long)(this._panRemainder / height);
        if (rows != 0)
        {
            this._panRemainder -= rows * height;
            this.ScrollRows(rows);
        }
    }

    private void Menu_Opening(object sender, object args)
    {
        this._menuOpen = true;
        this.ClearHover();
        this.UndoItem.Visibility = this.RedoItem.Visibility = this.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
        this.UndoItem.IsEnabled = this._buffer.CanReplayHistory(redo: false);
        this.RedoItem.IsEnabled = this._buffer.CanReplayHistory(redo: true);
        this.CopyOffsetItem.IsEnabled = (this._menuOffset ?? this._buffer.Caret) < this._buffer.Length;
        this.CopyHexItem.IsEnabled = this.CopyAsciiItem.IsEnabled = this.SelectionLength > 0;
        this.PasteItem.Visibility = this.PasteOverwriteItem.Visibility = this._buffer.CanWrite ? Visibility.Visible : Visibility.Collapsed;
        this.PasteOverwriteItem.IsEnabled = this.SelectionLength > 0;
    }

    private void Menu_Closed(object sender, object args)
    {
        this._menuOpen = false;
        this._menuOffset = null;
    }

    private void CopyOffset_Click(object sender, RoutedEventArgs args) => this.CopyOffset(this._menuOffset ?? this._buffer.Caret);

    private void CopyOffset(long offset)
    {
        if (offset < this._buffer.Length)
        {
            this.CopyText(this.FormatOffset(offset));
        }
    }

    private string FormatOffset(long offset) => "0x" + offset.ToString(this._digits == 8 ? "X8" : "X16", CultureInfo.InvariantCulture);

    private void CopyHex_Click(object sender, RoutedEventArgs args) => this.Copy(false);

    private void CopyAscii_Click(object sender, RoutedEventArgs args) => this.Copy(true);

    private void Paste_Click(object sender, RoutedEventArgs args) => _ = this.PasteAsync();

    private void PasteOverwrite_Click(object sender, RoutedEventArgs args) => _ = this.PasteAsync(overwrite: true);

    private void Undo_Click(object sender, RoutedEventArgs args) => this.ReplayHistory(redo: false);

    private void Redo_Click(object sender, RoutedEventArgs args) => this.ReplayHistory(redo: true);

    private void ReplayHistory(bool redo)
    {
        if (this._buffer.ReplayHistory(redo))
        {
            this.PublishEdit();
        }
    }

    private void Copy(bool ascii)
    {
        if (this.Document is null || this.SelectionLength == 0)
        {
            return;
        }

        if (this.SelectionLength > MaxClipboardBytes)
        {
            this.ShowStatus("Select up to 1 MiB to copy to the text clipboard.");
            return;
        }

        try
        {
            var bytes = new byte[(int)this.SelectionLength];
            this.Document.Read(this.SelectionStart, bytes);
            var text = ascii
                ? string.Create(
                    bytes.Length, bytes, static (chars, values) =>
                    {
                        for (var i = 0; i < chars.Length; i++)
                        {
                            chars[i] = HexEditorRow.Printable(values[i]);
                        }
                    })
                : BitConverter.ToString(bytes).Replace('-', ' ');
            this.CopyText(text);
        }
        catch (Exception ex) when (IsSourceError(ex))
        {
            this.ShowStatus(ex.Message);
        }
    }

    private void CopyText(string text)
    {
        try
        {
            var content = new DataPackage();
            content.SetText(text);
            Clipboard.SetContent(content);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            this.ShowStatus(ex.Message);
        }
    }

    /// <summary>Pastes clipboard text in the active pane. Overwrite starts at the first selected byte and preserves the document length.</summary>
    public async Task PasteAsync(bool overwrite = false)
    {
        var document = this.Document;
        if (!this.IsLoaded || !this.IsEnabled || document is null || !this._buffer.CanWrite)
        {
            return;
        }

        var revision = document.Revision;
        var canResize = this._buffer.CanResize;
        var selection = CurrentSelection();

        (long, long, long, bool, bool, bool) CurrentSelection() =>
            (this._buffer.Caret, this.SelectionStart, this.SelectionLength, this._buffer.HasSelection, this._buffer.LowNibble, this._ascii);

        bool CanApply() => this.IsLoaded
                           && this.IsEnabled
                           && this._buffer.CanWrite
                           && canResize == this._buffer.CanResize
                           && document == this.Document
                           && document.Revision == revision
                           && selection == CurrentSelection();

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = await content.GetTextAsync();
            if (!CanApply())
            {
                return;
            }

            if (!this._ascii && this.PrepareHexPasteAsync is { } prepare)
            {
                string? prepared;
                try
                {
                    prepared = await prepare(text);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (CanApply())
                    {
                        this.ShowStatus(ex.Message);
                    }

                    return;
                }

                if (prepared is null || !CanApply())
                {
                    return;
                }

                text = prepared;
            }

            if (this._buffer.TryPaste(text, this._ascii, out var message, overwrite, this.ReadCustomHexPaste))
            {
                this.PublishEdit();
            }

            if (message is not null)
            {
                this.ShowStatus(message);
            }
        }
        catch (Exception ex) when (IsSourceError(ex) || ex is System.Runtime.InteropServices.COMException or OverflowException)
        {
            this.ShowStatus(ex.Message);
        }
    }

    private sealed partial class HexEditorAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
    {
        private readonly HexEditor _editor;

        internal HexEditorAutomationPeer(HexEditor editor)
            : base(editor)
        {
            this._editor = editor;
        }

        public bool IsReadOnly => !this._editor._buffer.CanWriteByte(this._editor._buffer.Caret);

        public string Value => this._editor.CurrentByte();

        public void SetValue(string value)
        {
            if (!this._editor.IsEnabled || this.IsReadOnly)
            {
                throw new InvalidOperationException("The byte is read-only.");
            }

            if (value.Length != 2 || !byte.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new ArgumentException("Enter two hexadecimal digits.", nameof(value));
            }

            var buffer = this._editor._buffer;
            var offset = buffer.Caret;
            buffer.Document!.Replace(offset, offset < buffer.Length ? 1 : 0, [parsed]);
            buffer.MoveTo(offset);
            this._editor.PublishEdit();
        }

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        protected override string GetClassNameCore() => nameof(HexEditor);

        protected override string GetLocalizedControlTypeCore() => this._editor.IsReadOnly ? "hex viewer" : "hex editor";
    }
}
