// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Views.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ToolPageHeader : UserControl
{
    public ToolPageHeader()
    {
        this.InitializeComponent();
    }

    internal Button MenuButton => this.GlobalMenuButton;

    public string Title
    {
        get => this.Heading.Text;
        set
        {
            this.Heading.Text = value;
            ToolTipService.SetToolTip(this.Heading, value);
        }
    }

    public string Description
    {
        get => this.DescriptionText.Text;
        set
        {
            this.DescriptionText.Text = value;
            this.DescriptionText.Visibility = ToolView.TextVisibility(value);
            ToolTipService.SetToolTip(this.DescriptionText, value);
        }
    }

    internal void SetAction(string label, string automationId, Action<Button> action, Symbol icon)
    {
        this.SetAction(label, automationId, action, new SymbolIcon(icon));
    }

    internal void SetAction(string label, string automationId, Action<Button> action, IconElement icon)
    {
        this.PageAction.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
        };
        this.PageAction.Visibility = Visibility.Visible;
        AutomationProperties.SetName(this.PageAction, label);
        AutomationProperties.SetAutomationId(this.PageAction, automationId);
        ToolTipService.SetToolTip(this.PageAction, label);
        this.PageAction.Click += (_, _) => action(this.PageAction);
    }
}
