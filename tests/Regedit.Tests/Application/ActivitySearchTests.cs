// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.Principal;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ActivitySearchTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool liveRegistry)
    {
        check(SearchQueryText.Parse("  C:\\Windows\\Proxy  ").Text == "  C:\\Windows\\Proxy  ", "Plain queries preserve spaces and path characters");
        check(SearchQueryText.Parse("\"activity:inspected\"") is { Text: "activity:inspected", Activity: SearchActivity.None },
            "Quoted activity syntax is literal text");
        check(SearchQueryText.Parse("within:7D Proxy  Settings ACTIVITY:ANY") is
            { Text: "Proxy  Settings", Activity: SearchActivity.Either, ActivityWithin.TotalDays: 7, Keys: false },
            "Mixed queries preserve internal spaces and accept case-insensitive operators");
        var options = new SearchOptions("  \"Proxy\" within:old  ", Keys: false, Activity: SearchActivity.Inspected, ActivityWithin: TimeSpan.FromHours(13));
        check(SearchQueryText.Parse(SearchQueryText.Format(options)) == options, "Formatting and parsing round-trip literal operator text and duration");

        var location = RegistryLocation.Parse("HKCU\\Software");
        var browser = new BrowserSearchViewModel(() => location);
        var searches = new List<SavedSearch>();
        var errors = 0;
        browser.ResultsRequested += searches.Add;
        browser.Notice += (_, _, _) => errors++;
        foreach (var invalid in new[] { "activity:unknown", "activity:edited activity:inspected", "within:7d", "activity:edited within:0h", "activity:edited within:2147483647d", "activity:either within:7d within:8d" })
        {
            browser.Query = invalid;
            browser.QuickSearchCommand.Execute(null);
        }

        check(errors == 6 && searches.Count == 0, "Invalid typed activity filters report errors before opening a result tab");
        browser.Query = "activity:inspected within:7d";
        browser.QuickSearchCommand.Execute(null);
        browser.FindRequested += form =>
        {
            check(form is { Query: "", ActivityIndex: (int)SearchActivity.Inspected, Within: "7d", CanMatchKeyFields: false },
                "Opening Advanced search from a typed query initializes activity controls");
            form.ActivityIndex = (int)SearchActivity.Either;
            form.Within = "12h";
            return Task.FromResult<SavedSearch?>(form.CreateSearch());
        };
        await browser.FindCommand.ExecuteAsync(null);
        check(searches.Count == 2 && searches[0].Options.Activity == SearchActivity.Inspected
              && searches[1].Options is { Text: "", Activity: SearchActivity.Either, ActivityWithin.TotalHours: 12 }
              && browser.Query == "activity:either within:12h", "Advanced controls and typed search produce the same structured filter and query");
        var allHives = new FindOptionsViewModel(location, "", searches[1].Options) { ScopeIndex = 1 };
        var computer = new BrowserSearchViewModel(location.GetComputer) { Query = "activity:edited" };
        computer.ResultsRequested += searches.Add;
        computer.QuickSearchCommand.Execute(null);
        check(allHives.CreateSearch().Roots.SequenceEqual(location.GetHives()) && searches[2].Roots.SequenceEqual(location.GetHives()),
            "All-hives activity searches include the named hives as recorded in history");

        var directory = Directory.CreateTempSubdirectory("regedit-activity-search-");
        try
        {
            var settingsPath = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(settingsPath);
            settings.Searches.Add(searches[1]);
            settings.Save(settingsPath);
            check(AppSettings.Load(settingsPath).Searches.Single().Options == searches[1].Options,
                "Saved searches preserve a filter-only query and rolling time window");
            if (liveRegistry)
            {
                CheckRegistry(location, directory.FullName, check);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static void CheckRegistry(RegistryLocation parent, string directory, Action<bool, string> check)
    {
        var root = parent.Child("!Regedit.ActivitySearch." + Guid.NewGuid().ToString("N"));
        var raw = new RegistryService();
        var journalPath = Path.Combine(directory, "Journal");
        var registry = new RegistryService { Journal = new RegistryJournal(journalPath) };
        raw.CreateKey(parent, root.Name);
        try
        {
            var child = root.Child("Child");
            raw.CreateKey(root, child.Name);
            var edited = new RegistryValue("EditedOnly", RegistryValueType.Binary, [1]);
            var both = new RegistryValue("Both", RegistryValueType.Binary, [2]);
            var removed = new RegistryValue("Removed", RegistryValueType.Binary, [3]);
            registry.Write(root, edited, null);
            registry.Write(child, both, null);
            registry.Write(root, removed, null);
            registry.DeleteValue(root, removed);
            raw.Write(root, new RegistryValue("", RegistryValueType.Binary, [4]), null);
            raw.Write(root, new RegistryValue("OldInspection", RegistryValueType.Binary, [5]), null);
            raw.Write(root, new RegistryValue("OtherView", RegistryValueType.Binary, [6]), null);
            raw.Write(root, new RegistryValue("Passive", RegistryValueType.Binary, [7]), null);
            File.WriteAllText(Path.Combine(journalPath, "damaged.json"), "{ invalid");

            var now = DateTimeOffset.UtcNow;
            using var identity = WindowsIdentity.GetCurrent();
            var alias = new RegistryLocation(RegistryHive.Users, $"{identity.User!.Value}\\{child.SubKey}", View: child.View);
            RegistryValueInspection[] inspections =
            [
                new(root, "", now),
                new(alias with { SubKey = alias.SubKey.ToUpperInvariant() }, "bOtH", now),
                new(root with { View = RegistryView.Registry32 }, "OtherView", now),
                new(root, "OldInspection", now.AddDays(-10))
            ];
            RegistrySearchResults Search(string query, RegistryLocation[]? roots = null, int limit = 100000,
                Action<RegistrySearchMatch[]>? progress = null)
                => RegistrySearch.FindAll(registry, new SavedSearch(query, roots ?? [root], SearchQueryText.Parse(query)), default,
                    inspections, limit, progress);

            var editedOnly = Search("activity:edited");
            check(editedOnly.Matches.Select(match => match.ValueName).Order().SequenceEqual(["Both", "EditedOnly"])
                  && editedOnly.SkippedJournalEntries == 1, "Edited uses actual value changes, skips deleted values, and reports damaged journal entries");
            var inspected = Search("activity:inspected within:7d");
            check(inspected.Matches.Select(match => match.ValueName).Order().SequenceEqual(["", "Both"])
                  && inspected.SkippedJournalEntries == 0, "Inspected honors time and registry view, includes the default value, and can run independently of the journal");
            var either = Search("activity:either within:7d");
            check(either.Matches.Length == 3 && either.Matches.Single(match => match.ValueName == "Both").Fields == "Edited and inspected",
                "Either deduplicates value names and merges activity under alternate hive paths");
            var aliased = Search("activity:either", [alias]).Matches.Single();
            check(aliased.Fields == "Edited and inspected" && aliased.Location.IsSameLocation(alias)
                  && Search("activity:either", [child, alias]).Matches is [{ ValueName: "Both", Fields: "Edited and inspected" }],
                "An alias scope finds both sources, uses the scoped path, and yields one match across both scopes");
            check(Search("Both activity:either", [child]).Matches is [{ ValueName: "Both" }]
                  && Search("Passive activity:either").Matches.Length == 0, "Activity combines with free text and the selected branch");
            var progressive = new List<RegistrySearchMatch>();
            var limited = Search("activity:either", limit: 1, progress: progressive.AddRange);
            check(limited.Truncated && limited.Matches.SequenceEqual(progressive), "Activity search streams normal results and respects the result cap");
        }
        finally
        {
            raw.DeleteKey(root);
        }
    }
}
