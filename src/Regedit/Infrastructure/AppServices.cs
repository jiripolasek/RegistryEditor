// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.Principal;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.Infrastructure;

internal static class AppServices
{
    internal static ServiceCollection Configure(IJumpListBackend jumpListBackend, string? settingsPath = null)
    {
        settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(AppSettings.Folder, "settings.json"));
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => AppLogging.Open());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<Func<AppSettings>>(_ => () => AppSettings.Load(settingsPath));
        services.AddScoped(provider => provider.GetRequiredService<Func<AppSettings>>()());
        services.AddSingleton(provider => new SearchHistoryStore(
            provider.GetRequiredService<Func<AppSettings>>(), settingsPath, provider.GetRequiredService<ILogger<SearchHistoryStore>>()));
        services.AddSingleton<IJumpListBackend>(jumpListBackend);
        services.AddSingleton(provider => new JumpListService(
            provider.GetRequiredService<Func<AppSettings>>(), provider.GetRequiredService<IJumpListBackend>(), settingsPath,
            provider.GetRequiredService<ILogger<JumpListService>>()));
        services.AddScoped(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new FavoritesStore(settings, () => settings.Save(settingsPath));
        });
        services.AddScoped(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new InvestigationStore(settings, () => settings.Save(settingsPath));
        });
        services.AddScoped(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new NotesStore(settings, () => settings.Save(settingsPath));
        });
        services.AddScoped(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new SettingsViewModel(settings, change => settings.UpdatePreferences(change, settingsPath));
        });
        services.AddScoped(_ => CreateJournal());
        services.AddScoped(provider => new JournalViewModel(
            provider.GetRequiredService<RegistryJournal>(), provider.GetRequiredService<ILogger<JournalViewModel>>()));
        services.AddScoped(provider => new RegistryService { Journal = provider.GetRequiredService<RegistryJournal>() });
        services.AddSingleton<RegistryEntryClipboard>();
        services.AddSingleton<RegistryHistoryBroadcaster>();
        services.AddScoped<Func<RegistryLocation, BrowserViewModel>>(provider =>
        {
            var registry = provider.GetRequiredService<RegistryService>();
            var clipboard = provider.GetRequiredService<RegistryEntryClipboard>();
            var favorites = provider.GetRequiredService<FavoritesStore>();
            var settings = provider.GetRequiredService<AppSettings>();
            var preferences = provider.GetRequiredService<SettingsViewModel>();
            var history = provider.GetRequiredService<RegistryHistoryBroadcaster>();
            return location => new BrowserViewModel(
                registry, location, clipboard, favorites, settings, preferences, historyBroadcaster: history);
        });
        services.AddScoped(provider => new MainWindowViewModel(
            provider.GetRequiredService<AppSettings>(), provider.GetRequiredService<InvestigationStore>(),
            provider.GetRequiredService<RegistryService>(), provider.GetRequiredService<Func<RegistryLocation, BrowserViewModel>>(),
            provider.GetRequiredService<ILogger<MainWindowViewModel>>(), provider.GetRequiredService<NotesStore>(),
            provider.GetRequiredService<RegistryHistoryBroadcaster>(), provider.GetRequiredService<JournalViewModel>(),
            provider.GetRequiredService<SearchHistoryStore>()));
        services.AddTransient(provider => new WatchSurfaceViewModel(provider.GetRequiredService<MainWindowViewModel>()));
        services.AddScoped<Func<WatchSurfaceViewModel>>(provider => provider.GetRequiredService<WatchSurfaceViewModel>);
        return services;
    }

    private static RegistryJournal CreateJournal()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var folder = Path.Combine(AppSettings.Folder, "Journal");
#if !REGEDIT_UI_TESTS
        if (administrator)
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RegeditModern.AdministratorJournal",
                identity.User!.Value);
        }
#endif
        return new RegistryJournal(folder, administrator);
    }
}
