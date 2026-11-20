// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Views.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private readonly List<(ToolCommand Command, ToggleMenuFlyoutItem Item)> _toolMenuToggles = [];

    internal (ToolbarButtons Button, ToolCommand Command)[] ToolbarCommands { get; private set; } = [];

    private void InitializeToolbarCommands()
    {
        this.ToolbarCommands =
        [
            (ToolbarButtons.Favorites,
                new ToolCommand("Favorites", Symbol.Favorite)
                {
                    IconSource = new FontIconSource { Glyph = "\uE728", FontSize = 16 },
                    AutomationId = "ToolbarFavorites",
                    ToolTip = "Open Favorites in the right sidebar",
                    Command = new RelayCommand(() => this.ShowTools(ToolKind.Favorites, true)),
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.Favorites)
                }),
            (ToolbarButtons.Notes,
                new ToolCommand("Notes", Symbol.Edit)
                {
                    IconSource = new FontIconSource
                    {
                        FontFamily = new FontFamily(ToolPresentation.FluentSystemIconsFontFamily), Glyph = "\uF56B"
                    },
                    AutomationId = "ToolbarNotes",
                    ToolTip = "Open Notes in the right sidebar",
                    Command = new RelayCommand(() => this.ShowTools(ToolKind.Notes, true)),
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.Notes)
                }),
            (ToolbarButtons.Watches,
                new ToolCommand("Watches", Symbol.View)
                {
                    IconSource = new FontIconSource { Glyph = "\uE7B3" },
                    AutomationId = "ToolbarWatches",
                    ToolTip = "Open Watches in the bottom panel",
                    Command = this.ViewModel.OpenToolCommand,
                    CommandParameter = ToolKind.Watches,
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.Watches)
                }),
            (ToolbarButtons.Journal,
                new ToolCommand("Change journal", Symbol.Clock)
                {
                    AutomationId = "ToolbarJournal",
                    ToolTip = "Open the change journal in the bottom panel",
                    Command = this.ViewModel.OpenToolCommand,
                    CommandParameter = ToolKind.Journal,
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.Journal)
                }),
            (ToolbarButtons.SavedSearches,
                new ToolCommand("Saved searches", Symbol.Find)
                {
                    AutomationId = "ToolbarSavedSearches",
                    ToolTip = "Open saved searches",
                    Command = this.ViewModel.OpenSavedSearchesCommand,
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.SavedSearches)
                }),
            (ToolbarButtons.Workspaces,
                new ToolCommand("Workspaces", Symbol.Folder)
                {
                    AutomationId = "ToolbarWorkspaces",
                    ToolTip = "Open workspaces",
                    Command = this.ViewModel.OpenWorkspacesCommand,
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.Workspaces)
                }),
            (ToolbarButtons.Sidebar,
                new ToolCommand("Right sidebar", Symbol.DockRight)
                {
                    AutomationId = "ToolbarSidebar",
                    ToolTip = "Show or hide the right sidebar (F9)",
                    IsToggle = true,
                    ShortcutText = "F9",
                    Command = new RelayCommand(() => this.ToggleTools()),
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.Sidebar)
                }),
            (ToolbarButtons.BottomPanel,
                new ToolCommand("Bottom panel", Symbol.DockBottom)
                {
                    AutomationId = "ToolbarBottomPanel",
                    ToolTip = "Show or hide the bottom panel (Ctrl+J)",
                    IsToggle = true,
                    ShortcutText = "Ctrl+J",
                    Command = new RelayCommand(this.ToggleBottomPane),
                    Attach = control => this.AttachToolbarButton(control, ToolbarButtons.BottomPanel)
                })
        ];
        this.UpdateToolbarCommands();
    }

    internal void UpdateToolbarCommands()
    {
        foreach (var (button, command) in this.ToolbarCommands)
        {
            command.IsVisible = (this.Settings.ToolbarButtons & button) != 0;
            command.IsChecked = button switch
            {
                ToolbarButtons.Sidebar => this.ViewModel.IsPaneOpen,
                ToolbarButtons.BottomPanel => this.ViewModel.IsBottomPaneOpen,
                _ => false
            };
        }

        foreach (var (command, item) in this._toolMenuToggles)
        {
            item.IsChecked = command.IsChecked;
        }
    }

    internal void FillToolsMenu(IList<MenuFlyoutItemBase> items, Action<ToolbarButtons, ToolCommand, MenuFlyoutItem>? attach = null)
    {
        this.UpdateToolbarCommands();
        items.Clear();
        this._toolMenuToggles.Clear();
        foreach (var (button, command) in this.ToolbarCommands)
        {
            if (button == ToolbarButtons.Sidebar)
            {
                items.Add(new MenuFlyoutSeparator());
            }

            MenuFlyoutItem item = command.IsToggle ? new ToggleMenuFlyoutItem { IsChecked = command.IsChecked } : new MenuFlyoutItem();
            item.Text = command.Label;
            item.KeyboardAcceleratorTextOverride = command.ShortcutText ?? string.Empty;
            item.Icon = command.IconSource is { } source
                ? new IconSourceElement { IconSource = source }
                : new FontIcon { Glyph = SymbolGlyphs.Get(command.Symbol) };
            item.Command = command.Command;
            item.CommandParameter = command.CommandParameter;
            if (item is ToggleMenuFlyoutItem toggle)
            {
                this._toolMenuToggles.Add((command, toggle));
            }

            attach?.Invoke(button, command, item);
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());
        var customize = new MenuFlyoutItem { Text = "Customize toolbar…", Icon = new SymbolIcon(Symbol.Setting) };
        customize.Click += (_, _) => this.CustomizeToolbar();
        items.Add(customize);
    }

    private void AttachToolbarButton(FrameworkElement control, ToolbarButtons button)
    {
        control.ContextFlyout = this.CreateToolbarContextMenu(button);
        if (control is ContentControl { Content: FontIcon icon })
        {
            icon.FontSize = 14;
        }

        if (control is Control toolbarButton)
        {
            toolbarButton.CornerRadius = new CornerRadius(8);
        }
    }

    internal MenuFlyout CreateToolbarContextMenu(ToolbarButtons button = ToolbarButtons.None)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var customize = new MenuFlyoutItem { Text = "Customize", Icon = new SymbolIcon(Symbol.Setting) };
        customize.Click += (_, _) => this.CustomizeToolbar();
        menu.Items.Add(customize);
        if (button != ToolbarButtons.None)
        {
            var hide = new MenuFlyoutItem
            {
                Text = "Hide from toolbar",
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Cancel) },
                Command = this.Preferences.HideToolbarButtonCommand,
                CommandParameter = button
            };
            hide.Click += (_, _) => this.DispatcherQueue.TryEnqueue(() => (this.SelectedTabContent as BrowserPage)?.FocusToolsButton());
            menu.Items.Add(hide);
        }

        return menu;
    }

    internal void ShowListSpacing()
    {
        this.ViewModel.OpenSettingsCommand.Execute(null);
        ((SettingsPage)this.ActiveTabContent.Content).ShowListSpacing();
    }

    private void CustomizeToolbar()
    {
        this.ViewModel.OpenSettingsCommand.Execute(null);
        ((SettingsPage)this.ActiveTabContent.Content).ShowToolbarButtons();
    }
}
