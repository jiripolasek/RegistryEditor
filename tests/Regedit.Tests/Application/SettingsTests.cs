// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class SettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RegeditSettingsTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        try
        {
            check(new AppSettings().AutoSyncTree, "New settings keep automatic tree tracking enabled");
            File.WriteAllText(file, "{}");
            var settings = AppSettings.Load(file);
            check(settings.AutoSyncTree, "Legacy settings retain automatic tree tracking when the preference is absent");
            check(
                !settings.ShowFavoritesBar && !settings.OpenFavoritesMenu,
                "Legacy settings keep both favorites access options off");
            check(
                settings.Theme == "Default"
                && settings.FontFamily == "Segoe UI Variable"
                && settings.FontSize == 14
                && settings.ShowStatusBar, "Settings page retains existing appearance defaults");
            check(settings.TerminalFontFamily == "Consolas" && settings.TerminalFontSize == 14,
                "Legacy settings retain the terminal renderer's default font");
            check(!settings.ExperimentalTerminalEnabled, "Legacy settings keep the experimental terminal disabled");
            settings.Favorites.Add(new Favorite("Example", "HKCU\\Software"));
            settings.Tabs.Add(new SavedTab("HKCU"));
            settings.ToolPanePinned = true;
            settings.UpdatePreferences(
                value =>
                {
                    value.Theme = "Dark";
                    value.FontFamily = " Consolas ";
                    value.FontSize = 18;
                    value.TerminalFontFamily = " Courier New ";
                    value.TerminalFontSize = 24;
                    value.ExperimentalTerminalEnabled = true;
                    value.ShowStatusBar = false;
                    value.ShowKeysInList = value.ShowParentKeyInList = value.ShowValuePreview = true;
                    value.AutoSyncTree = false;
                    value.ShowFavoritesBar = value.OpenFavoritesMenu = true;
                }, file);
            var loaded = AppSettings.Load(file);
            check(!loaded.AutoSyncTree, "Disabled automatic tree tracking survives saving and loading");
            check(loaded.TerminalFontFamily == "Courier New" && loaded.TerminalFontSize == 24,
                "Terminal font preferences round-trip through the settings file");
            check(loaded.ExperimentalTerminalEnabled, "Experimental terminal consent survives saving and loading");
            check(
                loaded.ShowFavoritesBar && loaded.OpenFavoritesMenu, "Favorites access options survive saving and loading");
            check(
                loaded.LoadError is null
                && loaded.Theme == "Dark"
                && loaded.FontFamily == "Consolas"
                && loaded.FontSize == 18
                && !loaded.ShowStatusBar
                && loaded.ShowKeysInList
                && loaded.ShowParentKeyInList
                && loaded.ShowValuePreview, "Settings page preferences round-trip through the existing settings file");
            check(
                loaded.Favorites.SequenceEqual(settings.Favorites) && loaded.Tabs.SequenceEqual(settings.Tabs) && loaded.ToolPanePinned,
                "Changing preferences preserves favorites tabs and panel state");

            var saved = File.ReadAllBytes(file);

            void Reject(Action<AppSettings> change, string message)
            {
                var rejected = false;
                try
                {
                    settings.UpdatePreferences(change, file);
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }

                check(
                    rejected
                    && settings.Theme == "Dark"
                    && settings.FontFamily == "Consolas"
                    && settings.FontSize == 18
                    && settings.TerminalFontFamily == "Courier New"
                    && settings.TerminalFontSize == 24
                    && settings.ExperimentalTerminalEnabled
                    && saved.SequenceEqual(File.ReadAllBytes(file)), message);
            }

            Reject(
                value =>
                {
                    value.Theme = "Light";
                    value.FontFamily = " ";
                }, "Invalid font restores all changed preferences and leaves the file intact");
            Reject(value => value.Theme = "Blue", "Unknown themes cannot be saved");
            Reject(value => { value.TerminalFontFamily = "Consolas"; value.TerminalFontSize = 33; },
                "Invalid terminal font size restores both terminal font preferences");
            foreach (var size in new[] { double.NaN, double.PositiveInfinity, 7, 33 })
            {
                Reject(value => value.FontSize = size, $"Invalid font size {size} leaves saved preferences intact");
            }

            var failed = false;
            using (var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    settings.UpdatePreferences(
                        value =>
                        {
                            value.Theme = "Light";
                            value.FontFamily = "Arial";
                            value.FontSize = 12;
                            value.ShowStatusBar = true;
                            value.ShowKeysInList = value.ShowParentKeyInList = value.ShowValuePreview = false;
                            value.AutoSyncTree = true;
                            value.ShowFavoritesBar = value.OpenFavoritesMenu = false;
                        }, file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }
            }

            check(
                failed
                && settings.Theme == "Dark"
                && settings.FontFamily == "Consolas"
                && settings.FontSize == 18
                && !settings.ShowStatusBar
                && settings.ShowKeysInList
                && settings.ShowParentKeyInList
                && settings.ShowValuePreview
                && !settings.AutoSyncTree
                && settings.ShowFavoritesBar
                && settings.OpenFavoritesMenu
                && saved.SequenceEqual(File.ReadAllBytes(file)),
                "Failed settings writes restore every preference without replacing the file");
            check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Failed preference writes leave no temporary settings files");
            settings.UpdatePreferences(
                value =>
                {
                    value.Theme = "Default";
                    value.ShowKeysInList = false;
                    value.AutoSyncTree = true;
                }, file);
            loaded = AppSettings.Load(file);
            check(loaded.AutoSyncTree, "Automatic tree tracking can be re-enabled after a failed settings write");
            check(
                loaded.Theme == "Default" && !loaded.ShowKeysInList && loaded.ShowParentKeyInList,
                "Settings can be saved after a failure and retain the parent-row preference while keys are hidden");

            var locations = new[]
            {
                new RegistryLocation(RegistryHive.CurrentUser, "Software\\Tail "),
                new RegistryLocation(RegistryHive.CurrentUser, "Software\\ ", View: RegistryView.Registry32),
                new RegistryLocation(RegistryHive.LocalMachine, "Software\\ \t\\Tail\u2003", "SERVER", RegistryView.Registry32)
            };
            foreach (var location in locations)
            {
                settings.AddFavorite("Exact " + location.Address, location.Address, location.View == RegistryView.Registry32, null);
                settings.Tabs.Add(new SavedTab(location.Address, location.View == RegistryView.Registry32));
                settings.Notes.Add(new KeyNote(location, "Note for " + location.SubKey));
                settings.Searches.Add(new SavedSearch("Exact", [location], new SearchOptions("Marker")));
            }

            settings.Workspaces.Add(
                new RegistryWorkspace(
                    "Exact paths", locations, settings.Searches.ToArray(),
                    locations.Where(location => location.Machine is null).Select(location => new WatchDefinition(location)).ToArray()));
            settings.Save(file);
            loaded = AppSettings.Load(file);
            check(loaded.LoadError is null, "Settings accept saved locations with significant whitespace");
            foreach (var location in locations)
            {
                check(
                    loaded.Favorites.Any(favorite => favorite.Matches(location))
                    && !loaded.Favorites.Any(favorite => favorite.Matches(location with { SubKey = location.SubKey.TrimEnd() })),
                    "Favorite persistence never redirects a whitespace-suffixed key to its trimmed neighbor");
                check(
                    loaded.Tabs.Any(tab => RegistryLocation.Parse(
                                               tab.Address, tab.Is32Bit ? RegistryView.Registry32 : RegistryView.Registry64)
                                           == location), "Saved tabs restore exact whitespace, computer, and view");
                check(
                    loaded.Notes.Any(note => note.Location == location)
                    && loaded.Searches.Any(search => search.Roots.Contains(location))
                    && loaded.Workspaces.Single().Tabs.Contains(location),
                    "Notes, saved searches, and workspaces retain exact key identity");
            }

            check(
                loaded.Workspaces.Single().Watches.Select(watch => watch.Location)
                    .SequenceEqual(locations.Where(location => location.Machine is null)),
                "Saved watches retain whitespace-only and trailing-space targets");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                File.Delete(path);
            }

            Directory.Delete(folder);
        }
    }
}
