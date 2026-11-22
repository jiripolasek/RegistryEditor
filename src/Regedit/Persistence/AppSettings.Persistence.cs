// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Lists;
using JPSoftworks.Regedit.Models.Settings;

namespace JPSoftworks.Regedit.Persistence;

public sealed partial class AppSettings
{
    private const int RecentSearchLimit = 20;

    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private JsonObject _baseline;
    private string? _loadedContent;
    private HashSet<string> _restoredSessionIds = [];
    private SavedWindowSession[] _sessions = [];
    private WindowLayout? _windowLayout;

    private JsonObject Capture()
    {
        return JsonSerializer.SerializeToNode(
            new SavedSettings(
                this.Favorites.ToArray(), this.Tabs, this.Theme, this.FontFamily, this.FontSize, this.ShowStatusBar, this.Searches,
                this.Notes, this.Workspaces, this.ShowKeysInList, this.ShowValuePreview, this.ToolPanePinned, this.ToolPaneWidth,
                this.SelectedTool, this.FavoriteFolders.ToArray(), this.ShowParentKeyInList, this.AutoSyncTree, this._sessions,
                this.DataFont, this.ShowListToolbar, this.ListColumns.Select(column => column.ToString()).ToArray(), this.ListRowPadding,
                this.ToolbarButtons, this.PanelTabsAtBottom,
                new Dictionary<string, ColumnLayout>(this.ColumnLayouts, StringComparer.Ordinal), this.ShowBackButton,
                this.ShowForwardButton, this.ShowUpButton, this.ShowRefreshButton, this.AlwaysShowAddressAsText, this.Backdrop,
                this.HiddenJumpListFavorites.ToArray(), this.WebSearchUrl, this.RegistryFileLayout, this.EditorFont,
                this.RecentSearches.ToArray(), this.ShowSearchBox, this.ShowFavoritesBar, this.OpenFavoritesMenu,
                this.PowerShellPath, this.OfferWindowsPowerShellFallback, this.TerminalFontFamily, this.TerminalFontSize),
            SettingsJsonContext.Default.SavedSettings)!.AsObject();
    }

    private static string ReadSettingsFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SavedSettings? ReadSavedSettings(string text)
    {
        var document = JsonNode.Parse(text);
        if (document is null)
        {
            return null;
        }

        if (document is not JsonObject)
        {
            throw new JsonException("Settings must be a JSON object.");
        }

        if (document["Favorites"] is JsonArray favorites)
        {
            for (var index = 0; index < favorites.Count; index++)
            {
                if (favorites[index] is JsonObject favorite && favorite["Id"] is null)
                {
                    // Every process derives the same identity until a legacy bookmark is next saved.
                    favorite["Id"]
                        = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index + ":" + favorite.ToJsonString())))[..32];
                }
            }
        }

        return document.Deserialize(SettingsJsonContext.Default.SavedSettings);
    }

    internal void SaveSession(List<SavedTab> tabs, bool replaceRestored, string? filePath = null, WindowLayout? layout = null)
    {
        var previous = this.Tabs;
        var previousLayout = this._windowLayout;
        this.Tabs = tabs;
        this._windowLayout = layout?.Normalize();
        try
        {
            this.SaveCore(filePath ?? FilePath, true, replaceRestored);
        }
        catch
        {
            this.Tabs = previous;
            this._windowLayout = previousLayout;
            throw;
        }
    }

    internal bool RecordSearch(string query, string filePath)
    {
        if (this.LoadError is not null || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        this.SaveCore(filePath, recentSearch: query);
        return true;
    }

    internal static string[] NormalizeRecentSearches(IEnumerable<string> queries)
    {
        return queries.Where(query => !string.IsNullOrWhiteSpace(query)).Distinct(StringComparer.Ordinal)
            .Take(RecentSearchLimit).ToArray();
    }

    private void SaveCore(
        string filePath, bool captureSession = false, bool replaceRestored = true, string? recentSearch = null)
    {
        var local = this.Capture();
        var sessions = this._sessions;
        if (captureSession || !JsonNode.DeepEquals(local["Tabs"], this._baseline["Tabs"]))
        {
            sessions = this._sessions
                .Where(session => session.Id != this._sessionId && (!replaceRestored || !this._restoredSessionIds.Contains(session.Id)))
                .Concat(
                    this.Tabs.Count == 0 && this._windowLayout is null
                        ? []
                        : new[] { new SavedWindowSession(this._sessionId, this.Tabs.ToArray(), this._windowLayout) }).ToArray();
            local["WindowSessions"] = JsonSerializer.SerializeToNode(sessions, SettingsJsonContext.Default.SavedWindowSessionArray);
        }

        ValidateSaved(local.Deserialize(SettingsJsonContext.Default.SavedSettings)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        using var lease = AcquireSettingsLock(filePath + ".lock");
        if (this._loadedContent is not null && !File.Exists(filePath))
        {
            throw Conflict("settings file was removed");
        }

        var current = Load(filePath);
        if (this._preserveUnreadableFile && current._loadedContent != this._loadedContent)
        {
            throw Conflict("unreadable settings file changed");
        }

        if (current.LoadError is not null
            && !(this._preserveUnreadableFile && this._loadedContent is not null && current._loadedContent == this._loadedContent))
        {
            throw new IOException("The current settings file cannot be read. It has not been overwritten. " + current.LoadError);
        }

        var latest = current.LoadError is null ? current.Capture() : this._baseline;
        var merged = new JsonObject();
        foreach (var (name, value) in local)
        {
            if (name == "Tabs")
            {
                continue;
            }

            merged[name] = MergeValue(name, this._baseline[name], value, latest[name])?.DeepClone();
        }

        if (recentSearch is not null)
        {
            merged[nameof(RecentSearches)] = JsonSerializer.SerializeToNode(
                NormalizeRecentSearches(current.RecentSearches.Prepend(recentSearch)), SettingsJsonContext.Default.StringArray);
        }

        var saved = merged.Deserialize(SettingsJsonContext.Default.SavedSettings)!;
        saved = saved with { Tabs = saved.WindowSessions!.SelectMany(session => session.Tabs).ToList() };
        try
        {
            ValidateSaved(saved);
        }
        catch (ArgumentException ex)
        {
            throw new IOException(
                "The combined settings would be invalid. Reopen a window to load the latest settings before retrying. " + ex.Message, ex);
        }

        var text = JsonSerializer.Serialize(saved, SettingsJsonContext.Default.SavedSettings);
        var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            if (this._preserveUnreadableFile && File.Exists(filePath))
            {
                File.Copy(filePath, filePath + ".unreadable." + Guid.NewGuid().ToString("N"));
            }

            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    File.Move(temporary, filePath, true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33)
                {
                    // Readers and file-system filters can briefly retain the previous replacement.
                    if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3))
                    {
                        throw;
                    }

                    Thread.Sleep(25);
                }
            }

            // Keep this window's baseline; unseen changes from other windows are not local deletions.
            this.RecentSearches = saved.RecentSearches ?? [];
            local[nameof(RecentSearches)] = merged[nameof(RecentSearches)]?.DeepClone();
            this._baseline = local;
            this._sessions = sessions;
            this._loadedContent = text;
            this._preserveUnreadableFile = false;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static FileStream AcquireSettingsLock(string filePath)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3))
                {
                    throw new IOException("Another window is saving settings. Try again shortly.", ex);
                }

                Thread.Sleep(25);
            }
        }
    }

    private static JsonNode? MergeValue(string name, JsonNode? before, JsonNode? local, JsonNode? current)
    {
        if (JsonNode.DeepEquals(local, before))
        {
            return current;
        }

        if (JsonNode.DeepEquals(current, before) || JsonNode.DeepEquals(local, current))
        {
            return local;
        }

        // Column layouts merge per list: another window's change to a different list is kept, and on the same list
        // the latest local change wins because a layout is cheap to redo.
        if (name == "ColumnLayouts" && local is JsonObject changedLayouts)
        {
            var mergedLayouts = (current as JsonObject)?.DeepClone().AsObject() ?? [];
            foreach (var (id, layout) in changedLayouts)
            {
                if (!JsonNode.DeepEquals(layout, (before as JsonObject)?[id]))
                {
                    mergedLayouts[id] = layout?.DeepClone();
                }
            }

            return mergedLayouts;
        }

        if (before is JsonArray original && local is JsonArray changed && current is JsonArray latest)
        {
            return MergeItems(name, original, changed, latest);
        }

        throw Conflict(name);
    }

    private static JsonArray MergeItems(string name, JsonArray before, JsonArray local, JsonArray current)
    {
        string Key(JsonNode? item)
        {
            if (item is null)
            {
                throw Conflict(name);
            }

            return name switch
            {
                "Favorites" or "FavoriteFolders" or "WindowSessions" => item["Id"]!.GetValue<string>(),
                "HiddenJumpListFavorites" => item.GetValue<string>(),
                "Searches" or "Workspaces" => item["Name"]!.GetValue<string>().ToUpperInvariant(),
                "Notes" => JsonSerializer.Serialize(
                    new[]
                    {
                        item["Location"]!["Hive"]?.ToJsonString(), item["Location"]!["View"]?.ToJsonString(),
                        item["Location"]!["Machine"]?.GetValue<string>().ToUpperInvariant(),
                        item["Location"]!["SubKey"]!.GetValue<string>().ToUpperInvariant()
                    }, SettingsJsonContext.Default.StringArray),
                _ => throw Conflict(name)
            };
        }

        Dictionary<string, JsonNode?> Index(JsonArray items)
        {
            var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!result.TryAdd(Key(item), item))
                {
                    throw Conflict(name + " contains duplicate entries");
                }
            }

            return result;
        }

        var original = Index(before);
        var changed = Index(local);
        var latest = Index(current);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var key in original.Keys.Concat(changed.Keys).Concat(latest.Keys).Distinct(StringComparer.Ordinal))
        {
            original.TryGetValue(key, out var old);
            changed.TryGetValue(key, out var edit);
            latest.TryGetValue(key, out var now);
            if (JsonNode.DeepEquals(edit, old))
            {
                values[key] = now;
            }
            else if (JsonNode.DeepEquals(now, old) || JsonNode.DeepEquals(edit, now))
            {
                values[key] = edit;
            }
            else if (name == "WindowSessions")
            {
                // Restoring a live window's snapshot must not discard its updates or prevent it from saving again.
                values[key] = edit ?? now;
            }
            else
            {
                throw Conflict(name);
            }
        }

        var common = original.Keys.Where(key => changed.ContainsKey(key) && latest.ContainsKey(key)).ToHashSet(StringComparer.Ordinal);
        var oldOrder = original.Keys.Where(common.Contains).ToArray();
        var localOrder = changed.Keys.Where(common.Contains).ToArray();
        var currentOrder = latest.Keys.Where(common.Contains).ToArray();
        var reordered = !oldOrder.SequenceEqual(localOrder);
        if (reordered && !oldOrder.SequenceEqual(currentOrder) && !localOrder.SequenceEqual(currentOrder))
        {
            throw Conflict(name + " order");
        }

        var order = reordered ? changed.Keys.Concat(latest.Keys) : latest.Keys.Concat(changed.Keys);
        return new JsonArray(
            order.Distinct(StringComparer.Ordinal).Where(key => values[key] is not null).Select(key => values[key]!.DeepClone()).ToArray());
    }

    private static IOException Conflict(string name)
    {
        return new IOException(
            $"Another window changed {name}. Nothing was overwritten. Reopen a window to load the latest settings before retrying.");
    }

    private static void ValidateSessions(SavedWindowSession[] sessions)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session is null || string.IsNullOrWhiteSpace(session.Id) || !ids.Add(session.Id) || session.Tabs is null)
            {
                throw new ArgumentException("Saved window sessions have missing or duplicate identifiers or tabs.");
            }

            foreach (var tab in session.Tabs)
            {
                if (tab is null || string.IsNullOrWhiteSpace(tab.Address))
                {
                    throw new ArgumentException("A saved tab is missing its address.");
                }

                RegistryWorkspace.ValidateLocation(
                    RegistryLocation.Parse(
                        tab.Address, tab.Is32Bit ? Microsoft.Win32.RegistryView.Registry32 : Microsoft.Win32.RegistryView.Registry64),
                    true);
            }
        }
    }

    private static void ValidateSaved(SavedSettings saved)
    {
        ValidateFavorites(saved.Favorites ?? [], saved.FavoriteFolders ?? []);
        ValidateSessions(saved.WindowSessions ?? []);
        foreach (var search in saved.Searches ?? [])
        {
            RegistrySearch.Validate(search);
        }

        foreach (var note in saved.Notes ?? [])
        {
            if (note is null || note.Text is null)
            {
                throw new ArgumentException("A note has no text.");
            }

            RegistryWorkspace.ValidateLocation(note.Location);
        }

        foreach (var workspace in saved.Workspaces ?? [])
        {
            if (workspace is null)
            {
                throw new ArgumentException("A workspace is missing.");
            }

            workspace.Validate();
        }
    }

    private sealed record SavedWindowSession(string Id, SavedTab[] Tabs, WindowLayout? Layout = null);

    [JsonSourceGenerationOptions(WriteIndented = true, IgnoreReadOnlyProperties = true)]
    [JsonSerializable(typeof(SavedSettings))]
    [JsonSerializable(typeof(string[]))]
    private sealed partial class SettingsJsonContext : JsonSerializerContext;
}
