// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Lists;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels;

internal sealed partial class SettingsViewModel(AppSettings settings, Action<Action<AppSettings>> save) : ObservableObject
{
    internal event Action<string>? PreferenceSaved;

    internal event Action<Exception>? SaveFailed;

    private bool _defaultDataFontRequested;

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    public partial bool IsSaveErrorOpen { get; set; }

    [ObservableProperty]
    public partial string? LogsError { get; private set; }

    [ObservableProperty]
    public partial bool IsLogsErrorOpen { get; set; }

    [ObservableProperty]
    public partial string[]? FontFamilies { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingFontFamilies { get; private set; }

    [ObservableProperty]
    public partial string? FontFamiliesError { get; private set; }

    [ObservableProperty]
    public partial string DefaultDataFontFamily { get; private set; } = MonospaceFonts.FallbackFamily;

    public RegistryFont DataFont =>
        settings.DataFont ?? new RegistryFont(this.DefaultDataFontFamily, settings.FontSize);

    public int ThemeIndex
    {
        get => settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        set
        {
            if (value is >= 0 and <= 2)
            {
                this.Update(this.ThemeIndex, value, s => s.Theme = value switch { 1 => "Light", 2 => "Dark", _ => "Default" });
            }
        }
    }

    public int BackdropIndex
    {
        get => settings.Backdrop == "Acrylic" ? 1 : 0;
        set
        {
            if (value is 0 or 1)
            {
                this.Update(this.BackdropIndex, value, s => s.Backdrop = value == 1 ? "Acrylic" : "Mica");
            }
        }
    }

    public bool UseCustomDataFont
    {
        get => settings.DataFont is not null;
        set => this.Update(this.UseCustomDataFont, value, s => s.DataFont = value ? this.DataFont : null);
    }

    public string WebSearchUrl
    {
        get => settings.WebSearchUrl;
        set => this.Update(this.WebSearchUrl, value, s => s.WebSearchUrl = value);
    }

    public string DataFontFamily
    {
        get => this.DataFont.Family;
        set
        {
            if (settings.DataFont is { } font)
            {
                this.Update(this.DataFontFamily, value, s => s.DataFont = font with { Family = value });
            }
        }
    }

    public double DataFontSize
    {
        get => this.DataFont.Size;
        set
        {
            if (settings.DataFont is { } font)
            {
                this.Update(this.DataFontSize, value, s => s.DataFont = font with { Size = value });
            }
        }
    }

    public int EditorLayoutIndex
    {
        get => (int)settings.RegistryFileLayout;
        set
        {
            if (value is >= 0 and <= 2)
            {
                this.Update(this.EditorLayoutIndex, value, s => s.RegistryFileLayout = (RegistryFileLayout)value);
            }
        }
    }

    public string EditorFontFamily
    {
        get => settings.EditorFont.Family;
        set => this.Update(this.EditorFontFamily, value, s => s.EditorFont = s.EditorFont with { Family = value });
    }

    public double EditorFontSize
    {
        get => settings.EditorFont.Size;
        set => this.Update(this.EditorFontSize, value, s => s.EditorFont = s.EditorFont with { Size = value });
    }

    public bool ShowListToolbar
    {
        get => settings.ShowListToolbar;
        set => this.Update(this.ShowListToolbar, value, s => s.ShowListToolbar = value);
    }

    public bool ShowBackButton
    {
        get => settings.ShowBackButton;
        set => this.Update(this.ShowBackButton, value, s => s.ShowBackButton = value);
    }

    public bool ShowForwardButton
    {
        get => settings.ShowForwardButton;
        set => this.Update(this.ShowForwardButton, value, s => s.ShowForwardButton = value);
    }

    public bool ShowUpButton
    {
        get => settings.ShowUpButton;
        set => this.Update(this.ShowUpButton, value, s => s.ShowUpButton = value);
    }

    public bool ShowRefreshButton
    {
        get => settings.ShowRefreshButton;
        set => this.Update(this.ShowRefreshButton, value, s => s.ShowRefreshButton = value);
    }

    public ToolbarButtons ToolbarButtons => settings.ToolbarButtons;

    public bool PanelTabsAtBottom
    {
        get => settings.PanelTabsAtBottom;
        set => this.Update(this.PanelTabsAtBottom, value, s => s.PanelTabsAtBottom = value);
    }

    public double ListRowPadding
    {
        get => settings.ListRowPadding;
        set
        {
            if (double.IsFinite(value))
            {
                var padding = Math.Clamp(Math.Round(value), 0, 10);
                this.Update(this.ListRowPadding, padding, s => s.ListRowPadding = padding);
            }
        }
    }

    public IReadOnlyList<RegistrySortColumn> ListColumns => settings.ListColumns;

    public bool ShowStatusBar
    {
        get => settings.ShowStatusBar;
        set => this.Update(this.ShowStatusBar, value, s => s.ShowStatusBar = value);
    }

    public bool AlwaysShowAddressAsText
    {
        get => settings.AlwaysShowAddressAsText;
        set => this.Update(this.AlwaysShowAddressAsText, value, s => s.AlwaysShowAddressAsText = value);
    }

    public bool ShowKeysInList
    {
        get => settings.ShowKeysInList;
        set => this.Update(this.ShowKeysInList, value, s => s.ShowKeysInList = value);
    }

    public bool ShowParentKeyInList
    {
        get => settings.ShowParentKeyInList;
        set => this.Update(this.ShowParentKeyInList, value, s => s.ShowParentKeyInList = value);
    }

    public bool ShowValuePreview
    {
        get => settings.ShowValuePreview;
        set => this.Update(this.ShowValuePreview, value, s => s.ShowValuePreview = value);
    }

    public bool AutoSyncTree
    {
        get => settings.AutoSyncTree;
        set => this.Update(this.AutoSyncTree, value, s => s.AutoSyncTree = value);
    }

    partial void OnDefaultDataFontFamilyChanged(string value)
    {
        if (!this.UseCustomDataFont)
        {
            this.OnPropertyChanged(nameof(this.DataFont));
            this.OnPropertyChanged(nameof(this.DataFontFamily));
        }
    }

    internal async Task LoadDefaultDataFontAsync(Func<string> load)
    {
        if (this._defaultDataFontRequested)
        {
            return;
        }

        this._defaultDataFontRequested = true;
        try
        {
            var family = await Task.Run(load);
            if (!string.IsNullOrWhiteSpace(family))
            {
                this.DefaultDataFontFamily = family;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Monospace font preference unavailable ({ex.GetType().Name}).");
        }
    }

    internal async Task LoadFontFamiliesAsync(Func<string[]> load)
    {
        if (this.IsLoadingFontFamilies || this.FontFamilies is not null)
        {
            return;
        }

        this.IsLoadingFontFamilies = true;
        this.FontFamiliesError = null;
        try
        {
            this.FontFamilies = await Task.Run(() => load().Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
        }
        catch (Exception)
        {
            this.FontFamiliesError = "Installed fonts could not be loaded. You can still enter a font family name.";
        }
        finally
        {
            this.IsLoadingFontFamilies = false;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLogging.Folder);
            Process.Start(new ProcessStartInfo(AppLogging.Folder) { UseShellExecute = true });
            this.IsLogsErrorOpen = false;
            this.LogsError = null;
        }
        catch (Exception ex)
        {
            this.LogsError = ex.Message;
            this.IsLogsErrorOpen = true;
        }
    }

    [RelayCommand]
    private void SetWebSearchUrl(string url)
    {
        this.WebSearchUrl = url;
    }

    [RelayCommand]
    private void ToggleToolbarButton(ToolbarButtons button)
    {
        this.Update(this.ToolbarButtons, this.ToolbarButtons ^ button, s => s.ToolbarButtons ^= button, nameof(this.ToolbarButtons));
    }

    [RelayCommand]
    private void HideToolbarButton(ToolbarButtons button)
    {
        this.Update(this.ToolbarButtons, this.ToolbarButtons & ~button, s => s.ToolbarButtons &= ~button, nameof(this.ToolbarButtons));
    }

    /// <summary>The saved column layout for a list, or null to use the list's defaults.</summary>
    internal ColumnLayout? ColumnLayout(string listId)
    {
        return settings.ColumnLayouts.GetValueOrDefault(listId);
    }

    internal void SaveColumnLayout(string listId, ColumnLayout layout)
    {
        var current = this.ColumnLayout(listId);
        if (current is not null && current.IsEquivalentTo(layout))
        {
            return;
        }

        this.Update(
            current, layout,
            s => s.ColumnLayouts = new Dictionary<string, ColumnLayout>(s.ColumnLayouts, StringComparer.Ordinal) { [listId] = layout },
            nameof(this.ColumnLayout));
    }

    /// <summary>Replaces the registry list's visible columns and their order.</summary>
    internal void SetListColumns(IReadOnlyList<RegistrySortColumn> columns)
    {
        if (columns.Count > 0 && !columns.SequenceEqual(settings.ListColumns))
        {
            this.SaveColumns(columns.Distinct().ToArray());
        }
    }

    [RelayCommand]
    private void ToggleColumn(RegistrySortColumn column)
    {
        var visible = this.ListColumns.ToList();
        if (visible.Contains(column))
        {
            if (visible.Count == 1)
            {
                return;
            }

            visible.Remove(column);
        }
        else
        {
            visible.Add(column);
        }

        this.SaveColumns(visible.ToArray());
    }

    [RelayCommand]
    private void MoveColumnLeft(RegistrySortColumn column)
    {
        this.MoveColumn(column, Array.IndexOf(settings.ListColumns, column) - 1);
    }

    [RelayCommand]
    private void MoveColumnRight(RegistrySortColumn column)
    {
        this.MoveColumn(column, Array.IndexOf(settings.ListColumns, column) + 1);
    }

    internal void MoveColumn(RegistrySortColumn column, int destination)
    {
        var visible = this.ListColumns.ToList();
        var index = visible.IndexOf(column);
        if (index < 0 || destination < 0 || destination >= visible.Count || index == destination)
        {
            return;
        }

        visible.RemoveAt(index);
        visible.Insert(destination, column);
        this.SaveColumns(visible.ToArray());
    }

    private void SaveColumns(RegistrySortColumn[] columns)
    {
        this.Update(settings.ListColumns, columns, s => s.ListColumns = columns, nameof(this.ListColumns));
    }

    private void Update<T>(T current, T value, Action<AppSettings> change, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        try
        {
            save(change);
        }
        catch (Exception ex)
        {
            this.SaveError = ex.Message;
            this.IsSaveErrorOpen = true;
            this.OnPropertyChanged(property);
            this.SaveFailed?.Invoke(ex);
            return;
        }

        this.IsSaveErrorOpen = false;
        this.SaveError = null;
        this.OnPropertyChanged(property);
        if (property == nameof(this.UseCustomDataFont))
        {
            this.OnPropertyChanged(nameof(this.DataFontFamily));
            this.OnPropertyChanged(nameof(this.DataFontSize));
        }

        this.PreferenceSaved?.Invoke(property);
    }
}
