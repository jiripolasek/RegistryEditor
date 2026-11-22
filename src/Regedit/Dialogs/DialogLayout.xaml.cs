// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class DialogLayout : UserControl
{
    internal string AccessibleTitle { get; }
    internal TitleBar WindowTitleBar => this.CaptionBar;
    internal bool CanDismiss { get; init; } = true;
    internal bool ShowCloseButton { get; init; } = true;
    internal Func<bool>? Accept { get; init; }
    internal Action? OnOpened { get; init; }
    internal event Action<ContentDialogResult>? Completed;

    private readonly ContentDialogButton _defaultButton;

    internal DialogLayout(
        string title,
        object body,
        string primary = "",
        string close = "Cancel",
        string? subject = null,
        string? subtitle = null,
        ContentDialogButton defaultButton = ContentDialogButton.Primary,
        bool warning = false,
        Uri? help = null,
        bool fillBody = false)
    {
        this.InitializeComponent();
        this._defaultButton = defaultButton;
        this.AccessibleTitle = string.IsNullOrEmpty(subject) ? title : $"{title} {subject}";
        this.ActionTitle.Text = title;
        this.SubjectTitle.Text = string.IsNullOrEmpty(subject) ? string.Empty : $" {subject}";
        AutomationProperties.SetName(this.Heading, this.AccessibleTitle);
        ToolTipService.SetToolTip(this.Heading, this.AccessibleTitle);
        if (!string.IsNullOrEmpty(subtitle))
        {
            this.SubtitleText.Text = subtitle;
            this.SubtitleText.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(this.SubtitleText, subtitle);
        }

        this.WarningIcon.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        this.BodyHost.Content = body;
        if (fillBody)
        {
            this.BodyHost.Margin = new Thickness(0);
            this.BodyHost.VerticalContentAlignment = VerticalAlignment.Stretch;
            this.BodyScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            this.BodyScroll.VerticalContentAlignment = VerticalAlignment.Stretch;
            this.BodyScroll.VerticalScrollMode = ScrollMode.Disabled;
            this.BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        this.AcceptButton.Content = primary;
        this.AcceptButton.Visibility = string.IsNullOrEmpty(primary) ? Visibility.Collapsed : Visibility.Visible;
        this.FooterCloseButton.Content = close;
        this.FooterCloseButton.Visibility = string.IsNullOrEmpty(close) ? Visibility.Collapsed : Visibility.Visible;
        if (defaultButton != ContentDialogButton.None)
        {
            this.GetDefaultButton().Style = (Style)this.Resources["DialogDefaultButtonStyle"];
        }

        this.HelpButton.NavigateUri = help;
        this.HelpButton.Visibility = help is null ? Visibility.Collapsed : Visibility.Visible;
        this.KeyDown += this.Layout_KeyDown;
    }

    internal void Prepare(bool captioned)
    {
        this.CaptionBar.Visibility = captioned ? Visibility.Visible : Visibility.Collapsed;
        this.HeadingPanel.Margin = new Thickness(0, captioned ? 4 : 16, 16, 0);
        this.HeaderCloseButton.Visibility = !captioned && this.CanDismiss && this.ShowCloseButton
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void EnableHorizontalScrolling()
    {
        this.BodyScroll.HorizontalScrollMode = ScrollMode.Enabled;
        this.BodyScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    internal void FocusOnOpen()
    {
        if (this.OnOpened is { } open)
        {
            open();
        }
        else if (this._defaultButton != ContentDialogButton.None)
        {
            this.GetDefaultButton().Focus(FocusState.Programmatic);
        }
    }

    private Button GetDefaultButton()
    {
        return this._defaultButton == ContentDialogButton.Primary && this.AcceptButton.Visibility == Visibility.Visible
            ? this.AcceptButton
            : this.FooterCloseButton;
    }

    private void Accept_Click(object sender, RoutedEventArgs args)
    {
        if (this.Accept?.Invoke() != false)
        {
            this.Completed?.Invoke(ContentDialogResult.Primary);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs args)
    {
        this.Completed?.Invoke(ContentDialogResult.None);
    }

    private void Layout_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            if (this.CanDismiss)
            {
                this.Completed?.Invoke(ContentDialogResult.None);
            }
        }
        else if (args.Key == VirtualKey.Enter && this._defaultButton != ContentDialogButton.None
                 && FocusManager.GetFocusedElement(this.XamlRoot) is not (ButtonBase or TextBox { AcceptsReturn: true } or RichEditBox))
        {
            var button = this.GetDefaultButton();
            if (button.Visibility == Visibility.Visible && button.IsEnabled)
            {
                args.Handled = true;
                if (button == this.AcceptButton)
                {
                    this.Accept_Click(button, args);
                }
                else
                {
                    this.Close_Click(button, args);
                }
            }
        }
    }
}
