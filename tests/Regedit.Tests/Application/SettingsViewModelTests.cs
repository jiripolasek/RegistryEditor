// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;


namespace JPSoftworks.Regedit.Tests.Application;

internal static class SettingsViewModelTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await CheckDefaultDataFontAsync(check);
        await CheckFontFamiliesAsync(check);
        CheckBackdrop(check);
        var directory = Directory.CreateTempSubdirectory("RegeditPreferences.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(path);
            settings.DataFont = new RegistryFont(settings.FontFamily, settings.FontSize);
            var writes = 0;
            var applied = new List<string>();
            var model = new SettingsViewModel(
                settings, change =>
                {
                    writes++;
                    settings.UpdatePreferences(change, path);
                });
            model.PreferenceSaved += applied.Add;
            var refreshed = new List<string?>();
            model.PropertyChanged += (_, args) => refreshed.Add(args.PropertyName);

            model.ThemeIndex = model.ThemeIndex;
            model.DataFontFamily = model.DataFontFamily;
            model.DataFontSize = model.DataFontSize;
            model.ShowKeysInList = model.ShowKeysInList;
            check(model.ToolbarButtons == ToolbarButtons.None, "All optional toolbar buttons start hidden");
            check(writes == 0 && !File.Exists(path) && applied.Count == 0, "Reading and initializing preferences performs no writes");

            model.ShowKeysInList = true;
            model.ShowParentKeyInList = true;
            model.ShowKeysInList = false;
            model.ThemeIndex = 2;
            model.DataFontFamily = "  Consolas  ";
            model.DataFontSize = 17;
            var saved = AppSettings.Load(path);
            check(
                !saved.ShowKeysInList
                && saved.ShowParentKeyInList
                && saved.Theme == "Dark"
                && saved.DataFont == new RegistryFont("Consolas", 17)
                && applied.Count == 6
                && writes == 6, "Both preference surfaces share one save per accepted change and retain the hidden parent-row choice");

            model.DataFontSize = double.NaN;
            check(
                model.DataFontSize == 17
                && model.IsSaveErrorOpen
                && !string.IsNullOrEmpty(model.SaveError)
                && applied.Count == 6
                && refreshed.Contains(nameof(model.DataFontSize)), "Validation restores the accepted font size and exposes the save error");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.ShowStatusBar = false;
                check(
                    model.ShowStatusBar && model.IsSaveErrorOpen && applied.Count == 6 && AppSettings.Load(path).ShowStatusBar,
                    "A failed preference write rolls back in memory and on disk without applying the change");
            }

            model.ShowStatusBar = false;
            check(
                !AppSettings.Load(path).ShowStatusBar && !model.IsSaveErrorOpen && model.SaveError is null && applied.Count == 7,
                "Retry persists the preference once and clears the failure");

            var other = AppSettings.Load(path);
            other.UpdatePreferences(s => s.DataFont = s.DataFont! with { Size = 20 }, path);
            model.DataFontSize = 19;
            check(
                model.IsSaveErrorOpen && model.DataFontSize == 17 && AppSettings.Load(path).DataFont?.Size == 20 && applied.Count == 7,
                "Conflicting preference writes preserve the other window's committed value and this window's previous selection");
            model.ListRowPadding = 8;
            check(
                AppSettings.Load(path).ListRowPadding == 8 && model.ListRowPadding == 8,
                "List spacing persists independently of font size");
            model.ListRowPadding = 99;
            check(model.ListRowPadding == 10, "The list spacing slider clamps to the comfortable end");
            model.ListRowPadding = double.NaN;
            check(model.ListRowPadding == 10, "Nonfinite list spacing cannot enter settings");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.ListRowPadding = 0;
                check(
                    model.ListRowPadding == 10 && AppSettings.Load(path).ListRowPadding == 10 && model.IsSaveErrorOpen,
                    "Failed density saves roll back the slider and preserve its last committed value");
            }

            model.ListRowPadding = 0;
            check(AppSettings.Load(path).ListRowPadding == 0, "Retry can persist fully compact list spacing");

            var buttons = Enum.GetValues<ToolbarButtons>().Where(button => button is not (ToolbarButtons.None or ToolbarButtons.All));
            foreach (var button in buttons)
            {
                model.ToggleToolbarButtonCommand.Execute(button);
                check(
                    model.ToolbarButtons == button
                    && AppSettings.Load(path).ToolbarButtons == button
                    && refreshed.Last() == nameof(model.ToolbarButtons),
                    $"The {button} toolbar shortcut can be enabled independently and survives reload");
                model.ToggleToolbarButtonCommand.Execute(button);
                check(AppSettings.Load(path).ToolbarButtons == ToolbarButtons.None, $"The {button} toolbar shortcut can be removed");
            }

            model.ToggleToolbarButtonCommand.Execute(ToolbarButtons.Favorites);
            model.ToggleToolbarButtonCommand.Execute(ToolbarButtons.BottomPanel);
            var selection = ToolbarButtons.Favorites | ToolbarButtons.BottomPanel;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.ToggleToolbarButtonCommand.Execute(ToolbarButtons.Notes);
                check(
                    model.ToolbarButtons == selection && AppSettings.Load(path).ToolbarButtons == selection && model.IsSaveErrorOpen,
                    "Failed toolbar changes restore the selected shortcuts and preserve the saved configuration");
            }

            model.ToggleToolbarButtonCommand.Execute(ToolbarButtons.Notes);
            check(
                AppSettings.Load(path).ToolbarButtons == (selection | ToolbarButtons.Notes) && !model.IsSaveErrorOpen,
                "Retry saves several toolbar shortcuts together without resetting the other choices");
            model.HideToolbarButtonCommand.Execute(ToolbarButtons.Favorites);
            check(
                model.ToolbarButtons == (ToolbarButtons.BottomPanel | ToolbarButtons.Notes)
                && AppSettings.Load(path).ToolbarButtons == model.ToolbarButtons,
                "Hide from toolbar removes only the requested shortcut and persists the other choices");
            var writesBeforeHide = writes;
            model.HideToolbarButtonCommand.Execute(ToolbarButtons.Favorites);
            check(writes == writesBeforeHide, "Hiding an already hidden shortcut does not re-enable it or write settings");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.HideToolbarButtonCommand.Execute(ToolbarButtons.BottomPanel);
                check(
                    model.ToolbarButtons == (ToolbarButtons.BottomPanel | ToolbarButtons.Notes) && model.IsSaveErrorOpen,
                    "A failed Hide from toolbar save restores the shortcut alongside the other enabled buttons");
            }

            model.HideToolbarButtonCommand.Execute(ToolbarButtons.BottomPanel);
            check(
                AppSettings.Load(path).ToolbarButtons == ToolbarButtons.Notes && !model.IsSaveErrorOpen,
                "Retrying Hide from toolbar persists the removal and clears the save error");
            var navigation = new (string Name, Func<bool> Get, Action<bool> Set, Func<AppSettings, bool> Saved)[]
            {
                ("Back", () => model.ShowBackButton, value => model.ShowBackButton = value, value => value.ShowBackButton),
                ("Forward", () => model.ShowForwardButton, value => model.ShowForwardButton = value, value => value.ShowForwardButton),
                ("Up", () => model.ShowUpButton, value => model.ShowUpButton = value, value => value.ShowUpButton),
                ("Refresh", () => model.ShowRefreshButton, value => model.ShowRefreshButton = value, value => value.ShowRefreshButton)
            };
            check(navigation.All(button => button.Get()), "Navigation buttons remain visible by default");
            foreach (var button in navigation)
            {
                var writesBefore = writes;
                button.Set(true);
                check(writes == writesBefore, $"Initializing the {button.Name} toggle performs no writes");
                button.Set(false);
                var navigationSaved = AppSettings.Load(path);
                check(
                    !button.Get()
                    && !button.Saved(navigationSaved)
                    && navigation.Count(item => !item.Saved(navigationSaved)) == 1
                    && navigationSaved.ToolbarButtons == ToolbarButtons.Notes,
                    $"Hiding {button.Name} persists independently and preserves other toolbar choices");
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    button.Set(true);
                    check(
                        !button.Get() && !button.Saved(AppSettings.Load(path)) && model.IsSaveErrorOpen,
                        $"A failed {button.Name} visibility save restores the last committed choice");
                }

                button.Set(true);
                check(
                    button.Get() && button.Saved(AppSettings.Load(path)) && !model.IsSaveErrorOpen,
                    $"Retry can restore the {button.Name} toolbar button");
            }

            check(!model.AlwaysShowAddressAsText, "Address breadcrumbs remain the default");
            model.AlwaysShowAddressAsText = true;
            check(
                model.AlwaysShowAddressAsText
                && AppSettings.Load(path).AlwaysShowAddressAsText
                && applied.Last() == nameof(model.AlwaysShowAddressAsText), "Text address mode persists and notifies open views");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.AlwaysShowAddressAsText = false;
                check(
                    model.AlwaysShowAddressAsText && AppSettings.Load(path).AlwaysShowAddressAsText && model.IsSaveErrorOpen,
                    "Failed address mode saves preserve the last committed choice");
            }

            model.AlwaysShowAddressAsText = false;
            check(
                !AppSettings.Load(path).AlwaysShowAddressAsText && !model.IsSaveErrorOpen,
                "Breadcrumb mode can be restored and survives reload");
            var legacy = Path.Combine(directory.FullName, "legacy.json");
            File.WriteAllText(legacy, "{}");
            check(!AppSettings.Load(legacy).AlwaysShowAddressAsText, "Legacy settings retain breadcrumbs");
            check(AppSettings.Load(legacy).ToolbarButtons == ToolbarButtons.None, "Existing settings keep every new toolbar button off");
            check(navigation.All(button => button.Saved(AppSettings.Load(legacy))), "Legacy settings keep every navigation button visible");
            File.WriteAllText(legacy, "{\"ToolbarButtons\":1025}");
            check(
                AppSettings.Load(legacy).ToolbarButtons == ToolbarButtons.Favorites,
                "Unsupported toolbar buttons are ignored while valid choices survive");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task CheckDefaultDataFontAsync(Action<bool, string> check)
    {
        var settings = new AppSettings();
        var writes = 0;
        var model = new SettingsViewModel(
            settings, change =>
            {
                change(settings);
                writes++;
            });
        var fontChanges = 0;
        model.PropertyChanged += (_, args) => fontChanges += args.PropertyName == nameof(model.DataFont) ? 1 : 0;
        check(
            !model.UseCustomDataFont && model.DataFont == new RegistryFont(MonospaceFonts.FallbackFamily, 14) && writes == 0,
            "Registry value data starts with the monospace fallback chain without enabling or saving an override");
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = model.LoadDefaultDataFontAsync(() =>
        {
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The default font test did not release its worker.");
            }

            return "JetBrains Mono";
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.ThemeIndex = 2;
            model.UseCustomDataFont = true;
            check(model.DataFontFamily == MonospaceFonts.FallbackFamily, "Enabling an override starts with the current automatic font");
            model.DataFontFamily = "Manual choice";
            check(
                !loading.IsCompleted && model.ThemeIndex == 2 && writes == 3,
                "Slow preferred-font discovery leaves other settings and manual font selection usable");
        }
        finally
        {
            release.Set();
            await loading.WaitAsync(TimeSpan.FromSeconds(10));
        }

        check(
            model.DefaultDataFontFamily == "JetBrains Mono" && fontChanges == 0 && model.DataFontFamily == "Manual choice" && writes == 3,
            "Preferred-font discovery leaves a user-selected font and its editor untouched without saving preferences");
        model.UseCustomDataFont = false;
        check(
            model.DataFontFamily == "JetBrains Mono" && model.DataFont.Size == 14 && settings.DataFont is null,
            "Disabling the override restores the detected automatic monospace font");
        await model.LoadDefaultDataFontAsync(() => throw new InvalidOperationException("A second preference scan started."));
        check(model.DataFontFamily == "JetBrains Mono", "The automatic font is resolved once per window");

        var automaticSettings = new AppSettings();
        var automatic = new SettingsViewModel(automaticSettings, _ => throw new InvalidOperationException("Automatic fonts were saved."));
        var automaticChanges = new List<string?>();
        automatic.PropertyChanged += (_, args) => automaticChanges.Add(args.PropertyName);
        await automatic.LoadDefaultDataFontAsync(() => "Consolas");
        check(
            automatic.DataFontFamily == "Consolas"
            && automaticSettings.DataFont is null
            && automaticChanges.Contains(nameof(automatic.DataFont))
            && automaticChanges.Contains(nameof(automatic.DataFontFamily)),
            "Resolving the automatic font refreshes open lists and the picker without persisting a custom override");

        var savedFont = new RegistryFont("Previously saved font", 19);
        var savedSettings = new AppSettings { DataFont = savedFont };
        var saved = new SettingsViewModel(savedSettings, change => change(savedSettings));
        await saved.LoadDefaultDataFontAsync(() => "Consolas");
        check(saved.DataFont == savedFont && saved.UseCustomDataFont, "Existing explicit Data fonts retain their family and size");

        foreach (var failure in new Exception[]
                 {
                     new IOException("Settings unavailable."), new TypeInitializationException("Win2D", null)
                 })
        {
            var fallbackSettings = new AppSettings();
            var fallback = new SettingsViewModel(fallbackSettings, change => change(fallbackSettings));
            await fallback.LoadDefaultDataFontAsync(() => throw failure);
            fallback.ThemeIndex = 1;
            check(
                fallback.DataFontFamily == MonospaceFonts.FallbackFamily
                && fallback.ThemeIndex == 1
                && fallback.FontFamiliesError is null
                && !fallback.IsSaveErrorOpen,
                $"A default-font {failure.GetType().Name} preserves the monospace fallback and leaves Settings usable");
        }
    }

    private static async Task CheckFontFamiliesAsync(Action<bool, string> check)
    {
        var settings = new AppSettings { DataFont = new RegistryFont("Saved font", 14) };
        var writes = 0;
        var model = new SettingsViewModel(
            settings, change =>
            {
                change(settings);
                writes++;
            });
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerIsThreadPool = false;
        var scans = 0;
        var loading = model.LoadFontFamiliesAsync(() =>
        {
            scans++;
            workerIsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The font loading test did not release its worker.");
            }

            return ["zeta", "Alpha", "beta"];
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                !loading.IsCompleted && model.IsLoadingFontFamilies && model.FontFamilies is null && workerIsThreadPool,
                "A slow font scan runs off the calling thread and exposes its loading state without blocking");
            await model.LoadFontFamiliesAsync(() => throw new InvalidOperationException("A second font scan started."));
            model.DataFontFamily = "Typed while loading";
            model.ThemeIndex = 2;
            check(
                scans == 1 && model.DataFontFamily == "Typed while loading" && model.ThemeIndex == 2 && writes == 2,
                "Settings and manual font entry remain usable while duplicate font loads are ignored");
        }
        finally
        {
            release.Set();
            await loading.WaitAsync(TimeSpan.FromSeconds(10));
        }

        check(
            model.FontFamilies!.SequenceEqual(["Alpha", "beta", "zeta"])
            && !model.IsLoadingFontFamilies
            && model.FontFamiliesError is null
            && model.DataFontFamily == "Typed while loading"
            && writes == 2, "Loaded font families are sorted alphabetically without replacing manual input or saving preferences");
        await model.LoadFontFamiliesAsync(() => throw new InvalidOperationException("Cached fonts were scanned again."));
        check(model.FontFamiliesError is null && scans == 1, "Reopening Settings reuses the loaded font families");

        foreach (var failure in new Exception[]
                 {
                     new IOException("Font enumeration failed."), new COMException("Win2D activation failed."),
                     new TypeInitializationException(
                         "Microsoft.Graphics.Canvas.Text.CanvasTextFormat", new DllNotFoundException("Microsoft.Graphics.Canvas.dll"))
                 })
        {
            var failedSettings = new AppSettings { DataFont = new RegistryFont("Saved font", 14) };
            var failed = new SettingsViewModel(failedSettings, change => change(failedSettings));
            await failed.LoadFontFamiliesAsync(() => throw failure);
            check(
                !failed.IsLoadingFontFamilies
                && failed.FontFamilies is null
                && !string.IsNullOrEmpty(failed.FontFamiliesError)
                && failed.DataFontFamily == "Saved font",
                $"A font scan {failure.GetType().Name} clears loading and preserves the selected font with a visible error");
            failed.ThemeIndex = 2;
            failed.BackdropIndex = 1;
            failed.ShowStatusBar = false;
            failed.DataFontSize = 18;
            check(
                failedSettings is { Theme: "Dark", Backdrop: "Acrylic", ShowStatusBar: false, DataFont.Size: 18 }
                && !failed.IsSaveErrorOpen, $"Theme backdrop status bar and font size remain editable after {failure.GetType().Name}");
            failed.DataFontFamily = "Manual fallback";
            await failed.LoadFontFamiliesAsync(() => ["Arial"]);
            check(
                failed.FontFamilies!.SequenceEqual(["Arial"])
                && failed.FontFamiliesError is null
                && !failed.IsLoadingFontFamilies
                && failed.DataFontFamily == "Manual fallback",
                $"Manual entry and a later font load recover after {failure.GetType().Name}");
        }
    }

    private static void CheckBackdrop(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditBackdrop.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var settings = AppSettings.Load(path);
            var applied = new List<string>();
            var model = new SettingsViewModel(settings, change => settings.UpdatePreferences(change, path));
            model.PreferenceSaved += applied.Add;
            check(settings.Backdrop == "Mica" && model.BackdropIndex == 0, "New windows default to Mica");
            model.BackdropIndex = 0;
            model.BackdropIndex = -1;
            model.BackdropIndex = 2;
            check(!File.Exists(path) && applied.Count == 0, "Unchanged or invalid backdrop selections do not save");

            model.BackdropIndex = 1;
            check(
                settings.Backdrop == "Acrylic"
                && AppSettings.Load(path).Backdrop == "Acrylic"
                && applied.SequenceEqual([nameof(model.BackdropIndex)]), "Acrylic persists and applies once after a successful save");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.BackdropIndex = 0;
                check(
                    model.BackdropIndex == 1
                    && settings.Backdrop == "Acrylic"
                    && model.IsSaveErrorOpen
                    && AppSettings.Load(path).Backdrop == "Acrylic"
                    && applied.Count == 1, "A failed backdrop save restores the selector without applying a different material");
            }

            model.BackdropIndex = 0;
            check(
                AppSettings.Load(path).Backdrop == "Mica" && !model.IsSaveErrorOpen && applied.Count == 2,
                "Retry restores Mica and clears the backdrop save error");
            var before = File.ReadAllText(path);
            try
            {
                settings.UpdatePreferences(s => s.Backdrop = "Glass", path);
                check(false, "Unsupported backdrops cannot be saved");
            }
            catch (ArgumentException)
            {
                check(
                    settings.Backdrop == "Mica" && File.ReadAllText(path) == before,
                    "Invalid backdrops restore the accepted preference and preserve the saved file");
            }

            var other = AppSettings.Load(path);
            model.BackdropIndex = 1;
            other.UpdatePreferences(s => s.Theme = "Dark", path);
            check(
                AppSettings.Load(path) is { Backdrop: "Acrylic", Theme: "Dark" },
                "Saving another window's theme preserves the selected backdrop");
            foreach (var json in new[] { "{}", "{\"Backdrop\":\"Unknown\"}", "{\"Backdrop\":null}" })
            {
                var legacy = Path.Combine(directory.FullName, "legacy.json");
                File.WriteAllText(legacy, json);
                check(
                    AppSettings.Load(legacy) is { Backdrop: "Mica", LoadError: null },
                    "Missing or unsupported backdrop preferences fall back to Mica");
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
