// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using BetterHexViewer.WinUI3;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Shared;

/// <summary>
/// Read-only hex and ASCII view of a byte array. Wraps <see cref="BetterHexViewer.WinUI3.BetterHexViewer"/> so the
/// rest of the app depends only on <see cref="Bytes"/> and <see cref="Highlight"/>.
/// </summary>
public sealed partial class HexView : UserControl
{
    /// <summary>Raised with a new array after the user overwrites a byte by typing in the view.</summary>
    public event EventHandler<byte[]>? BytesEdited;

    /// <summary>Content width that fits 16 bytes per row, the offset column and the ASCII column.</summary>
    internal const double DialogWidth = 744;

    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(
        nameof(Bytes), typeof(byte[]), typeof(HexView), new PropertyMetadata(null, OnBytesChanged));

    private readonly BetterHexViewer.WinUI3.BetterHexViewer _viewer;
    private byte[] _loaded = [];
    private long _cursor;
    private bool _lowNibble;
    private bool _editing;

    /// <summary>When true, typing hex digits overwrites the selected byte (two digits per byte).</summary>
    public bool IsEditable { get; set; }

    /// <summary>
    /// The bytes to display. The viewer keeps a reference to the array, so callers must not mutate it afterwards.
    /// </summary>
    public byte[]? Bytes { get => (byte[]?)this.GetValue(BytesProperty); set => this.SetValue(BytesProperty, value); }

    public HexView()
    {
        this._viewer = new BetterHexViewer.WinUI3.BetterHexViewer
        {
            FontFamily = new FontFamily("Consolas"),
            OffsetFormat = OffsetFormat.Hexadecimal,
            ShowAsciiPanel = true,
            // Always 16 bytes per row so offsets advance by 0x10; hosts must be at least DialogWidth wide.
            FullWidth = false,
            NoDataLoadedText = "No data",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        this.Content = this._viewer;
        this.IsTabStop = false;
        // BetterHexViewer 1.2.3 hooks window-wide pointer/key handlers on Loaded but creates its canvas in
        // OnApplyTemplate. Inside a collapsed subtree Loaded fires without a template, so the first click
        // anywhere in the window throws a NullReferenceException and fail-fasts the app. Force the template.
        this.Loaded += (_, _) => this._viewer.ApplyTemplate();
        this._viewer.SelectionChanged += (_, e) =>
        {
            // Our own reselect after an edit keeps the nibble state; a user move starts a new byte.
            if (!this._editing && e.StartOffset != this._cursor)
            {
                this._cursor = e.StartOffset;
                this._lowNibble = false;
            }
        };
        this.CharacterReceived += this.OnCharacterReceived;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // Characters, not virtual keys, so digits work on layouts that type them with Shift.
        var digit = HexDigit(e.Character);
        if (!this.IsEditable || this._loaded.Length == 0 || digit < 0)
        {
            return;
        }

        e.Handled = true;
        var offset = Math.Clamp(this._cursor, 0, this._loaded.Length - 1);
        var bytes = (byte[])this._loaded.Clone();
        bytes[offset] = this._lowNibble ? (byte)((bytes[offset] & 0xF0) | digit) : (byte)((digit << 4) | (bytes[offset] & 0x0F));
        var next = this._lowNibble ? Math.Min(offset + 1, bytes.Length - 1) : offset;
        this._lowNibble = !this._lowNibble;
        this._cursor = next;
        this._editing = true;
        try
        {
            // Reloading clears the viewer's selection; ignore those events so the nibble state survives.
            this.Load(bytes);
            this._viewer.SelectRange(next, 1);
            this._viewer.ScrollToOffset(next);
        }
        finally
        {
            this._editing = false;
        }

        this.BytesEdited?.Invoke(this, bytes);
    }

    private static int HexDigit(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };
    }

    private void Load(byte[] bytes)
    {
        this._loaded = bytes;
        if (bytes.Length > 0)
        {
            this._viewer.LoadBytes(bytes);
        }
        else
        {
            this._viewer.Clear();
        }
    }

    /// <summary>
    /// Scrolls to <paramref name="offset"/> and selects up to <paramref name="length"/> bytes from it.
    /// Out-of-range requests are ignored.
    /// </summary>
    public void Highlight(long offset, long length)
    {
        var size = this._viewer.FileSize;
        if (offset < 0 || offset >= size)
        {
            return;
        }

        this._viewer.ScrollToOffset(offset);
        if (length > 0)
        {
            this._viewer.SelectRange(offset, Math.Min(length, size - offset));
        }
    }

    private static void OnBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (HexView)d;
        // Skip the echo of our own edit coming back through the binding.
        if (!ReferenceEquals(e.NewValue, view._loaded))
        {
            view.Load(e.NewValue as byte[] ?? []);
        }
    }
}
