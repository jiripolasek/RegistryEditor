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
    private int _focusRequest;
    private (FrameworkElement Target, RoutedEventHandler Handler)? _pendingFocus;

    internal SettingsViewModel ViewModel { get; }

    internal Button MenuButton => this.PageHeader.MenuButton;

    private string AppVersion => $"Version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}";

    private Visibility TerminalSettingsVisibility => Visibility.Visible;

    private readonly Func<Task<bool>> _confirmExperimentalTerminal;

    internal void ShowTerminalSettings() => this.ShowShortcuts(false, this.PowerShellPathBox);

    internal SettingsPage(SettingsViewModel viewModel, Func<Task<bool>> confirmExperimentalTerminal)
    {
        this.ViewModel = viewModel;
        this._confirmExperimentalTerminal = confirmExperimentalTerminal;
        this.InitializeComponent();
        this.Unloaded += (_, _) => this.CancelDeferredFocus();
        this.AddShortcutGroup(
            "Tabs and navigation", ("Open the application menu", "Alt+F"), ("Open Settings", "Ctrl+,"),
            ("Open Keyboard shortcuts", "Ctrl+/"), ("Open Help and about", "F1"), ("New tab", "Ctrl+T"),
            ("Reopen closed tab", "Ctrl+Shift+T"), ("Close tab", "Ctrl+W"), ("Next / previous tab", "Ctrl+Tab / Ctrl+Shift+Tab"),
            ("Focus the address bar", "Ctrl+L / Alt+D"),
            ("Open the command palette from a registry tab", "Ctrl+Shift+P"),
            ("Open the command palette from the omnibox", ">"),
            ("Back / forward", "Alt+Left / Alt+Right"),
            ("Back in the list / select the parent in the tree", "Backspace"), ("Open the parent key", "Alt+Up"),
            ("Switch between HKLM and HKCU", "Ctrl+Shift+H"), ("Sync tree to current key", "Alt+Shift+L"),
            ("Refresh the current key", "F5"), ("Cycle through visible panes", "F6"), ("Show toolbar key hints", "Alt"));
        this.AddShortcutGroup(
            "Search and filtering", ("Search the registry", "Ctrl+F"), ("Open advanced search", "Ctrl+Shift+F"),
            ("Search from the omnibox", "?"),
            ("Find the next match", "F3"), ("Filter the current key", "Ctrl+Alt+F"),
            ("Filter by typing in the registry list", "Start typing"));
        this.AddShortcutGroup(
            "Selection and editing", ("Copy the key path", "Ctrl+Shift+C"),
            ("Undo / redo this tab's last registry edit", "Ctrl+Z / Ctrl+Y"), ("Inspect the selected value", "Ctrl+I"),
            ("Show or close the detail panel", "Alt+P"), ("Open the New menu in the registry list", "Ctrl+N"),
            ("Copy the selected key or value", "Ctrl+C"), ("Paste keys or values", "Ctrl+V"),
            ("Rename the selected registry item or favorite", "F2"), ("Delete selected registry items or remove favorites", "Delete"));
        this.AddShortcutGroup(
            "Tools and favorites", ("Open / close the right sidebar", "F9"), ("Open / close the bottom panel", "Ctrl+J"),
            ("Show or hide the Favorites bar", "Ctrl+Shift+B"), ("Open Favorites in the sidebar", "Ctrl+Shift+O"),
            ("Open a PowerShell terminal", "Ctrl+;"),
            ("Add or remove the selected key or current location in Favorites", "Ctrl+D"), ("Save the current note in Notes", "Ctrl+S"));
        this.AddShortcutGroup(
            "Registry file editor", ("Open the File menu", "Alt+F"), ("Open / save a registry file", "Ctrl+O / Ctrl+S"),
            ("Save as", "Ctrl+Shift+S"), ("Editor / split / preview", "Alt+1 / Alt+2 / Alt+3"), ("Open editor settings", "Ctrl+,"),
            ("Close the preview window", "Ctrl+W / Alt+F4"));
        this.AddShortcutGroup(
            "Mouse actions", ("Back / forward", "Mouse Back / Forward buttons"),
            ("Open a location in a background tab", "Middle-click the location"), ("Close a tab", "Middle-click the tab"));
    }

    private async void FontExpander_Expanded(object? sender, EventArgs args)
    {
        await this.ViewModel.LoadFontFamiliesAsync(SystemFonts.GetFontFamilies);
    }

    private async void ExperimentalTerminal_Toggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch toggle || toggle.IsOn == this.ViewModel.ExperimentalTerminalEnabled)
        {
            return;
        }

        if (!toggle.IsOn)
        {
            this.ViewModel.ExperimentalTerminalEnabled = false;
            return;
        }

        toggle.IsEnabled = false;
        toggle.IsOn = false;
        try
        {
            if (await this._confirmExperimentalTerminal())
            {
                this.ViewModel.ExperimentalTerminalEnabled = true;
            }
        }
        finally
        {
            toggle.IsEnabled = true;
        }
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
        if (sender is ToggleSwitch { Tag: ToolbarButtons button } toggle
            && toggle.IsOn != this.ToolbarButtonChecked(this.ViewModel.ToolbarButtons, button))
        {
            this.ViewModel.ToggleToolbarButtonCommand.Execute(button);
        }
    }

    private void CancelDeferredFocus()
    {
        this._focusRequest++;
        if (this._pendingFocus is { } pending)
        {
            pending.Target.Loaded -= pending.Handler;
            this._pendingFocus = null;
        }
    }

    internal void ShowShortcuts(bool show, Control? focusTarget = null)
    {
        this.CancelDeferredFocus();
        var request = this._focusRequest;
        this.SettingsScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        this.ShortcutsScroll.Visibility = this.ShortcutsBackButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        this.PageHeader.Title = show ? "Keyboard shortcuts" : "Settings";
        var target = focusTarget ?? (show ? (Control)this.ShortcutsBackButton : this.ThemeSelector);
        if (!target.IsLoaded)
        {
            this._pendingFocus = (target, Target_Loaded);
            target.Loaded += Target_Loaded;
            return;
        }

        void Target_Loaded(object sender, RoutedEventArgs args)
        {
            target.Loaded -= Target_Loaded;
            if (request == this._focusRequest)
            {
                this._pendingFocus = null;
                this.ShowShortcuts(show, target);
            }
        }

        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (request != this._focusRequest || !this.IsLoaded)
                {
                    return;
                }

                var scroll = show ? this.ShortcutsScroll : this.SettingsScroll;
                scroll.UpdateLayout();
                scroll.ChangeView(
                    null, target == this.KeyboardShortcutsButton || target == this.AboutExpander ? scroll.ScrollableHeight : 0, null, true);
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

    internal void ShowRegistryFileEditor()
    {
        this.RegistryFileEditorExpander.IsExpanded = true;
        this.ShowShortcuts(false, this.EditorLayoutSelector);
    }

    private void ShortcutsBack_Click(object sender, RoutedEventArgs args)
    {
        this.ShowShortcuts(false, this.KeyboardShortcutsButton);
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: string family } selector)
        {
            this.SetFontFamily(selector, family);
        }
    }

    private void FontFamily_Submitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        this.SetFontFamily(sender, args.Text);
    }

    private void FontFamily_LostFocus(object sender, RoutedEventArgs args)
    {
        var selector = (ComboBox)sender;
        this.SetFontFamily(selector, selector.Text);
    }

    private void SetFontFamily(ComboBox selector, string family)
    {
        if (selector == this.EditorFontSelector)
        {
            this.ViewModel.EditorFontFamily = family;
        }
        else if (selector == this.TerminalFontSelector)
        {
            this.ViewModel.TerminalFontFamily = family;
        }
        else
        {
            this.ViewModel.DataFontFamily = family;
        }
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
