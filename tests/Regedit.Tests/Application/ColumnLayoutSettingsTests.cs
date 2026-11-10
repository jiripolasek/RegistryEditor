// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Lists;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ColumnLayoutSettingsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditColumnLayouts.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, """{"FontFamily":"Segoe UI","FontSize":14}""");
            var first = AppSettings.Load(path);
            var second = AppSettings.Load(path);
            var firstPreferences = new SettingsViewModel(first, change => first.UpdatePreferences(change, path));
            var secondPreferences = new SettingsViewModel(second, change => second.UpdatePreferences(change, path));
            check(firstPreferences.ColumnLayout("Tool.Journal") is null, "Lists without a saved layout use defaults");

            var journal = new ColumnLayout(
            [
                new ColumnState("Subject", 180, true), new ColumnState("Path", 0, true),
                new ColumnState("Summary", 0, false)
            ]);
            var search = new ColumnLayout([new ColumnState("Path", 300, true), new ColumnState("Subject", 0, true)]);
            firstPreferences.SaveColumnLayout("Tool.Journal", journal);
            secondPreferences.SaveColumnLayout("SearchResults", search);
            var reloaded = AppSettings.Load(path);
            check(
                reloaded.ColumnLayouts.TryGetValue("Tool.Journal", out var savedJournal) &&
                savedJournal.IsEquivalentTo(journal) &&
                reloaded.ColumnLayouts.TryGetValue("SearchResults", out var savedSearch) &&
                savedSearch.IsEquivalentTo(search), "Windows saving different lists keep both layouts");

            var narrower = journal with
            {
                Columns = [journal.Columns[0] with { Width = 120 }, .. journal.Columns.Skip(1)]
            };
            secondPreferences.SaveColumnLayout("Tool.Journal", narrower);
            firstPreferences.SaveColumnLayout(
                "Tool.Journal", journal with { Columns = journal.Columns.Reverse().ToArray() });
            check(
                !firstPreferences.IsSaveErrorOpen && !secondPreferences.IsSaveErrorOpen &&
                AppSettings.Load(path).ColumnLayouts["Tool.Journal"].Columns[0].Id == "Summary" &&
                AppSettings.Load(path).ColumnLayouts.ContainsKey("SearchResults"),
                "Concurrent changes to one list resolve to the latest save without a conflict");

            File.WriteAllText(
                path,
                """{"ColumnLayouts":{"Tool.Notes":{"Columns":[{"Id":"Path","Width":200,"Visible":true}]},"":null}}""");
            var legacy = AppSettings.Load(path);
            check(
                legacy.LoadError is null && legacy.ColumnLayouts.Count == 1 &&
                legacy.ColumnLayouts["Tool.Notes"].Columns.Single().Width == 200,
                "Saved layouts load and incomplete entries are skipped");
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
