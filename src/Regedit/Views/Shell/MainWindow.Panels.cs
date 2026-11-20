// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.Views.Pages;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private IPanelView? _panelView;
    private bool _syncingPanelSearch;

    private void BottomTabs_Loaded(object sender, RoutedEventArgs args)
    {
        this.BottomTabs.ApplyTemplate();
        // shortcut: TabView has no add-button flyout API; recheck the AddButton template part when upgrading WinUI.
        if (VisualTreeHelper.GetChildrenCount(this.BottomTabs) > 0
            && VisualTreeHelper.GetChild(this.BottomTabs, 0) is FrameworkElement root
            && root.FindName("AddButton") is Button button)
        {
            button.Flyout = this.BottomPanelMenu;
            AutomationProperties.SetAutomationId(button, "BottomPanelAddButton");
            AutomationProperties.SetName(button, "Open panel");
            ToolTipService.SetToolTip(button, "Open panel");
        }
    }

    private void ConfigureDockedView(object? view, bool docked)
    {
        (view as IPanelView)?.SetDocked(docked);
    }

    private void ShowBottomContent()
    {
        this.AttachPanelView(null);
        this.BottomContent.Content = null;
        this.BottomTabs.SelectedItem = this.ViewModel.ActiveBottomTab;
        if (this.ViewModel.ActiveBottomTab is { } tab)
        {
            var view = this.GetView(tab);
            this.ConfigureDockedView(view, true);
            this.AttachPanelView(view as IPanelView);
            this.BottomContent.Content = view;
        }

        this.UpdateToolLayout();
    }

    /// <summary>Renders the panel header search box and commands for <paramref name="view"/>.</summary>
    private void AttachPanelView(IPanelView? view)
    {
        if (this._panelView?.PanelSearch is { } previous)
        {
            previous.TextChanged -= this.PanelSearch_SourceChanged;
        }

        this._panelView = view;
        this.BottomActions.SetCommands(view?.PanelCommands);
        this.BottomActions.Visibility = view is { PanelCommands.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this.BottomActions, view?.PanelCommandsId ?? "BottomActions");

        var search = view?.PanelSearch;
        this.BottomFilter.Visibility = search is null ? Visibility.Collapsed : Visibility.Visible;
        if (search is null)
        {
            return;
        }

        AutomationProperties.SetName(this.PanelSearchBox, search.AutomationName);
        AutomationProperties.SetAutomationId(this.PanelSearchBox, search.AutomationId ?? string.Empty);
        this.PanelSearchBox.PlaceholderText = search.Placeholder;
        ToolTipService.SetToolTip(this.PanelSearchBox, search.ToolTip);
        this.ShowPanelSearchText(search.Text);
        search.TextChanged += this.PanelSearch_SourceChanged;
    }

    private void PanelSearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (!this._syncingPanelSearch && this._panelView?.PanelSearch is { } search)
        {
            search.Text = this.PanelSearchBox.Text;
        }
    }

    private void PanelSearch_SourceChanged()
    {
        if (this._panelView?.PanelSearch is { } search)
        {
            this.ShowPanelSearchText(search.Text);
        }
    }

    private void ShowPanelSearchText(string text)
    {
        if (this.PanelSearchBox.Text == text)
        {
            return;
        }

        this._syncingPanelSearch = true;
        try
        {
            this.PanelSearchBox.Text = text;
        }
        finally
        {
            this._syncingPanelSearch = false;
        }
    }

    private void BottomTabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (this.BottomTabs.SelectedItem is WindowTab tab && this.ViewModel.BottomTabs.Contains(tab))
        {
            this.ViewModel.ActiveBottomTab = tab;
        }
    }

    private void BottomTabs_CloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is WindowTab tab)
        {
            this.ViewModel.CloseBottomTab(tab);
        }
    }

    private void PromoteBottomTab_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.PromoteBottomTab();
    }

    private void HideBottomPane_Click(object sender, RoutedEventArgs args)
    {
        this.HideBottomPane();
    }

    private void BottomPane_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        this.ToggleBottomPane();
        args.Handled = true;
    }

    internal void ToggleBottomPane()
    {
        if (this.ViewModel.IsBottomPaneOpen)
        {
            this.HideBottomPane();
        }
        else
        {
            this.ViewModel.ShowBottomPane();
            this.SaveToolPreferences();
        }
    }

    private void HideBottomPane()
    {
        this.ViewModel.IsBottomPaneOpen = false;
        if (this.SelectedTabContent is BrowserPage page)
        {
            page.FocusToolsButton();
        }

        this.SaveToolPreferences();
    }

    private void BottomPane_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            this.HideBottomPane();
        }
    }

    private void BottomResize_DragDelta(object sender, DragDeltaEventArgs args)
    {
        this.ViewModel.ResizeBottomPane(-args.VerticalChange, this.Shell.ActualHeight);
        this.UpdateToolLayout();
    }

    private void BottomResize_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Up or VirtualKey.Down)
        {
            args.Handled = true;
            this.ViewModel.ResizeBottomPane(args.Key == VirtualKey.Up ? 20 : -20, this.Shell.ActualHeight);
            this.UpdateToolLayout();
            this.SaveToolPreferences();
        }
    }
}
