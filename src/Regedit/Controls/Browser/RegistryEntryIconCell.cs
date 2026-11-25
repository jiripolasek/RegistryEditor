// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Browser;

public sealed partial class RegistryEntryIconCell : Grid
{
    private const int IconSize = 16;
    private static readonly int IconCount = Enum.GetValues<IconKind>().Length;

    [ThreadStatic]
    private static string?[]? s_pathData;

    public static readonly DependencyProperty NavigationStyleProperty = RegisterStyle(nameof(NavigationStyle));

    public static readonly DependencyProperty TextStyleProperty = RegisterStyle(nameof(TextStyle));

    public static readonly DependencyProperty LinkStyleProperty = RegisterStyle(nameof(LinkStyle));

    public static readonly DependencyProperty DataStyleProperty = RegisterStyle(nameof(DataStyle));


    private readonly PropertyChangedEventHandler _entryChanged;
    private RegistryEntry? _observedEntry;
    private PathIcon? _icon;
    private IconKind? _kind;
    private IconKind? _shape;

    public Style? NavigationStyle
    {
        get => (Style?)this.GetValue(NavigationStyleProperty);
        set => this.SetValue(NavigationStyleProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)this.GetValue(TextStyleProperty);
        set => this.SetValue(TextStyleProperty, value);
    }

    public Style? LinkStyle
    {
        get => (Style?)this.GetValue(LinkStyleProperty);
        set => this.SetValue(LinkStyleProperty, value);
    }

    public Style? DataStyle
    {
        get => (Style?)this.GetValue(DataStyleProperty);
        set => this.SetValue(DataStyleProperty, value);
    }

    public RegistryEntryIconCell()
    {
        this._entryChanged = this.EntryChanged;
        this.DataContextChanged += this.CellContextChanged;
        this.Loaded += this.CellLoaded;
        this.Unloaded += this.CellUnloaded;
    }

    private static IconKind GetIconKind(RegistryEntry entry)
    {
        if (entry.IsParent)
        {
            return IconKind.Parent;
        }

        if (entry.IsKey)
        {
            return IconKind.Key;
        }

        var type = entry.Value?.Type;
        if (type == RegistryValueType.Link)
        {
            return IconKind.Link;
        }

        var isText = entry.IsDefaultPlaceholder
                     || type is RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.MultiString;
        if (entry.IsDefaultValue)
        {
            return isText ? IconKind.DefaultText : IconKind.DefaultData;
        }

        return type switch
        {
            RegistryValueType.Binary => IconKind.Binary,
            RegistryValueType.DWord or RegistryValueType.DWordBigEndian or RegistryValueType.QWord => IconKind.Number,
            _ => isText ? IconKind.Text : IconKind.Other,
        };
    }

    private static string GetPathData(IconKind shape)
    {
        var paths = s_pathData ??= new string?[IconCount];
        return paths[(int)shape] ??= (string)Application.Current.Resources[GetPathKey(shape)];
    }

    private static string GetPathKey(IconKind shape)
    {
        return shape switch
        {
            IconKind.Parent => "RegistryParentIconData",
            IconKind.Key => "RegistryKeyIconData",
            IconKind.Link => "RegistryLinkIconData",
            IconKind.DefaultData => "RegistryDefaultTagIconData",
            IconKind.Binary => "RegistryBinaryIconData",
            IconKind.Number => "RegistryNumberIconData",
            IconKind.Text => "RegistryStringIconData",
            IconKind.Other => "RegistryOtherIconData",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static DependencyProperty RegisterStyle(string name)
    {
        return DependencyProperty.Register(
            name, typeof(Style), typeof(RegistryEntryIconCell),
            new PropertyMetadata(null, (owner, _) => ((RegistryEntryIconCell)owner).UpdateStyle()));
    }

    private void CellContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var entry = args.NewValue as RegistryEntry;
        if (this.IsLoaded)
        {
            this.ObserveEntry(entry);
        }

        this.UpdateIcon(entry);
    }

    private void CellLoaded(object sender, RoutedEventArgs args)
    {
        var entry = this.DataContext as RegistryEntry;
        this.ObserveEntry(entry);
        this.UpdateIcon(entry);
    }

    private void CellUnloaded(object sender, RoutedEventArgs args) => this.ObserveEntry(null);

    private void ObserveEntry(RegistryEntry? entry)
    {
        if (ReferenceEquals(this._observedEntry, entry))
        {
            return;
        }

        this._observedEntry?.PropertyChanged -= this._entryChanged;
        this._observedEntry = entry;
        this._observedEntry?.PropertyChanged += this._entryChanged;
    }

    private void EntryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "" or nameof(RegistryEntry.Value) or nameof(RegistryEntry.IsDefaultPlaceholder))
        {
            this.UpdateIcon(this._observedEntry);
        }
    }

    private void UpdateIcon(RegistryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var kind = GetIconKind(entry);
        if (this._kind == kind)
        {
            return;
        }

        var shape = kind == IconKind.DefaultText ? IconKind.DefaultData : kind;
        if (this._icon is null)
        {
            this._icon = new PathIcon
            {
                Width = IconSize,
                Height = IconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAccessibilityView(this._icon, AccessibilityView.Raw);
            this.Children.Add(this._icon);
        }

        if (this._shape != shape)
        {
            this._icon.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), GetPathData(shape));
            this._shape = shape;
        }

        this._kind = kind;
        this.UpdateStyle();
    }

    private void UpdateStyle()
    {
        if (this._icon is null)
        {
            return;
        }

        this._icon.Style = this._kind switch
        {
            IconKind.Parent => null,
            IconKind.Key => this.NavigationStyle,
            IconKind.Link => this.LinkStyle,
            IconKind.Text or IconKind.DefaultText => this.TextStyle,
            _ => this.DataStyle,
        };
    }

    private enum IconKind
    {
        Parent,
        Key,
        Link,
        DefaultText,
        DefaultData,
        Binary,
        Number,
        Text,
        Other,
    }
}
