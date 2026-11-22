// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class KeyModifiedSearchTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool liveRegistry)
    {
        const string typed = "proxy modified:2026-11-03..2026-11-22 activity:inspected within:7d";
        var parsed = SearchQueryText.Parse(typed);
        check(parsed.Text == "proxy" && parsed.Activity == SearchActivity.Inspected
              && parsed.KeyModified == new KeyModifiedFilter(From: new DateOnly(2026, 11, 3), Through: new DateOnly(2026, 11, 22))
              && SearchQueryText.Parse(SearchQueryText.Format(parsed)) == parsed,
            "Key modification dates compose with text and activity and round-trip through the typed query");
        check(SearchQueryText.Parse("modified:24H").KeyModified == new KeyModifiedFilter(Within: TimeSpan.FromHours(24))
              && SearchQueryText.Parse("modified:2026-11-03..").KeyModified is { Through: null }
              && SearchQueryText.Parse("modified:..2026-11-22").KeyModified is { From: null }
              && SearchQueryText.Parse("\"modified:7d\"") is { Text: "modified:7d", KeyModified: null },
            "Typed modification filters accept presets, open-ended ranges, and quoted literal queries");
        const string quoted = "\"C:\\Program Files\\Tool.exe\"";
        var literal = new SearchOptions(quoted);
        check(SearchQueryText.Parse(quoted) == literal && SearchQueryText.Format(literal) == quoted &&
              SearchQueryText.Parse(SearchQueryText.Format(new SearchOptions("\"modified:7d\""))).Text == "\"modified:7d\"",
            "Ordinary surrounding quotes stay literal and quoting still escapes filter syntax");

        var zone = TimeZoneInfo.CreateCustomTimeZone("Test offset", TimeSpan.FromHours(2), "Test offset", "Test offset");
        var day = new DateOnly(2026, 11, 22);
        var custom = new KeyModifiedFilter(From: day, Through: day);
        var start = new DateTimeOffset(2026, 11, 21, 22, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);
        check(custom.Matches(start, end, zone) && custom.Matches(end.AddTicks(-1), end, zone)
              && !custom.Matches(start.AddTicks(-1), end, zone) && !custom.Matches(end, end, zone),
            "Custom ranges include the whole local calendar day and exclude neighboring days");
        var recent = new KeyModifiedFilter(Within: TimeSpan.FromHours(1));
        check(recent.Matches(end.AddHours(-1), end, zone) && !recent.Matches(end.AddHours(-1).AddTicks(-1), end, zone)
              && !recent.Matches(end.AddTicks(1), end, zone), "A preset uses a rolling window with a fixed search start instant");

        var location = RegistryLocation.Parse("HKCU\\Software");
        try
        {
            new RegistryService().FindNext(location, new SearchOptions("proxy", Notes: true), null, default);
            check(false, "The core Find Next API rejects key note filters");
        }
        catch (ArgumentException ex)
        {
            check(ex.ParamName == "options", "The core Find Next API rejects key note filters");
        }

        var browser = new BrowserSearchViewModel(() => location);
        var results = new List<SavedSearch>();
        var errors = 0;
        browser.ResultsRequested += results.Add;
        browser.Notice += (_, _, _) => errors++;
        foreach (var invalid in new[] { "modified:..", "modified:5d", "modified:2026-11-31..", "modified:2026-11-22..2026-11-21", "modified:1h modified:7d" })
        {
            browser.Query = invalid;
            browser.QuickSearchCommand.Execute(null);
        }

        check(errors == 5 && results.Count == 0, "Invalid, reversed, and duplicate modification filters show an error before opening results");
        browser.Query = "modified:2026-11-22..2026-11-22";
        browser.QuickSearchCommand.Execute(null);
        browser.FindRequested += form =>
        {
            check(form.HasCustomModifiedRange && DateOnly.FromDateTime(form.ModifiedFrom!.Value.LocalDateTime) == day,
                "Advanced search initializes custom dates from the typed query");
            return Task.FromResult<SavedSearch?>(form.CreateSearch());
        };
        await browser.FindCommand.ExecuteAsync(null);
        check(results.Count == 2 && results.All(search => search.Options is { Text: "", Keys: true } && search.Options.KeyModified == custom),
            "The dialog and typed search preserve the same date-only filter");

        var draft = new BrowserSearchViewModel(() => location) { Query = "Counter activity:inspected modified:" };
        draft.ResultsRequested += results.Add;
        draft.FindRequested += form =>
        {
            check(form.InitialError is not null && form.Query == "Counter" && form.HasActivity,
                "An unfinished qualifier opens Advanced search with an inline error, text, and valid filters");
            form.KeyModifiedIndex = 2;
            return Task.FromResult<SavedSearch?>(form.CreateSearch());
        };
        await draft.FindCommand.ExecuteAsync(null);
        check(results.Count == 3 && results[^1].Options is { Text: "Counter", Activity: SearchActivity.Inspected }
              && results[^1].Options.KeyModified?.Within == TimeSpan.FromHours(24),
            "Selecting a date preset repairs the draft query");

        var directory = Directory.CreateTempSubdirectory("regedit-key-modified-");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(file);
            settings.Searches.Add(results[0]);
            settings.Save(file);
            check(AppSettings.Load(file).Searches.Single().Options.KeyModified == custom, "Saved searches retain fixed custom dates");
            if (liveRegistry)
            {
                CheckRegistry(location, check);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static void CheckRegistry(RegistryLocation parent, Action<bool, string> check)
    {
        var registry = new RegistryService();
        var root = parent.Child("!Regedit.KeyModified." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(parent, root.Name);
        try
        {
            var child = root.Child("ModifiedChild");
            registry.CreateKey(root, child.Name);
            registry.Write(child, new RegistryValue("ModifiedValue", RegistryValueType.Binary, [1, 2]), null);
            registry.Write(root, new RegistryValue("OtherValue", RegistryValueType.DWord, [0, 0, 0, 0]), null);
            RegistrySearchResults Find(SearchOptions options, RegistryValueInspection[]? inspections = null, KeyNote[]? notes = null) =>
                RegistrySearch.FindAll(registry, new SavedSearch("Modified", [root], options), default, inspections, notes: notes);

            var dated = SearchQueryText.Parse("modified:24h");
            var keys = Find(dated).Matches;
            check(keys.Length == 2 && keys.All(match => match.Value is null && match.KeyLastModifiedUtc is not null),
                "Date-only searches return keys and attach their last-write timestamps");
            var value = Find(SearchQueryText.Parse("ModifiedValue modified:7d")).Matches.Single();
            check(value.ValueName == "ModifiedValue" && value.KeyLastModifiedUtc == registry.Read(child, includeLastWriteTime: true).LastWriteTimeUtc,
                "A text match on a value is dated by its containing key");
            check(Find(dated with { Types = [RegistryValueType.Binary] }).Matches is [{ ValueName: "ModifiedValue" }]
                  && Find(SearchQueryText.Parse("modified:1900-01-01..1900-01-02")).Matches.Length == 0,
                "A type filter returns values and an old date range excludes newer keys and values");
            var inspections = new[] { new RegistryValueInspection(child, "ModifiedValue", DateTimeOffset.UtcNow) };
            check(Find(SearchQueryText.Parse("activity:inspected modified:24h"), inspections).Matches is [{ ValueName: "ModifiedValue" }]
                  && Find(SearchQueryText.Parse("activity:inspected modified:1900-01-01..1900-01-02"), inspections).Matches.Length == 0,
                "An activity search intersects its candidates with the key modification filter");

            KeyNote[] notes =
            [
                new(child with { SubKey = child.SubKey.ToUpperInvariant() }, "ModifiedChild needs a reboot"),
                new(root.Child("Removed"), "ModifiedChild needs a reboot"),
                new(parent, "ModifiedChild needs a reboot")
            ];
            var noteQuery = SearchQueryText.Parse("needs a reboot match:notes");
            var noteHits = Find(noteQuery, notes: notes).Matches;
            check(noteHits is [{ Fields: "Key note", MatchedText: "ModifiedChild needs a reboot" }]
                  && noteHits[0].Location.IsSameLocation(child)
                  && Find(new SearchOptions("needs a reboot"), notes: notes).Matches.Length == 0,
                "Key notes are opt-in and notes-only results open existing keys within the search scope");
            check(Find(SearchQueryText.Parse("ModifiedChild match:keys,notes"), notes: notes).Matches is [{ Fields: "Key name and note" }]
                  && Find(noteQuery with { KeyModified = dated.KeyModified }, notes: notes).Matches is [{ KeyLastModifiedUtc: not null }]
                  && Find(noteQuery with { KeyModified = new KeyModifiedFilter(Through: new DateOnly(1900, 1, 1)) }, notes: notes).Matches.Length == 0,
                "A key matching both fields appears once and modification dates filter notes by their key timestamp");

            var data = new byte[256 * 1024];
            for (var index = 0; index < 8; index++)
            {
                registry.Write(root, new RegistryValue($"Large{index}", RegistryValueType.Binary, data), null);
            }

            var namesOnly = new SavedSearch("Names only", [root], dated with { Text = "Large", Keys = false, Data = false });
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var limited = RegistrySearch.FindAll(registry, namesOnly, default, limit: 1);
            check(limited.Truncated && limited.Matches is [{ Value.Data.Length: 256 * 1024 }]
                  && GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 1024 * 1024,
                "Name-only search retains its hit data without loading the other large values beyond the limit");
            using var cancellation = new CancellationTokenSource();
            try
            {
                RegistrySearch.FindAll(registry, namesOnly, cancellation.Token, progress: _ => cancellation.Cancel());
                check(false, "Canceling after the first value stops the same key's search");
            }
            catch (OperationCanceledException)
            {
                check(true, "Canceling after the first value stops the same key's search");
            }
        }
        finally
        {
            registry.DeleteKey(root);
        }
    }
}
