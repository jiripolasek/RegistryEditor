// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ToolContextRow : UserControl
{
    public static readonly DependencyProperty PrimaryContentProperty = DependencyProperty.Register(
        nameof(PrimaryContent), typeof(UIElement), typeof(ToolContextRow), new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryContentProperty = DependencyProperty.Register(
        nameof(SecondaryContent), typeof(UIElement), typeof(ToolContextRow), new PropertyMetadata(null));

    public ToolContextRow() { this.InitializeComponent(); }

    public UIElement? PrimaryContent
    {
        get => (UIElement?)this.GetValue(PrimaryContentProperty);
        set => this.SetValue(PrimaryContentProperty, value);
    }

    public UIElement? SecondaryContent
    {
        get => (UIElement?)this.GetValue(SecondaryContentProperty);
        set => this.SetValue(SecondaryContentProperty, value);
    }

    public Visibility ContentVisibility(UIElement? content)
    {
        return content is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
