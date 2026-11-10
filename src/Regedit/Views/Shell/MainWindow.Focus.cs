// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Views.Pages;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private void CyclePaneFocus_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.Dialogs.IsOpen)
        {
            return;
        }

        static void FocusContent(FrameworkElement content, UIElement fallback)
        {
            var field = FocusManager.FindFirstFocusableElement(content) as UIElement ?? fallback;
            field.Focus(FocusState.Keyboard);
            field.StartBringIntoView();
        }

        var panes = new List<(FrameworkElement Root, Action Focus)>();
        if (this.SelectedTabContent is BrowserPage browser)
        {
            panes.AddRange(browser.GetFocusPanes());
        }
        else if (this.SelectedTabContent is FrameworkElement content)
        {
            panes.Add((content, () => FocusContent(content, content)));
        }

        if (this.ToolsSplit.IsPaneOpen && this.ToolContent.Content is IToolSurface tool)
        {
            panes.Add((this.ToolPane, () => tool.RequestFocus()));
        }

        if (this.BottomPane.Visibility == Visibility.Visible)
        {
            panes.Add((this.BottomPane, () => FocusContent(this.BottomContent, this.BottomTabs)));
        }

        if (panes.Count == 0)
        {
            return;
        }

        var current = -1;
        for (var element = FocusManager.GetFocusedElement(this.Shell.XamlRoot) as DependencyObject;
             element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            current = panes.FindIndex(pane => ReferenceEquals(pane.Root, element));
            if (current >= 0)
            {
                break;
            }
        }

        panes[(current + 1) % panes.Count].Focus();
        args.Handled = true;
    }
}
