// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

namespace JPSoftworks.Controls;

/// <summary>A keyboard-accessible editor for a seekable byte document.</summary>
public partial class HexEditor : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(HexDocument), typeof(HexEditor), new PropertyMetadata(null, OnDocumentChanged));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(HexEditor), new PropertyMetadata(false, OnEditOptionsChanged));

    public static readonly DependencyProperty IsFixedLengthProperty = DependencyProperty.Register(
        nameof(IsFixedLength), typeof(bool), typeof(HexEditor), new PropertyMetadata(false, OnEditOptionsChanged));

    public static readonly DependencyProperty MaxLengthProperty = DependencyProperty.Register(
        nameof(MaxLength), typeof(long), typeof(HexEditor), new PropertyMetadata(0L, OnMaxLengthChanged));

    private readonly HexEditorBuffer _buffer = new();
    private long _firstRow;
    private int _visibleRows = 1;
    private int _digits = 8;
    private bool _settingScroll;
    private bool _ascii;
    private string _automationValue = string.Empty;
    private bool _automationReadOnly = true;

    /// <summary>The editable draft. The caller owns its backing stream.</summary>
    public HexDocument? Document
    {
        get => (HexDocument?)this.GetValue(DocumentProperty);
        set => this.SetValue(DocumentProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)this.GetValue(IsReadOnlyProperty);
        set => this.SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Restricts this view to overwriting existing bytes.</summary>
    public bool IsFixedLength
    {
        get => (bool)this.GetValue(IsFixedLengthProperty);
        set => this.SetValue(IsFixedLengthProperty, value);
    }

    /// <summary>Maximum byte count for user input. Zero allows unrestricted growth.</summary>
    public long MaxLength
    {
        get => (long)this.GetValue(MaxLengthProperty);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this.SetValue(MaxLengthProperty, value);
        }
    }

    public long SelectionStart => this._buffer.SelectionStart;

    public long SelectionLength => this._buffer.SelectionLength;

    /// <summary>Raised after a user edit has been applied to the draft.</summary>
    public event EventHandler? Edited;

    public HexEditor()
    {
        this.InitializeComponent();
        this.InitializeColumns();
        this.InitializeHover();
        this.CharacterReceived += this.Editor_CharacterReceived;
        this.KeyDown += this.Editor_KeyDown;
        this.PointerEntered += (_, _) => { this._pointerOver = true; this.UpdateFieldState(); };
        this.PointerExited += (_, _) => { this._pointerOver = false; this.UpdateFieldState(); };
        this.GotFocus += (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, this) && this.OffsetPanel.Visibility == Visibility.Visible)
            {
                this.ResetOffsetPrompt();
            }

            this._focusWithin = true;
            this.UpdateFieldState();
        };
        this.LostFocus += (_, _) => { this._focusWithin = false; this.UpdateFieldState(); };
        this.RegisterPropertyChangedCallback(CharacterSpacingProperty, (_, _) =>
        {
            foreach (RichTextBlock row in this.Rows.Children)
            {
                row.CharacterSpacing = this.CharacterSpacing;
            }
            this.RequestSelectionLayout();
        });
        this.RegisterPropertyChangedCallback(PaddingProperty, (_, _) => this.UpdateViewport());
        this.RegisterPropertyChangedCallback(BorderThicknessProperty, (_, _) => this.UpdateFieldState());
        this.RegisterPropertyChangedCallback(FocusStateProperty, (_, _) => this.RequestSelectionLayout());
        this.IsEnabledChanged += (_, _) =>
        {
            this.UpdateFieldState();
            if (!this.IsEnabled)
            {
                this.ClearHover();
            }
            this.RequestSelectionLayout();
        };
        this.ActualThemeChanged += (_, _) => this.RequestSelectionLayout();
        this._caretBlink.Tick += (_, _) => this.EditCaret.Opacity = this.EditCaret.Opacity == 0 ? 1 : 0;
        this.Rows.PointerPressed += this.Rows_PointerPressed;
        this.Rows.AddHandler(PointerMovedEvent, new PointerEventHandler(this.Rows_PointerMoved), true);
        this.Rows.PointerReleased += (_, args) => this.Rows.ReleasePointerCapture(args.Pointer);
        this.Rows.PointerCaptureLost += (_, _) => this._dragPointer = null;
        this.Rows.Tapped += this.Rows_Tapped;
        this.Rows.ManipulationDelta += (_, args) => this.Pan(-args.Delta.Translation.Y);
        this.Rows.ManipulationStarted += (_, _) => this._panRemainder = 0;
        this.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(this.Editor_PointerWheelChanged), true);
        this.Loaded += (_, _) =>
        {
            this.UpdateFieldState();
            if (this.Document is { } document)
            {
                document.ViewEdited += this.OtherViewEdited;
            }

            this._buffer.SetEditOptions(this.IsReadOnly, this.IsFixedLength);
            this.Refresh(dataChanged: true);
            this.UpdateColumns();
            if (this._buffer.HasSelection)
            {
                this.RevealColumn();
            }
        };
        this.Unloaded += (_, _) =>
        {
            this._pointerOver = this._focusWithin = false;
            this.ResetOffsetPrompt();
            this.ClearHover();
            this.ClearSelectionVisuals();
            if (this.Document is { } document)
            {
                document.ViewEdited -= this.OtherViewEdited;
            }
        };
        this.Viewport.SizeChanged += (_, _) => this.UpdateViewport();
        this.Header.SizeChanged += (_, _) => this.UpdateViewport();
        this.Rows.SizeChanged += (_, _) =>
        {
            this.UpdateColumns();
            this.UpdateViewport();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HexEditorAutomationPeer(this);

    private long RowCount => this.Document is null || (!this._buffer.CanResize && this._buffer.Length == 0)
        ? 0 : (this._buffer.LastPosition / HexEditorRow.BytesPerRow) + 1;

    private long MaxFirstRow => Math.Max(0, this.RowCount - this._visibleRows);

    private double RowHeight => this.Rows.Children.Count > 0 && this.Rows.Children[0] is FrameworkElement { ActualHeight: > 0 } row
        ? row.ActualHeight : this.FontSize * 1.5;

    private static void OnDocumentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (HexEditor)sender;
        if (editor.IsLoaded)
        {
            if (args.OldValue is HexDocument oldDocument)
            {
                oldDocument.ViewEdited -= editor.OtherViewEdited;
            }

            if (args.NewValue is HexDocument newDocument)
            {
                newDocument.ViewEdited += editor.OtherViewEdited;
            }
        }

        editor.ResetOffsetPrompt();
        editor.ClearHover();
        editor._buffer.Load((HexDocument?)args.NewValue);
        editor._firstRow = 0;
        editor.Refresh(dataChanged: true);
    }

    private static void OnEditOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (HexEditor)sender;
        editor._buffer.SetEditOptions(editor.IsReadOnly, editor.IsFixedLength);
        editor.Refresh(dataChanged: true);
    }

    private static void OnMaxLengthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (HexEditor)sender;
        var length = (long)args.NewValue;
        if (length < 0)
        {
            editor.SetValue(MaxLengthProperty, args.OldValue);
            throw new ArgumentOutOfRangeException(nameof(MaxLength));
        }

        editor._buffer.MaxLength = length;
        if (editor.IsLoaded)
        {
            editor.UpdateStatus();
            editor.RequestSelectionLayout();
        }
    }

    /// <summary>Scrolls to an existing offset and selects up to the requested number of bytes.</summary>
    public void Highlight(long offset, long length)
    {
        if (offset < 0 || offset >= this._buffer.Length || length <= 0)
        {
            return;
        }

        this._buffer.Select(offset, length);
        this.Reveal(offset);
        this.Refresh(revealCaret: false);
        this.RevealColumn();
    }

    private void Reveal(long offset)
    {
        var row = offset / HexEditorRow.BytesPerRow;
        this._firstRow = Math.Clamp(this._firstRow, Math.Max(0, row - this._visibleRows + 1), row);
    }

    private void Refresh(bool dataChanged = false, bool revealCaret = true)
    {
        if (revealCaret)
        {
            this.Reveal(this._buffer.Caret);
        }

        if (this.UpdateViewport(dataChanged))
        {
            this.UpdateStatus();
        }
    }

    private bool UpdateViewport(bool dataChanged = false)
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        var dataHeight = this.Viewport.ViewportHeight - this.Padding.Top - this.Padding.Bottom
            - (this.ShowColumnHeaders ? this.Header.ActualHeight : 0);
        var caretRow = this._buffer.Caret / HexEditorRow.BytesPerRow;
        var caretWasVisible = caretRow >= this._firstRow && caretRow - this._firstRow < this._visibleRows;
        this._visibleRows = Math.Max(1, (int)(dataHeight / this.RowHeight));
        if (caretWasVisible)
        {
            this.Reveal(this._buffer.Caret);
        }

        this._firstRow = Math.Clamp(this._firstRow, 0, this.MaxFirstRow);
        this._settingScroll = true;
        try
        {
            this.RowScroll.Maximum = this.MaxFirstRow;
            this.RowScroll.ViewportSize = this.RowScroll.LargeChange = this._visibleRows;
            this.RowScroll.Value = this._firstRow;
            this.RowScroll.Visibility = this.MaxFirstRow > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            this._settingScroll = false;
        }

        var digits = this._buffer.LastPosition > uint.MaxValue ? 16 : 8;
        if (this._digits != digits || this.ColumnRuler.Text.Length == 0)
        {
            this._digits = digits;
            dataChanged = true;
            this.ColumnRuler.Text = new string('0', HexEditorRow.AsciiStart(digits) + HexEditorRow.BytesPerRow);
        }
        var count = (int)Math.Min(this._visibleRows, this.RowCount);
        this.EmptyMessage.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        while (this.Rows.Children.Count > count)
        {
            this.Rows.Children.RemoveAt(this.Rows.Children.Count - 1);
        }

        Span<byte> bytes = stackalloc byte[HexEditorRow.BytesPerRow];
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (i == this.Rows.Children.Count)
                {
                    this.Rows.Children.Add(this.CreateRow());
                }

                var row = (RichTextBlock)this.Rows.Children[i];
                var offset = (this._firstRow + i) * HexEditorRow.BytesPerRow;
                if (dataChanged || row.Tag is not long previous || previous != offset)
                {
                    var read = this.Document!.Read(offset, bytes);
                    RowText(row).Text = HexEditorRow.Format(bytes[..read], offset, digits);
                    row.Tag = offset;
                    var address = row.TextHighlighters[OffsetHighlight].Ranges;
                    address.Clear();
                    address.Add(new TextRange { StartIndex = 0, Length = digits });
                    this.UpdateBandText(row);
                }

                this.UpdateHighlight(row, offset);
            }

            this.RequestSelectionLayout();
            return true;
        }
        catch (Exception ex) when (IsSourceError(ex))
        {
            this.Rows.Children.Clear();
            this.ClearHover();
            this.ClearSelectionVisuals();
            this.ShowStatus(ex.Message);
            return false;
        }
    }

    private void UpdateHighlight(RichTextBlock row, long offset)
    {
        var active = row.TextHighlighters[ActiveHighlight].Ranges;
        var first = this.SelectionStart;
        var last = first + (this.SelectionLength - 1);
        var start = Math.Max(first, offset) - offset;
        var end = Math.Min(last - offset, HexEditorRow.BytesPerRow - 1);
        if (start > end && active.Count == 0)
        {
            return;
        }

        var inactive = row.TextHighlighters[InactiveHighlight].Ranges;
        active.Clear();
        inactive.Clear();
        // WinUI can keep painting the previous ranges until the row is arranged again.
        row.InvalidateArrange();
        if (start > end)
        {
            return;
        }

        var hex = new TextRange { StartIndex = HexEditorRow.HexStart(this._digits) + ((int)start * 3), Length = (int)((end - start) * 3) + 2 };
        var ascii = new TextRange { StartIndex = HexEditorRow.AsciiStart(this._digits) + (int)start, Length = (int)(end - start) + 1 };
        active.Add(this._ascii ? ascii : hex);
        inactive.Add(this._ascii ? hex : ascii);
    }

    internal static Run RowText(RichTextBlock row) => (Run)row.FindName("ByteText");

    private void RowScroll_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!this._settingScroll)
        {
            this._firstRow = args.NewValue >= this.RowScroll.Maximum ? this.MaxFirstRow : (long)Math.Round(args.NewValue);
            this.UpdateViewport();
        }
    }

    private void ScrollRows(long delta)
    {
        this._firstRow += Math.Clamp(delta, -this._firstRow, this.MaxFirstRow - this._firstRow);
        this.UpdateViewport();
    }

    private void PublishEdit()
    {
        this.Document?.NotifyEdit(this);
        this.Refresh(dataChanged: true);
        this.RevealColumn();
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    private void OtherViewEdited(object? sender, HexDocument.Change change)
    {
        if (!ReferenceEquals(sender, this))
        {
            this._buffer.ApplyExternalChange(change);
            this._firstRow = change.MapPosition(this._firstRow * HexEditorRow.BytesPerRow) / HexEditorRow.BytesPerRow;
            this.Refresh(dataChanged: true, revealCaret: false);
        }
    }

    private static bool IsSourceError(Exception ex) => ex is IOException or ObjectDisposedException or UnauthorizedAccessException;

    private void UpdateStatus()
    {
        var mode = this._ascii ? "ASCII" : "Hex";
        string value;
        try
        {
            value = this.CurrentByte();
        }
        catch (Exception ex) when (IsSourceError(ex))
        {
            this.ShowStatus(ex.Message);
            return;
        }
        var readOnly = !this._buffer.CanWriteByte(this._buffer.Caret);
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            if (value != this._automationValue)
            {
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, this._automationValue, value);
            }
            if (readOnly != this._automationReadOnly)
            {
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.IsReadOnlyProperty, this._automationReadOnly, readOnly);
            }
        }
        this._automationValue = value;
        this._automationReadOnly = readOnly;

        var status = value.Length == 0 ? $"{mode}, end of data" : $"{mode}, offset {this._buffer.Caret:X}, byte {value}";
        if (this.SelectionLength > 1)
        {
            status += $", {this.SelectionLength:N0} bytes selected";
        }
        else if (!this._ascii && !this.IsReadOnly && value.Length > 0)
        {
            status += this._buffer.LowNibble ? ", low digit" : ", high digit";
        }

        if (this.IsReadOnly)
        {
            status += ", read-only";
        }
        else if (this.Document is not null && !this._buffer.CanResize)
        {
            status += ", fixed length";
        }
        else if (value.Length == 0 && this.MaxLength > 0 && this._buffer.Length >= this.MaxLength)
        {
            status += $", maximum length {this.MaxLength:N0} bytes";
        }
        else if (this.SelectionLength == 0 && this._buffer.CanWriteByte(this._buffer.Caret))
        {
            status += ", type to append";
        }

        this.ShowStatus(status);
    }

    private string CurrentByte()
    {
        Span<byte> value = stackalloc byte[1];
        return this.Document?.Read(this._buffer.Caret, value) == 1 ? Convert.ToHexString(value) : string.Empty;
    }

    private void ShowStatus(string text)
    {
        if (this.Status.Text == text)
        {
            return;
        }

        this.Status.Text = text;
        AutomationProperties.SetItemStatus(this, text);
        if (this.FocusState == FocusState.Unfocused && this.OffsetPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!this.ShowStatusBar)
        {
            FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
                AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "HexEditorStatus");
        }
        else if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            FrameworkElementAutomationPeer.CreatePeerForElement(this.Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
