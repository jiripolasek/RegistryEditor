// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class WebSearchTests
{
    internal static void Run(Action<bool, string> check)
    {
        var key = new RegistryLocation(RegistryHive.CurrentUser, @"Software\Example & #+%\Příliš ");
        foreach (var template in new[] { RegistryWebSearch.Bing, RegistryWebSearch.Google, RegistryWebSearch.DuckDuckGo })
        {
            var uri = RegistryWebSearch.CreateUri(template, key);
            check(
                uri.AbsoluteUri == template.Replace("{query}", Uri.EscapeDataString(key.Address)) && uri.Fragment.Length == 0,
                "Search presets encode the complete selected key path, including special characters and trailing spaces");
            var value = RegistryWebSearch.CreateUri(template, key, "Value / & #+%é ");
            check(
                Uri.UnescapeDataString(value.Query[3..]) == key.Address + "\\Value / & #+%é ",
                "Value searches append the selected value name and keep its characters in the search parameter");
            check(
                Uri.UnescapeDataString(RegistryWebSearch.CreateUri(template, key, string.Empty).Query[3..]) == key.Address + @"\(Default)",
                "Unnamed default values have an explicit name in the search query");
        }

        var remote = key with { Machine = "SERVER", View = RegistryView.Registry32 };
        check(
            Uri.UnescapeDataString(RegistryWebSearch.CreateUri(RegistryWebSearch.Bing, remote).Query[3..]) == key.Address,
            "Remote searches omit the computer name and registry view while preserving the key path");
        check(
            RegistryWebSearch.CreateUri("https://search.example/{query}?source=regedit", key).AbsoluteUri
            == "https://search.example/" + Uri.EscapeDataString(key.Address) + "?source=regedit",
            "Custom engines can place the encoded path in the URL path and retain other parameters");

        CheckPrivacy(check);
        CheckCommand(check);

        var directory = Directory.CreateTempSubdirectory("RegeditWebSearch.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(path);
            var writes = 0;
            var saved = new List<string>();
            var model = new SettingsViewModel(
                settings, change =>
                {
                    writes++;
                    settings.UpdatePreferences(change, path);
                });
            model.PreferenceSaved += saved.Add;
            model.WebSearchUrl = model.WebSearchUrl;
            check(
                model.WebSearchUrl == RegistryWebSearch.Bing && writes == 0 && !File.Exists(path),
                "New settings use Bing and initializing the search URL performs no writes");
            foreach (var template in new[] { RegistryWebSearch.Google, RegistryWebSearch.DuckDuckGo, RegistryWebSearch.Bing })
            {
                model.SetWebSearchUrlCommand.Execute(template);
                check(
                    model.WebSearchUrl == template
                    && AppSettings.Load(path).WebSearchUrl == template
                    && saved.Last() == nameof(model.WebSearchUrl), "Each preset applies immediately and survives reloading settings");
            }

            const string custom = "https://search.example/?source=regedit&q={query}";
            model.WebSearchUrl = "  " + custom + "  ";
            check(
                model.WebSearchUrl == custom && AppSettings.Load(path).WebSearchUrl == custom, "Custom search URLs are trimmed and saved");
            var original = File.ReadAllText(path);
            foreach (var invalid in new[]
                     {
                         "", " ", "https://example.com/search", "/search?q={query}", "file:///C:/test?q={query}",
                         "javascript:alert('{query}')", "ms-settings:search?query={query}", "https://{query}.example.com/search",
                         "https://user:password@example.com/?q={query}", "https://example.com/#q={query}"
                     })
            {
                model.WebSearchUrl = invalid;
                check(
                    model.WebSearchUrl == custom && model.IsSaveErrorOpen && File.ReadAllText(path) == original,
                    "Invalid or non-web search templates preserve the last working preference and show an error");
                var rejected = false;
                try
                {
                    RegistryWebSearch.CreateUri(invalid, key);
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }

                check(rejected, "Web searches validate their URL before launching a browser");
            }

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.SetWebSearchUrlCommand.Execute(RegistryWebSearch.Google);
                check(
                    model.WebSearchUrl == custom && model.IsSaveErrorOpen && File.ReadAllText(path) == original,
                    "Failed search engine saves restore the accepted URL and preserve settings on disk");
            }

            var other = AppSettings.Load(path);
            model.SetWebSearchUrlCommand.Execute(RegistryWebSearch.Google);
            other.UpdatePreferences(s => s.Theme = "Dark", path);
            check(
                AppSettings.Load(path) is { WebSearchUrl: RegistryWebSearch.Google, Theme: "Dark" } && !model.IsSaveErrorOpen,
                "Retrying clears the error and another window's independent preference save retains the search engine");
            foreach (var json in new[] { "{}", "{\"WebSearchUrl\":null}", "{\"WebSearchUrl\":\"file:///C:/test?q={query}\"}" })
            {
                File.WriteAllText(path, json);
                check(
                    AppSettings.Load(path) is { WebSearchUrl: RegistryWebSearch.Bing, LoadError: null },
                    "Missing or unsafe stored search templates fall back to Bing without making the profile unreadable");
            }
        }
        finally
        {
            directory.Delete(true);
        }

        var computerRejected = false;
        try
        {
            RegistryWebSearch.CreateUri(RegistryWebSearch.Bing, new RegistryLocation());
        }
        catch (ArgumentException)
        {
            computerRejected = true;
        }

        check(computerRejected, "Computer is not searched as though it were a registry key");
    }

    private static void CheckPrivacy(Action<bool, string> check)
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        foreach (var machine in new[] { null, "PRIVATE-SERVER" })
        {
            foreach (var (subKey, expected) in new[]
                     {
                         (sid, @"HKEY_USERS\<SID>"), (sid + @"\Software\Example & #+%é ", @"HKEY_USERS\<SID>\Software\Example & #+%é "),
                         (sid.ToLowerInvariant() + @"\Software", @"HKEY_USERS\<SID>\Software"),
                         (sid + @"_Classes\CLSID", @"HKEY_USERS\<SID>\CLSID")
                     })
            {
                var location = new RegistryLocation(RegistryHive.Users, subKey, machine);
                foreach (var valueName in new[] { null, string.Empty, "Value & #+%é " })
                {
                    var query = Uri.UnescapeDataString(RegistryWebSearch.CreateUri(RegistryWebSearch.Bing, location, valueName).Query[3..]);
                    var suffix = valueName is null ? string.Empty : "\\" + (valueName.Length == 0 ? "(Default)" : valueName);
                    check(
                        query == expected + suffix
                        && !query.Contains("PRIVATE-SERVER", StringComparison.Ordinal)
                        && !query.Contains(sid, StringComparison.OrdinalIgnoreCase),
                        "Local and remote user-hive searches redact the user SID before appending the selected value name");
                }
            }

            foreach (var user in new[] { ".DEFAULT", "S-1-5-18", "S-1-5-19", "S-1-5-20" })
            {
                var location = new RegistryLocation(RegistryHive.Users, user + @"\Software", machine);
                check(
                    Uri.UnescapeDataString(RegistryWebSearch.CreateUri(RegistryWebSearch.Bing, location).Query[3..])
                    == @"HKEY_USERS\" + user + @"\Software",
                    "Well-known user hives stay useful search terms while any remote computer name is omitted");
            }
        }
    }

    private static void CheckCommand(Action<bool, string> check)
    {
        var settings = new AppSettings { WebSearchUrl = RegistryWebSearch.Google };
        var preferences = new SettingsViewModel(settings, change => change(settings));
        var launched = new List<Uri>();
        Exception? launchFailure = null;
        var browser = new BrowserViewModel(
            new RegistryService(), RegistryLocation.Parse(@"HKCU\Software\Other"), new RegistryEntryClipboard(), preferences: preferences,
            launchWebSearch: uri =>
            {
                if (launchFailure is not null)
                {
                    throw launchFailure;
                }

                launched.Add(uri);
            });
        try
        {
            var target = RegistryLocation.Parse(@"\\PRIVATE-SERVER\HKU\S-1-5-21-111-222-333-1001\Software\Example");
            const string path = @"HKEY_USERS\<SID>\Software\Example";
            browser.SearchWebCommand.Execute((target, null));
            check(
                launched.Count == 1 && launched[^1].Host == "www.google.com" && Uri.UnescapeDataString(launched[^1].Query[3..]) == path,
                "The VM command uses the Settings model's template, sanitizes the passed location and calls the injected launcher");

            browser.SearchWebCommand.Execute((target, "Value & #+%é "));
            check(
                launched.Count == 2 && Uri.UnescapeDataString(launched[^1].Query[3..]) == path + "\\Value & #+%é ",
                "The VM command searches the passed value name instead of its current location or selection");
            browser.SearchWebCommand.Execute((target, string.Empty));
            check(
                launched.Count == 3 && Uri.UnescapeDataString(launched[^1].Query[3..]) == path + @"\(Default)",
                "The VM command distinguishes unnamed default values from key-only searches");

            preferences.SetWebSearchUrlCommand.Execute(RegistryWebSearch.DuckDuckGo);
            browser.SearchWebCommand.Execute((target, null));
            check(
                launched.Count == 4 && launched[^1].Host == "duckduckgo.com",
                "Existing browser commands read the latest search engine preference on every invocation");
            preferences.WebSearchUrl = "https://search.example/{query}?source=regedit";
            browser.SearchWebCommand.Execute((target, null));
            check(
                launched.Count == 5
                && Uri.UnescapeDataString(launched[^1].AbsoluteUri) == "https://search.example/" + path + "?source=regedit",
                "The VM command applies the same privacy filtering to custom search URL paths");

            browser.SearchWebCommand.Execute((null, "Value"));
            browser.SearchWebCommand.Execute((new RegistryLocation(Machine: "PRIVATE-SERVER"), null));
            check(launched.Count == 5 && browser.Error is null, "Missing locations and Computer never reach the injected launcher");

            Exception? reported = null;
            browser.WebSearchFailed += exception => reported = exception;
            launchFailure = new InvalidOperationException("The browser could not be opened.");
            browser.SearchWebCommand.Execute((target, null));
            check(
                ReferenceEquals(reported, launchFailure) && browser.Error == launchFailure.Message && launched.Count == 5,
                "Launcher failures reach the existing VM error surface and failure event without escaping the command");

            launchFailure = null;
            preferences.WebSearchUrl = "file:///C:/test?q={query}";
            browser.SearchWebCommand.Execute((target, null));
            check(
                reported is ArgumentException && browser.Error == reported.Message && launched.Count == 5,
                "Unsafe templates are rejected inside the VM command before calling the launcher");
        }
        finally
        {
            browser.CancelPending();
        }
    }
}
