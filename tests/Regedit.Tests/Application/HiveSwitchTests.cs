// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class HiveSwitchTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var path in new[] { string.Empty, @"Software\Vendor\App " })
                {
                    var source = new RegistryLocation(hive, path, View: view);
                    var target = source.GetAlternateHiveLocation()!;
                    check(
                        target.Hive != hive && target.SubKey == path && target.View == view && target.Machine is null,
                        "Switching hives preserves the exact subkey path and registry view: " + source.Address + " / " + view);
                    check(target.GetAlternateHiveLocation() == source, "Switching hives twice returns to the original location");
                }
            }
        }

        var computer = new RegistryLocation();
        var browser = new BrowserViewModel(new RegistryService(), computer);
        foreach (var source in new[]
                 {
                     computer, new RegistryLocation(RegistryHive.ClassesRoot), new RegistryLocation(RegistryHive.Users),
                     new RegistryLocation(RegistryHive.CurrentConfig), new RegistryLocation(Machine: "server"),
                     new RegistryLocation(RegistryHive.LocalMachine, "Software", "server"),
                     new RegistryLocation(RegistryHive.Users, "Software", "server")
                 })
        {
            check(
                source.GetAlternateHiveLocation() is null && !browser.SwitchHiveCommand.CanExecute(source),
                "Unsupported hives and remote locations cannot switch: " + source.Address);
            await browser.SwitchHiveCommand.ExecuteAsync(source);
            check(
                browser.Location == computer && browser.Error is null, "Executing an unsupported hive switch leaves the browser unchanged");
        }

        var software = new RegistryLocation(RegistryHive.CurrentUser, "Software");
        check(
            !browser.SwitchHiveCommand.CanExecute(null) && browser.SwitchHiveCommand.CanExecute(software),
            "A context key can switch independently of the current Computer location");
        var current = new BrowserViewModel(new RegistryService(), software);
        check(current.SwitchHiveCommand.CanExecute(null), "The shortcut can switch the current local key");
        var commandChanges = 0;
        current.SwitchHiveCommand.CanExecuteChanged += (_, _) => commandChanges++;
        current.CancelPending();
        await current.SwitchHiveCommand.ExecuteAsync(null);
        check(
            commandChanges > 0
            && !current.SwitchHiveCommand.CanExecute(null)
            && !current.SwitchHiveCommand.CanExecute(software)
            && current.Location == software
            && current.Error is null, "Closing a tab disables hive switching and ignores a captured switch command");
        browser.CancelPending();

        if (!registryTests)
        {
            return;
        }

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            var source = software with { View = view };
            var target = source.GetAlternateHiveLocation()!;
            var tab = new BrowserViewModel(new RegistryService(), source);
            try
            {
                await tab.NavigateAsync(source);
                check(tab.Error is null, "The source key can be read: " + tab.Error);
                tab.SelectedEntry = new RegistryEntry(source.Child("Unrelated selection"), null);
                await tab.SwitchHiveCommand.ExecuteAsync(null);
                check(
                    tab.Error is null && tab.Location == target && tab.CanGoBack,
                    "The shortcut switches the current key, preserves its view, and records history");
                await tab.BackCommand.ExecuteAsync(null);
                check(tab.Location == source && tab.CanGoForward, "Back restores the source hive after switching");
                await tab.ForwardCommand.ExecuteAsync(null);
                check(tab.Location == target, "Forward restores the destination hive after switching");
                await tab.SwitchHiveCommand.ExecuteAsync(null);
                check(tab.Error is null && tab.Location == source, "The shortcut also switches from HKLM to HKCU");

                var context = target with
                {
                    SubKey = string.Empty, View = view == RegistryView.Registry32 ? RegistryView.Registry64 : RegistryView.Registry32
                };
                await tab.SwitchHiveCommand.ExecuteAsync(context);
                check(
                    tab.Error is null && tab.Location == context.GetAlternateHiveLocation(),
                    "The context command uses its captured source path and registry view");

                var location = tab.Location;
                var entries = tab.Entries;
                var history = tab.GetHistory(false).ToArray();
                var missing = source.Child("RegeditHiveSwitchMissing." + Guid.NewGuid().ToString("N"));
                await tab.SwitchHiveCommand.ExecuteAsync(missing);
                check(
                    tab.Error is not null
                    && tab.Error.Contains(missing.GetAlternateHiveLocation()!.Address)
                    && tab.Location == location
                    && ReferenceEquals(tab.Entries, entries)
                    && history.SequenceEqual(tab.GetHistory(false)),
                    "A missing destination reports its path and preserves the current key, rows, and history");
            }
            finally
            {
                tab.CancelPending();
            }
        }
    }
}
