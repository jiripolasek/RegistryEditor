// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Resources;

public sealed partial class ToolRowResources : ResourceDictionary
{
    // WinUI cannot share one path geometry between multiple rendered icons.
    public static IconSource BinaryIcon => new PathIconSource
    {
        Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Application.Current.Resources["RegistryBinaryIconData"])
    };

    public ToolRowResources()
    {
        this.InitializeComponent();
    }

    public static Style IconStyle(string key)
    {
        return (Style)Application.Current.Resources[key];
    }
}
