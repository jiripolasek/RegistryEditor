// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Tools;

public sealed partial class ToolView
{
    private MenuFlyout CreateNoteMenu()
    {
        var menu = new MenuFlyout();
        var notes = this.Notes!;
        ListContextMenu.Add(
            menu, "Edit note", () => this.RequestFocus(), notes.HasNote, icon: Symbol.Edit, shortcut: "Enter");
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Open key",
                Command = notes.OpenCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.OpenFile) }
            });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy path", Command = notes.CopyPathCommand, Icon = new FontIcon { Glyph = "\uE62F" }
            });
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy note",
                Command = notes.CopyNoteCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Copy) }
            });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Delete note",
                Command = notes.DeleteCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Delete) },
                KeyboardAcceleratorTextOverride = "Del"
            });
        return menu;
    }

    private MenuFlyout CreateResultMenu()
    {
        this.RefreshCommands();
        var menu = new MenuFlyout();
        var selected = this.Selected;
        var location = selected.Length != 1
            ? null
            : selected[0].Source switch
            {
                WatchObservation observation => observation.Change.Location,
                JournalEntry entry => entry.Before.FirstOrDefault()?.Scope.Location ?? entry.ActivityLocation,
                _ => null
            };
        ListContextMenu.Add(
            menu, "Show details", () => this.SetDetailsExpanded(true), selected.Length == 1, icon: Symbol.View,
            shortcut: "Enter / Alt+P");
        ListContextMenu.Add(
            menu, "Open key",
            async () => await this.RunAsync(() => this._window.ViewModel.NavigateFromToolsAsync(location!)),
            location is not null, icon: Symbol.OpenFile);
        ListContextMenu.Add(
            menu, "Open in new tab",
            async () => await this.RunAsync(() => this._window.ViewModel.NavigateFromToolsAsync(location!, true)),
            location is not null, iconSource: (IconSource)Application.Current.Resources["OpenInNewTabIcon"]);
        menu.Items.Add(new MenuFlyoutSeparator());
        var actions = this.ResultActions;
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy path", Command = actions.CopyPathsCommand, Icon = new FontIcon { Glyph = "\uE62F" }
            });
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy results",
                Command = actions.CopyResultsCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Copy) }
            });
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy PowerShell",
                Command = actions.CopyPowerShellCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Copy) }
            });
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text = "Copy reg.exe (.cmd)",
                Command = actions.CopyRegExeCommand,
                Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Copy) }
            });
        if (this.JournalSurface is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var command in this._commands.Where(command =>
                         command.AutomationId is "JournalRevert" or "JournalArchive"))
            {
                menu.Items.Add(
                    new MenuFlyoutItem
                    {
                        Text = command.Label,
                        Command = command.Command,
                        CommandParameter = command.CommandParameter,
                        Icon = new FontIcon { Glyph = SymbolGlyphs.Get(command.Symbol) }
                    });
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        ListContextMenu.Add(
            menu, "Select all", () => this.Results.SelectAll(), this.Rows.Count > 0, icon: Symbol.SelectAll,
            shortcut: "Ctrl+A");
        return menu;
    }
}
