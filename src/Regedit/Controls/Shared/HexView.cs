// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Controls;
using Microsoft.UI.Xaml;

namespace JPSoftworks.Regedit.Controls.Shared;

/// <summary>Adapts the stream-backed editor to the registry dialogs' byte-array snapshots.</summary>
public sealed partial class HexView : HexEditor
{
    internal const double DialogWidth = 744;

    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(
        nameof(Bytes), typeof(byte[]), typeof(HexView), new PropertyMetadata(null, OnBytesChanged));

    private byte[]? _snapshot;

    public byte[]? Bytes
    {
        get => (byte[]?)this.GetValue(BytesProperty);
        set => this.SetValue(BytesProperty, value);
    }

    public event EventHandler<byte[]>? BytesEdited;

    public HexView()
    {
        this.Style = (Style)Application.Current.Resources["DefaultHexEditorStyle"];
        this.IsReadOnly = true;
        this.Document = new HexDocument(Stream.Null);
        this.Edited += this.OnEdited;
    }

    private static void OnBytesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (HexView)sender;
        if (!ReferenceEquals(view._snapshot, args.NewValue))
        {
            view._snapshot = (byte[]?)args.NewValue;
            view.Document = new HexDocument(new MemoryStream(view._snapshot ?? [], writable: false));
        }
    }

    private void OnEdited(object? sender, EventArgs args)
    {
        var document = this.Document!;
        var bytes = new byte[checked((int)document.Length)];
        document.Read(0, bytes);
        this._snapshot = bytes;
        this.Bytes = bytes;
        this.BytesEdited?.Invoke(this, bytes);
    }
}
