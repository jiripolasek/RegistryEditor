// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class AppServicesTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditScopes.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            JumpListEntry[]? published = null;
            var backend = new TestJumpListBackend((entries, saveRemoved) =>
            {
                published = entries;
                saveRemoved(new HashSet<string>([entries.Single(entry => entry.FavoriteId is not null).Arguments]));
                return Task.CompletedTask;
            });
            IServiceCollection services = AppServices.Configure(backend, path);
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            var reads = 0;
            services.AddSingleton<Func<AppSettings>>(_ => () =>
            {
                Interlocked.Increment(ref reads);
                return AppSettings.Load(path);
            });
            using var app = services.BuildServiceProvider(true);
            using var firstWindow = app.CreateScope();
            using var secondWindow = app.CreateScope();
            var first = firstWindow.ServiceProvider;
            var second = secondWindow.ServiceProvider;
            var registry = first.GetRequiredService<RegistryService>();
            var createWatch = first.GetRequiredService<Func<WatchSurfaceViewModel>>();
            var firstWatch = createWatch();
            var secondWatch = createWatch();
            firstWatch.Query = "independent reader";
            check(
                !ReferenceEquals(firstWatch, secondWatch) && secondWatch.Query.Length == 0,
                "DI creates independent watch readers in the owning window scope");
            check(
                ReferenceEquals(registry, first.GetRequiredService<RegistryService>())
                && ReferenceEquals(registry.Journal, first.GetRequiredService<RegistryJournal>())
                && ReferenceEquals(registry.Journal, first.GetRequiredService<JournalViewModel>().Journal)
                && !ReferenceEquals(registry.Journal, second.GetRequiredService<RegistryJournal>()),
                "Tabs share their window's registry service and journal");

            check(
                ReferenceEquals(first.GetRequiredService<MainWindowViewModel>(), first.GetRequiredService<MainWindowViewModel>())
                && !ReferenceEquals(first.GetRequiredService<MainWindowViewModel>(), second.GetRequiredService<MainWindowViewModel>()),
                "Tab selection and shell state belong to one window scope");

            check(
                ReferenceEquals(first.GetRequiredService<SettingsViewModel>(), first.GetRequiredService<SettingsViewModel>())
                && !ReferenceEquals(first.GetRequiredService<SettingsViewModel>(), second.GetRequiredService<SettingsViewModel>()),
                "The Settings page and browser View menu share one preferences model per window");

            var createTab = first.GetRequiredService<Func<RegistryLocation, BrowserViewModel>>();
            check(
                ReferenceEquals(
                    first.GetRequiredService<RegistryHistoryBroadcaster>(), second.GetRequiredService<RegistryHistoryBroadcaster>()),
                "Registry history broadcasts are shared across window scopes");
            var initial = RegistryLocation.Parse("HKCU");
            var left = createTab(initial);
            var right = createTab(initial);
            check(
                ReferenceEquals(left.Registry, registry) && ReferenceEquals(right.Registry, registry),
                "Both browser tabs read and edit through the registry service paired with their window journal");
            await left.NavigateAsync(initial.GetComputer());
            check(
                left.CanGoBack && !right.CanGoBack && right.Location == initial,
                "Tabs created by one window have independent location and history");

            var settings = first.GetRequiredService<AppSettings>();
            var otherSettings = second.GetRequiredService<AppSettings>();
            check(
                ReferenceEquals(first.GetRequiredService<FavoritesStore>(), first.GetRequiredService<FavoritesStore>())
                && !ReferenceEquals(first.GetRequiredService<FavoritesStore>(), second.GetRequiredService<FavoritesStore>()),
                "The Favorites store is shared within one window scope");
            check(
                ReferenceEquals(first.GetRequiredService<InvestigationStore>(), first.GetRequiredService<InvestigationStore>())
                && !ReferenceEquals(first.GetRequiredService<InvestigationStore>(), second.GetRequiredService<InvestigationStore>()),
                "Saved search and workspace changes share a store within one window");
            check(
                ReferenceEquals(settings, first.GetRequiredService<AppSettings>()) && !ReferenceEquals(settings, otherSettings),
                "Each window scope owns its settings");
            var notes = first.GetRequiredService<NotesStore>();
            check(
                ReferenceEquals(notes, first.GetRequiredService<NotesStore>())
                && ReferenceEquals(notes, first.GetRequiredService<MainWindowViewModel>().NoteStore)
                && !ReferenceEquals(notes, second.GetRequiredService<NotesStore>()),
                "DI shares one Notes store and autosave coordinator within each window");
            settings.SaveSession([new SavedTab("HKCU")], false, path);
            otherSettings.SaveSession([new SavedTab("HKLM")], false, path);
            check(
                AppSettings.Load(path).Tabs.Select(tab => tab.Address).Order().SequenceEqual(["HKCU", "HKLM"]),
                "Saving both scoped sessions retains both windows' tabs");

            var jumpList = first.GetRequiredService<JumpListService>();
            check(
                ReferenceEquals(jumpList, second.GetRequiredService<JumpListService>())
                && ReferenceEquals(backend, app.GetRequiredService<IJumpListBackend>()),
                "DI shares one Jump List coordinator across windows and uses the supplied backend");
            first.GetRequiredService<FavoritesStore>().Add("Saved", "HKCU\\Software", false, null);
            settings.Favorites.Add(new Favorite("Unsaved", "HKLM"));
            var previousReads = reads;
            await jumpList.UpdateAsync();
            check(
                reads == previousReads + 1 && published?.Count(entry => entry.FavoriteId is not null) == 1,
                "Jump List resolves the overridden settings factory afresh instead of capturing a window's unsaved state");
            check(
                AppSettings.Load(path).HiddenJumpListFavorites.Contains(settings.Favorites[0].Id)
                && settings.HiddenJumpListFavorites.Count == 0,
                "Jump List removals use the configured profile without mutating a scoped settings snapshot");

            var history = first.GetRequiredService<SearchHistoryStore>();
            var stale = AppSettings.Load(path);
            await history.RecordAsync("First query");
            stale.RecordSearch("Other window", path);
            await history.RecordAsync("First query");
            check(
                ReferenceEquals(history, second.GetRequiredService<SearchHistoryStore>()) &&
                history.Queries.SequenceEqual(["First query", "Other window"]) &&
                AppSettings.Load(path).RecentSearches.SequenceEqual(history.Queries),
                "Search history retains another window's query and moves repeat searches to the front");

            const string firstQueued = "First queued query";
            const string secondQueued = " Second queued query ";
            Task firstWrite;
            Task secondWrite;
            using (File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                firstWrite = history.RecordAsync(firstQueued);
                secondWrite = history.RecordAsync(secondQueued);
                check(
                    !firstWrite.IsCompleted && !secondWrite.IsCompleted &&
                    history.Queries.Take(2).SequenceEqual([secondQueued, firstQueued]),
                    "Search history is available while its disk writes wait for the profile lock");
            }

            await Task.WhenAll(firstWrite, secondWrite).WaitAsync(TimeSpan.FromSeconds(10));
            check(
                AppSettings.Load(path).RecentSearches.Take(2).SequenceEqual([secondQueued, firstQueued]),
                "Queued search history persists in order with query whitespace intact");

            await Task.WhenAll(Enumerable.Range(1, 20).Select(index => history.RecordAsync($"Query {index}")));
            check(
                AppSettings.Load(path).RecentSearches.SequenceEqual(
                    Enumerable.Range(1, 20).Reverse().Select(index => $"Query {index}")),
                "Search history persists the newest twenty queries");

            var readable = File.ReadAllText(path);
            const string unreadable = "{ damaged settings";
            File.WriteAllText(path, unreadable);
            using (var reopenedHistory = new SearchHistoryStore(
                () => AppSettings.Load(path), path, NullLogger<SearchHistoryStore>.Instance))
            {
                await reopenedHistory.RecordAsync("Search on an unreadable profile");
                check(
                    File.ReadAllText(path) == unreadable && !directory.EnumerateFiles("settings.json.unreadable.*").Any(),
                    "Opening search on an unreadable profile preserves the original settings file");

                File.WriteAllText(path, readable);
                _ = reopenedHistory.RecordAsync("Search after profile recovery");
            }

            check(
                AppSettings.Load(path).RecentSearches.FirstOrDefault() == "Search after profile recovery",
                "Search history resumes after profile recovery and flushes on shutdown");
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
