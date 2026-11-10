// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.WinUI.Controls;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class SettingsPage : Page
{
    internal SettingsViewModel ViewModel { get; }

    internal Button MenuButton => this.PageHeader.MenuButton;

    private string AppVersion => $"Version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}";

    internal SettingsPage(SettingsViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        this.AddShortcutGroup(
            "Tabs and navigation", ("Open the application menu", "Alt+F"), ("Open Settings", "Ctrl+,"),
            ("Open Keyboard shortcuts", "Ctrl+/"), ("Open Help and about", "F1"), ("New tab", "Ctrl+T"),
            ("Reopen closed tab", "Ctrl+Shift+T"), ("Close tab", "Ctrl+W"),
            ("Next / previous tab", "Ctrl+Tab / Ctrl+Shift+Tab"), ("Focus the address bar", "Ctrl+L / Alt+D"),
            ("Back / forward", "Alt+Left / Alt+Right"),
            ("Back in the list / select the parent in the tree", "Backspace"), ("Open the parent key", "Alt+Up"),
            ("Switch between HKLM and HKCU", "Ctrl+Shift+H"), ("Sync tree to current key", "Alt+Shift+L"),
            ("Refresh the current key", "F5"), ("Cycle through visible panes", "F6"),
            ("Show toolbar key hints", "Alt"));
        this.AddShortcutGroup(
            "Search and filtering", ("Search the registry", "Ctrl+F"), ("Open advanced search", "Ctrl+Shift+F"),
            ("Find the next match", "F3"), ("Filter the current key", "Ctrl+Alt+F"),
            ("Filter by typing in the registry list", "Start typing"));
        this.AddShortcutGroup(
            "Selection and editing", ("Copy the key path", "Ctrl+Shift+C"),
            ("Undo / redo this tab's last registry edit", "Ctrl+Z / Ctrl+Y"), ("Inspect the selected value", "Ctrl+I"),
            ("Show or close the detail panel", "Alt+P"), ("Open the New menu in the registry list", "Ctrl+N"),
            ("Copy the selected key or value", "Ctrl+C"), ("Paste keys or values", "Ctrl+V"),
            ("Rename the selected registry item or favorite", "F2"),
            ("Delete selected registry items or remove favorites", "Delete"));
        this.AddShortcutGroup(
            "Tools and favorites", ("Open / close the right sidebar", "F9"),
            ("Open / close the bottom panel", "Ctrl+J"),
            ("Add or remove the selected key or current location in Favorites", "Ctrl+D"),
            ("Save the current note in Notes", "Ctrl+S"));
        this.AddShortcutGroup(
            "Mouse actions", ("Back / forward", "Mouse Back / Forward buttons"),
            ("Open a location in a background tab", "Middle-click the location"),
            ("Close a tab", "Middle-click the tab"));
    }

    private async void DataFontExpander_Expanded(object? sender, EventArgs args)
    {
        await this.ViewModel.LoadFontFamiliesAsync(SystemFonts.GetFontFamilies);
    }

    private void AddShortcutGroup(string heading, params (string Action, string Keys)[] shortcuts)
    {
        var group = new SettingsExpander { Header = heading, IsExpanded = true };
        foreach (var shortcut in shortcuts)
        {
            group.Items.Add(
                new SettingsCard
                {
                    Header = shortcut.Action,
                    MinHeight = 44,
                    Padding = new Thickness(16, 8, 16, 8),
                    Content = new TextBlock
                    {
                        Text = shortcut.Keys,
                        FontFamily = new FontFamily("Consolas"),
                        MaxWidth = 240,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        HorizontalAlignment = HorizontalAlignment.Right
                    }
                });
        }

        this.ShortcutGroups.Children.Add(group);
    }

    private bool ToolbarButtonChecked(ToolbarButtons selected, ToolbarButtons button)
    {
        return (selected & button) != 0;
    }

    private void ToolbarButton_Toggled(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleSwitch { Tag: ToolbarButtons button } toggle &&
            toggle.IsOn != this.ToolbarButtonChecked(this.ViewModel.ToolbarButtons, button))
        {
            this.ViewModel.ToggleToolbarButtonCommand.Execute(button);
        }
    }

    internal void ShowShortcuts(bool show, Control? focusTarget = null)
    {
        this.SettingsScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        this.ShortcutsScroll.Visibility
            = this.ShortcutsBackButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        this.PageHeader.Title = show ? "Keyboard shortcuts" : "Settings";
        var target = focusTarget ?? (show ? (Control)this.ShortcutsBackButton : this.ThemeSelector);
        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var scroll = show ? this.ShortcutsScroll : this.SettingsScroll;
                scroll.UpdateLayout();
                scroll.ChangeView(
                    null,
                    target == this.KeyboardShortcutsButton || target == this.AboutExpander
                        ? scroll.ScrollableHeight
                        : 0, null, true);
                target.Focus(FocusState.Keyboard);
                target.StartBringIntoView();
            });
    }

    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs args)
    {
        this.ShowShortcuts(true);
    }

    internal void ShowAbout()
    {
        this.AboutExpander.IsExpanded = true;
        this.ShowShortcuts(false, this.AboutExpander);
    }

    internal void ShowToolbarButtons()
    {
        this.ToolbarButtonsExpander.IsExpanded = true;
        this.ShowShortcuts(false, this.ToolbarBackOption);
    }

    internal void ShowListSpacing()
    {
        this.ShowShortcuts(false, this.ListSpacingSlider);
    }

    private void ShortcutsBack_Click(object sender, RoutedEventArgs args)
    {
        this.ShowShortcuts(false, this.KeyboardShortcutsButton);
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: string family })
        {
            this.ViewModel.DataFontFamily = family;
        }
    }

    private void FontFamily_Submitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        this.ViewModel.DataFontFamily = args.Text;
    }

    private void FontFamily_LostFocus(object sender, RoutedEventArgs args)
    {
        var selector = (ComboBox)sender;
        this.ViewModel.DataFontFamily = selector.Text;
    }

    internal void RevealSaveError()
    {
        this.DispatcherQueue.TryEnqueue(() => this.SaveError.StartBringIntoView());
    }

    private void WebSearchUrl_LostFocus(object sender, RoutedEventArgs args)
    {
        this.ViewModel.WebSearchUrl = ((TextBox)sender).Text;
    }

    private void WebSearchUrl_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            this.WebSearchUrl_LostFocus(sender, args);
        }
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.Layout.Padding = new Thickness(args.NewSize.Width < 760 ? 16 : 24);
    }
}
