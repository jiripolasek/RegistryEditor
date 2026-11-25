// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Text.Json;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Lists;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Persistence;

public sealed partial class AppSettings
{
    internal static readonly string Folder =
#if REGEDIT_UI_TESTS
        GetTestFolder();
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegeditModern");
#endif
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

#if REGEDIT_UI_TESTS
    private static string GetTestFolder()
    {
        var path = Environment.GetEnvironmentVariable("REGEDIT_UI_TEST_DATA");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Set REGEDIT_UI_TEST_DATA to an absolute disposable profile path.");
        }

        var folder = new DirectoryInfo(path);
        var normal =
 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegeditModern");
        var elevatedJournal =
 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RegeditModern.AdministratorJournal");
        for (var directory = folder; directory is not null; directory = directory.Parent)
        {
            var name = Path.TrimEndingDirectorySeparator(directory.FullName);
            if (name.Equals(normal, StringComparison.OrdinalIgnoreCase) || name.Equals(elevatedJournal, StringComparison.OrdinalIgnoreCase) ||
                (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new InvalidOperationException("UI tests require a disposable profile outside application data folders and directory links.");
            }
        }

        return folder.FullName;
    }
#endif

    public ObservableCollection<Favorite> Favorites { get; } = [];

    public List<SavedTab> Tabs { get; set; } = [];

    internal WindowLayout? RestoredLayout { get; private set; }

    public List<SavedSearch> Searches { get; set; } = [];

    internal IReadOnlyList<string> RecentSearches { get; private set; } = [];

    public List<KeyNote> Notes { get; set; } = [];

    public List<RegistryWorkspace> Workspaces { get; set; } = [];

    public string Theme { get; set; } = "Default";

    public string Backdrop { get; set; } = "Mica";

    public string WebSearchUrl { get; set; } = RegistryWebSearch.Bing;

    // Retained for existing profiles.
    public string FontFamily { get; set; } = "Segoe UI Variable";

    public double FontSize { get; set; } = 14;

    public double ListRowPadding { get; set; } = 3;

    public RegistryFont? DataFont { get; set; }

    internal static RegistryFont DefaultEditorFont { get; } = new(
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "Cascadia Code" : "Consolas", 11);

    public RegistryFont EditorFont { get; set; } = DefaultEditorFont;

    public RegistryFileLayout RegistryFileLayout { get; set; } = RegistryFileLayout.Split;

    public string PowerShellPath { get; set; } = string.Empty;

    public bool OfferWindowsPowerShellFallback { get; set; } = true;

    public bool ExperimentalTerminalEnabled { get; set; }

    internal const string DefaultTerminalFontFamily = "Consolas";
    internal const int DefaultTerminalFontSize = 14;

    public string TerminalFontFamily { get; set; } = DefaultTerminalFontFamily;

    public int TerminalFontSize { get; set; } = DefaultTerminalFontSize;

    public bool ShowListToolbar { get; set; } = true;

    /// <summary>Places the bottom panel tab strip below its content instead of above it.</summary>
    public bool PanelTabsAtBottom { get; set; }

    public bool ShowBackButton { get; set; } = true;

    public bool ShowForwardButton { get; set; } = true;

    public bool ShowUpButton { get; set; } = true;

    public bool ShowRefreshButton { get; set; } = true;

    public bool ShowSearchBox { get; set; } = true;

    public ToolbarButtons ToolbarButtons { get; set; }

    public bool ShowFavoritesBar { get; set; }

    public bool OpenFavoritesMenu { get; set; }

    internal RegistrySortColumn[] ListColumns { get; set; } = RegistryColumns.Defaults();

    /// <summary>
    ///     Saved column layouts keyed by list id. Replace the dictionary instead of mutating it so a failed save can roll
    ///     back to the previous instance.
    /// </summary>
    internal IReadOnlyDictionary<string, ColumnLayout> ColumnLayouts { get; set; }
        = new Dictionary<string, ColumnLayout>(StringComparer.Ordinal);


    public bool ShowStatusBar { get; set; } = true;

    public bool AlwaysShowAddressAsText { get; set; }

    public bool ShowKeysInList { get; set; }

    public bool ShowParentKeyInList { get; set; }

    public bool AutoSyncTree { get; set; } = true;

    public bool ShowValuePreview { get; set; }

    public bool ToolPanePinned { get; set; }

    public double ToolPaneWidth { get; set; } = 360;

    public string SelectedTool { get; set; } = "Favorites";

    public string? LoadError { get; private set; }

    private bool _preserveUnreadableFile;

    public AppSettings()
    {
        this._baseline = this.Capture();
    }

    public static AppSettings Load()
    {
        return Load(FilePath);
    }

    internal static AppSettings Load(string filePath)
    {
        var settings = new AppSettings();
        if (!File.Exists(filePath))
        {
            return settings;
        }

        try
        {
            settings._loadedContent = ReadSettingsFile(filePath);
            var saved = ReadSavedSettings(settings._loadedContent);
            if (saved is null)
            {
                return settings;
            }

            ValidateFavorites(saved.Favorites ?? [], saved.FavoriteFolders ?? []);
            foreach (var folder in saved.FavoriteFolders ?? [])
            {
                settings.FavoriteFolders.Add(folder);
            }

            foreach (var favorite in saved.Favorites ?? [])
            {
                settings.Favorites.Add(favorite);
            }

            settings.Tabs = (saved.Tabs ?? []).Where(tab => tab is not null && !string.IsNullOrWhiteSpace(tab.Address)).ToList();
            settings._sessions = saved.WindowSessions
                                 ?? (settings.Tabs.Count == 0 ? [] : [new SavedWindowSession("legacy", settings.Tabs.ToArray())]);
            ValidateSessions(settings._sessions);
            settings.RestoredLayout = settings._sessions.FirstOrDefault()?.Layout?.Normalize();
            settings.Tabs = settings._sessions.SelectMany(session => session.Tabs)
                .Select(tab => tab with { Layout = tab.Layout?.Normalize() }).ToList();
            settings._restoredSessionIds = settings._sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            settings.Theme = saved.Theme is "Light" or "Dark" ? saved.Theme : "Default";
            settings.Backdrop = saved.Backdrop == "Acrylic" ? "Acrylic" : "Mica";
            settings.WebSearchUrl = RegistryWebSearch.IsValidTemplate(saved.WebSearchUrl)
                ? saved.WebSearchUrl!.Trim()
                : RegistryWebSearch.Bing;
            settings.HiddenJumpListFavorites = (saved.HiddenJumpListFavorites ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            settings.FontFamily = string.IsNullOrWhiteSpace(saved.FontFamily) ? "Segoe UI Variable" : saved.FontFamily;
            settings.FontSize = double.IsFinite(saved.FontSize) ? Math.Clamp(saved.FontSize, 8, 32) : 14;
            settings.ListRowPadding = double.IsFinite(saved.ListRowPadding) ? Math.Clamp(saved.ListRowPadding, 0, 10) : 3;
            settings.DataFont
                = saved.DataFont is { Family: { Length: > 0 }, Size: >= 8 and <= 32 } font && !string.IsNullOrWhiteSpace(font.Family)
                    ? font with { Family = font.Family.Trim() }
                    : null;
            settings.EditorFont
                = saved.EditorFont is { Family: { Length: > 0 }, Size: >= 8 and <= 32 } editorFont
                  && !string.IsNullOrWhiteSpace(editorFont.Family)
                    ? editorFont with { Family = editorFont.Family.Trim() }
                    : DefaultEditorFont;
            settings.RegistryFileLayout = Enum.IsDefined(saved.RegistryFileLayout) ? saved.RegistryFileLayout : RegistryFileLayout.Split;
            settings.PowerShellPath = saved.PowerShellPath?.Trim() ?? string.Empty;
            settings.OfferWindowsPowerShellFallback = saved.OfferWindowsPowerShellFallback;
            settings.ExperimentalTerminalEnabled = saved.ExperimentalTerminalEnabled;
            settings.TerminalFontFamily = string.IsNullOrWhiteSpace(saved.TerminalFontFamily)
                ? DefaultTerminalFontFamily
                : saved.TerminalFontFamily.Trim();
            settings.TerminalFontSize = Math.Clamp(saved.TerminalFontSize, 8, 32);
            settings.ShowListToolbar = saved.ShowListToolbar;
            settings.PanelTabsAtBottom = saved.PanelTabsAtBottom;
            settings.ToolbarButtons = saved.ToolbarButtons & ToolbarButtons.All;
            settings.ShowFavoritesBar = saved.ShowFavoritesBar;
            settings.OpenFavoritesMenu = saved.OpenFavoritesMenu;
            settings.ShowBackButton = saved.ShowBackButton;
            settings.ShowForwardButton = saved.ShowForwardButton;
            settings.ShowUpButton = saved.ShowUpButton;
            settings.ShowRefreshButton = saved.ShowRefreshButton;
            settings.ShowSearchBox = saved.ShowSearchBox;
            settings.ListColumns = RegistryColumns.FromNames(saved.ListColumns);
            settings.ColumnLayouts = (saved.ColumnLayouts ?? [])
                .Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value?.Columns is not null).ToDictionary(
                    pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            settings.ShowStatusBar = saved.ShowStatusBar;
            settings.AlwaysShowAddressAsText = saved.AlwaysShowAddressAsText;
            settings.ShowKeysInList = saved.ShowKeysInList;
            settings.ShowParentKeyInList = saved.ShowParentKeyInList;
            settings.AutoSyncTree = saved.AutoSyncTree;
            settings.ShowValuePreview = saved.ShowValuePreview;
            settings.ToolPanePinned = saved.ToolPanePinned;
            settings.ToolPaneWidth = double.IsFinite(saved.ToolPaneWidth) ? Math.Clamp(saved.ToolPaneWidth, 300, 680) : 360;
            settings.SelectedTool = saved.SelectedTool is "Watches" or "Journal" or "Notes" ? saved.SelectedTool : "Favorites";
            settings.RecentSearches = NormalizeRecentSearches(saved.RecentSearches ?? []);
            settings.Searches = saved.Searches ?? [];
            foreach (var search in settings.Searches)
            {
                RegistrySearch.Validate(search);
            }

            settings.Notes = saved.Notes ?? [];
            foreach (var note in settings.Notes)
            {
                if (note is null || note.Text is null)
                {
                    throw new JsonException("A note has no text.");
                }

                note.Validate();
            }

            settings.Workspaces = saved.Workspaces ?? [];
            foreach (var workspace in settings.Workspaces)
            {
                if (workspace is null)
                {
                    throw new JsonException("A workspace is missing.");
                }

                workspace.Validate();
            }

            settings._baseline = settings.Capture();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            settings.LoadError = $"Settings could not be read: {ex.Message}";
            settings._preserveUnreadableFile = true;
        }

        return settings;
    }

    public void Save()
    {
        this.Save(FilePath);
    }

    internal void UpdatePreferences(Action<AppSettings> change, string? filePath = null)
    {
        var previous = (this.Theme, this.FontFamily, this.FontSize, this.ShowStatusBar, this.ShowKeysInList, this.ShowParentKeyInList,
            this.ShowValuePreview, this.AutoSyncTree, this.DataFont, this.ShowListToolbar, this.ListColumns, this.ListRowPadding,
            this.ToolbarButtons, this.PanelTabsAtBottom, this.ColumnLayouts, this.ShowBackButton, this.ShowForwardButton, this.ShowUpButton,
            this.ShowRefreshButton, this.AlwaysShowAddressAsText, this.Backdrop, this.WebSearchUrl, this.RegistryFileLayout,
            this.EditorFont, this.ShowSearchBox, this.ShowFavoritesBar, this.OpenFavoritesMenu,
            this.PowerShellPath, this.OfferWindowsPowerShellFallback, this.ExperimentalTerminalEnabled,
            this.TerminalFontFamily, this.TerminalFontSize);
        try
        {
            change(this);
            if (this.Theme is not ("Default" or "Light" or "Dark"))
            {
                throw new ArgumentException("Choose an app theme.");
            }

            if (this.Backdrop is not ("Mica" or "Acrylic"))
            {
                throw new ArgumentException("Choose a window backdrop.");
            }

            if (string.IsNullOrWhiteSpace(this.FontFamily) || !double.IsFinite(this.FontSize) || this.FontSize is < 8 or > 32)
            {
                throw new ArgumentException("Enter a font family and a size between 8 and 32.");
            }

            if (this.DataFont is { } dataFont)
            {
                if (string.IsNullOrWhiteSpace(dataFont.Family) || !double.IsFinite(dataFont.Size) || dataFont.Size is < 8 or > 32)
                {
                    throw new ArgumentException("Enter a Data column font family and a size between 8 and 32.");
                }

                this.DataFont = dataFont with { Family = dataFont.Family.Trim() };
            }

            if (!Enum.IsDefined(this.RegistryFileLayout))
            {
                throw new ArgumentException("Choose an editor layout.");
            }

            if (this.EditorFont is not { } editorFont
                || string.IsNullOrWhiteSpace(editorFont.Family)
                || !double.IsFinite(editorFont.Size)
                || editorFont.Size is < 8 or > 32)
            {
                throw new ArgumentException("Enter an editor font family and a size between 8 and 32 points.");
            }

            this.EditorFont = editorFont with { Family = editorFont.Family.Trim() };

            if (string.IsNullOrWhiteSpace(this.TerminalFontFamily) || this.TerminalFontSize is < 8 or > 32)
            {
                throw new ArgumentException("Enter a terminal font family and a size between 8 and 32 points.");
            }

            this.TerminalFontFamily = this.TerminalFontFamily.Trim();

            if (!double.IsFinite(this.ListRowPadding) || this.ListRowPadding is < 0 or > 10)
            {
                throw new ArgumentException("Choose list spacing between 0 and 10.");
            }

            if ((this.ToolbarButtons & ~ToolbarButtons.All) != 0)
            {
                throw new ArgumentException("Choose supported toolbar buttons.");
            }

            this.FontFamily = this.FontFamily.Trim();
            this.WebSearchUrl = RegistryWebSearch.ValidateTemplate(this.WebSearchUrl);
            this.Save(filePath ?? FilePath);
        }
        catch
        {
            (this.Theme, this.FontFamily, this.FontSize, this.ShowStatusBar, this.ShowKeysInList, this.ShowParentKeyInList,
                this.ShowValuePreview, this.AutoSyncTree, this.DataFont, this.ShowListToolbar, this.ListColumns, this.ListRowPadding,
                this.ToolbarButtons, this.PanelTabsAtBottom, this.ColumnLayouts, this.ShowBackButton, this.ShowForwardButton,
                this.ShowUpButton, this.ShowRefreshButton, this.AlwaysShowAddressAsText, this.Backdrop, this.WebSearchUrl,
                this.RegistryFileLayout, this.EditorFont, this.ShowSearchBox, this.ShowFavoritesBar, this.OpenFavoritesMenu,
                this.PowerShellPath, this.OfferWindowsPowerShellFallback, this.ExperimentalTerminalEnabled,
                this.TerminalFontFamily, this.TerminalFontSize) = previous;
            throw;
        }
    }

    internal void Save(string filePath, NoteScopeMove? noteMove = null)
    {
        this.SaveCore(filePath, noteMove: noteMove);
    }

    private sealed record SavedSettings(
        Favorite[]? Favorites,
        List<SavedTab>? Tabs,
        string? Theme,
        string FontFamily = "Segoe UI Variable",
        double FontSize = 14,
        bool ShowStatusBar = true,
        List<SavedSearch>? Searches = null,
        List<KeyNote>? Notes = null,
        List<RegistryWorkspace>? Workspaces = null,
        bool ShowKeysInList = false,
        bool ShowValuePreview = false,
        bool ToolPanePinned = false,
        double ToolPaneWidth = 360,
        string SelectedTool = "Favorites",
        FavoriteFolder[]? FavoriteFolders = null,
        bool ShowParentKeyInList = false,
        bool AutoSyncTree = true,
        SavedWindowSession[]? WindowSessions = null,
        RegistryFont? DataFont = null,
        bool ShowListToolbar = true,
        string[]? ListColumns = null,
        double ListRowPadding = 3,
        ToolbarButtons ToolbarButtons = ToolbarButtons.None,
        bool PanelTabsAtBottom = false,
        Dictionary<string, ColumnLayout>? ColumnLayouts = null,
        bool ShowBackButton = true,
        bool ShowForwardButton = true,
        bool ShowUpButton = true,
        bool ShowRefreshButton = true,
        bool AlwaysShowAddressAsText = false,
        string Backdrop = "Mica",
        string[]? HiddenJumpListFavorites = null,
        string? WebSearchUrl = null,
        RegistryFileLayout RegistryFileLayout = RegistryFileLayout.Split,
        RegistryFont? EditorFont = null,
        string[]? RecentSearches = null,
        bool ShowSearchBox = true,
        bool ShowFavoritesBar = false,
        bool OpenFavoritesMenu = false,
        string? PowerShellPath = null,
        bool OfferWindowsPowerShellFallback = true,
        string? TerminalFontFamily = null,
        int TerminalFontSize = DefaultTerminalFontSize,
        bool ExperimentalTerminalEnabled = false);
}
