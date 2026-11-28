// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Controls.Omnibox;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
    private const string SearchScopeKeyword = "@search";
    private const string TabsGlyph = "\uE7C4";

    private void InitializeOmniboxScopes()
    {
        var window = this._window.ViewModel;
        var openFavorite = new AsyncRelayCommand<string>(id => this.GuardAsync(
            "Couldn't open the registry key", "Navigation canceled",
            () => this.ViewModel.NavigateAsync(this._window.Favorites.Bookmark(id!).GetLocation()), nameof(SuggestFavorites)),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        var openHistoryEntry = new AsyncRelayCommand<(int Index, RegistryLocation Location)>(entry => this.GuardAsync(
            "Couldn't open the registry key", "Navigation canceled",
            () => this.ViewModel.NavigateHistoryAsync(entry.Index, entry.Location), nameof(SuggestHistory)),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        var switchTab = new RelayCommand<WindowTab>(tab => window.SelectTab(tab!),
            tab => tab is not null && (window.Tabs.Contains(tab) || window.BottomTabs.Contains(tab)));
        var quickSearch = new RelayCommand<string>(query => this.RunQuickSearch(query!));

        OmniboxItem[] commands =
        [
            Command("New tab", window.NewTabCommand, "Ctrl+T", Symbol.Add, "Application"),
            Command("Open Settings", window.OpenSettingsCommand, "Ctrl+,", Symbol.Setting, "Application"),
            Command("Open Keyboard shortcuts", window.OpenShortcutsCommand, "Ctrl+/", Symbol.Keyboard, "Application"),
            Command("Refresh the current key", this.ViewModel.RefreshCommand, "F5", Symbol.Refresh, "Registry"),
            Command("Search the registry", this.ViewModel.Search.FindCommand, "Ctrl+Shift+F", Symbol.Find, "Registry"),
            Command("Go back", this.ViewModel.BackCommand, "Alt+Left", Symbol.Back, "Registry"),
            Command("Go forward", this.ViewModel.ForwardCommand, "Alt+Right", Symbol.Forward, "Registry"),
            ..this._window.ToolbarCommands.Select(entry => new OmniboxItem(
                (entry.Command.IsToggle ? "Toggle " : "Open ") + entry.Command.Label, string.Empty)
            {
                Command = entry.Command.Command,
                CommandParameter = entry.Command.CommandParameter,
                IconGlyph = SymbolGlyphs.Get(entry.Command.Symbol),
                RightText = entry.Command.ShortcutText,
                Group = "Tools"
            })
        ];

        var commandScope = new OmniboxScope("@commands", "Commands", "Type a command", query =>
        {
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return commands
                .Where(item => terms.All(term => Matches(term, item.Label, item.RightText ?? string.Empty)) &&
                               item.Command?.CanExecute(item.CommandParameter) == true)
                .ToArray();
        }, ">")
        {
            IconGlyph = "\uE756",
            EmptyMessage = "No matching commands",
            GroupPreviewCounts = new Dictionary<string, int> { ["Tools"] = 3 }
        };
        this.AddressBar.AddressPlaceholder = "Enter an address or type @";
        this.AddressBar.SuggestionActionProvider = id => (OmniboxAction[])
        [
            new("Show in Favorites organizer", () => this._window.RunFavoriteActionAsync("Show in Favorites organizer", () =>
            {
                this._window.ShowFavoriteInOrganizer(id);
                return Task.CompletedTask;
            }), CloseEditor: true),
            new("Remove favorite…", () => this._window.RunFavoriteActionAsync("Remove favorite", () =>
                FavoritesViewModel.RemoveItemsAsync(this._window.Favorites, new FavoriteDialogs(this._window.Dialogs),
                    (FavoriteItemId[])[new(id, false)])))
        ];
        this.AddressBar.Scopes = (OmniboxScope[])
        [
            new("@favorites", "Favorites", "Find favorites", query => this.SuggestFavorites(query.Trim(), openFavorite), "*")
            {
                IconGlyph = SymbolGlyphs.Get(Symbol.Favorite)
            },
            new("@history", "History", "Search this tab's history", query => this.SuggestHistory(query.Trim(), openHistoryEntry))
            {
                IconGlyph = SymbolGlyphs.Get(Symbol.Clock)
            },
            new("@tabs", "Tabs", "Search open tabs", query => this.SuggestTabs(query.Trim(), switchTab)) { IconGlyph = TabsGlyph },
            new(SearchScopeKeyword, "Search", "Search here", query => this.SuggestRegistrySearch(query, quickSearch), "?")
            {
                IconGlyph = SymbolGlyphs.Get(Symbol.Find)
            },
            commandScope
        ];
        this.AddShortcut(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
        {
            if (!this._window.Dialogs.IsOpen && !window.IsClosing)
            {
                this.AddressBar.BeginEdit(commandScope);
            }
        });
    }

    private OmniboxItem[] SuggestFavorites(string query, ICommand openFavorite)
    {
        var store = this._window.Favorites;
        return store.Favorites.Where(favorite => Matches(query, favorite.Name, favorite.Address))
            .Take(RegistryAddressSuggestions.SuggestionLimit)
            .Select(favorite => new OmniboxItem(favorite.Name, favorite.Address)
            {
                IconGlyph = SymbolGlyphs.Get(Symbol.Favorite),
                RightText = favorite.Is32Bit ? "32-bit" : "64-bit",
                ActionsKey = favorite.Id,
                Command = openFavorite,
                CommandParameter = favorite.Id
            }).ToArray();
    }

    private OmniboxItem[] SuggestHistory(string query, ICommand openHistoryEntry)
    {
        return this.ViewModel.GetHistory(false).Concat(this.ViewModel.GetHistory(true))
            .Where(entry => Matches(query, entry.Location.Name, entry.Location.Address))
            .Take(RegistryAddressSuggestions.SuggestionLimit)
            .Select(entry => new OmniboxItem(entry.Location.Name, entry.Location.Address)
            {
                IconGlyph = SymbolGlyphs.Get(Symbol.Clock),
                RightText = entry.Location.View == RegistryView.Registry32 ? "32-bit" : null,
                Command = openHistoryEntry,
                CommandParameter = entry
            }).ToArray();
    }

    private OmniboxItem[] SuggestTabs(string query, ICommand switchTab)
    {
        var window = this._window.ViewModel;
        return window.Tabs.Concat(window.BottomTabs)
            .Where(tab => Matches(query, tab.Title, tab.Tooltip))
            .Take(RegistryAddressSuggestions.SuggestionLimit)
            .Select(tab => new OmniboxItem(tab.Title, string.Empty)
            {
                Description = tab.Tooltip == tab.Title ? null : tab.Tooltip,
                IconGlyph = TabsGlyph,
                Command = switchTab,
                CommandParameter = tab
            }).ToArray();
    }

    private string[] SuggestRecentSearches(string query)
    {
        return this._window.ViewModel.RecentSearches.Where(term => term.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(5).ToArray();
    }

    private OmniboxItem[] SuggestRegistrySearch(string query, ICommand quickSearch)
    {
        var history = this.SuggestRecentSearches(query.Trim()).Select(term => new OmniboxItem(term, string.Empty)
        {
            Group = "Recent searches",
            IconGlyph = SymbolGlyphs.Get(Symbol.Clock),
            Command = quickSearch,
            CommandParameter = term
        });
        var advancedSearch = new OmniboxItem("Advanced search…", string.Empty)
        {
            IconGlyph = SymbolGlyphs.Get(Symbol.Filter),
            Command = this.ViewModel.Search.FindCommand,
            CommandParameter = query
        };
        if (string.IsNullOrWhiteSpace(query))
        {
            return [advancedSearch, ..history];
        }

        var location = this.ViewModel.Location;
        return
        [
            new OmniboxItem(location.IsComputer ? "Search all hives" : "Search this branch", string.Empty)
            {
                Description = location.IsComputer ? null : location.Address,
                IconGlyph = SymbolGlyphs.Get(Symbol.Find),
                Command = quickSearch,
                CommandParameter = query
            },
            advancedSearch,
            ..history
        ];
    }

    private static OmniboxItem Command(string label, ICommand command, string shortcut, Symbol icon, string group)
    {
        return new OmniboxItem(label, string.Empty)
        {
            Command = command,
            IconGlyph = SymbolGlyphs.Get(icon),
            RightText = shortcut,
            Group = group
        };
    }

    private static bool Matches(string query, string label, string detail)
    {
        return label.Contains(query, StringComparison.OrdinalIgnoreCase) || detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
