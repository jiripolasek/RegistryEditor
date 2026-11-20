// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ComputerRootTests
{
    public static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var local = RegistryLocation.Parse("Computer\\", RegistryView.Registry32);
        var remote = RegistryLocation.Parse("\\\\SERVER01\\", RegistryView.Registry32);
        check(
            local.IsComputer && local.Address == "Computer" && local.GetNavigationParent() is null, "Computer address is a navigable root");
        check(
            remote.IsComputer && remote.Name == "SERVER01" && remote.Address == "\\\\SERVER01",
            "Server root addresses round-trip without a hive");
        check(RegistryLocation.Parse(remote.Address, remote.View) == remote, "Remote root round-trip retains its registry view");
        var localHives = local.GetHives();
        var remoteHives = remote.GetHives();
        check(
            localHives.Length == 5
            && localHives.All(hive => !hive.IsComputer && hive.Machine is null && hive.View == RegistryView.Registry32),
            "Computer lists five local hives in its selected view");
        check(
            remoteHives.Select(hive => hive.Hive).SequenceEqual(new RegistryHive?[] { RegistryHive.LocalMachine, RegistryHive.Users })
            && remoteHives.All(hive => hive.Machine == "SERVER01" && hive.View == RegistryView.Registry32),
            "Server lists only its supported remote hives");
        check(
            remoteHives.All(hive => hive.GetNavigationParent() == remote && hive.Parent is null),
            "Hive navigation reaches the computer without giving hives a mutable key parent");
        check(
            remote.GetScopeRoots().SequenceEqual(remoteHives)
            && local.GetScopeRoots().SequenceEqual(localHives.Where(hive => hive.Hive is RegistryHive.LocalMachine or RegistryHive.Users)),
            "Whole-computer search, print and export visit only HKLM and HKU in the selected view");
        check(
            localHives.All(hive => hive.GetScopeRoots().SequenceEqual([hive])),
            "Selecting an alias hive explicitly preserves that hive as the branch root");
        var key = remoteHives[0].Child("Software");
        check(
            key.GetScopeRoots().SequenceEqual(new[] { key }) && key.GetScopeRoots(true).SequenceEqual(remoteHives),
            "Key search defaults to its branch and can explicitly select all hives");

        var service = new RegistryService();

        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                check(true, name);
                return;
            }

            check(false, name);
        }

        Reject(() => local.Child("Child"), "A computer cannot acquire a registry subkey directly");
        Reject(() => service.Open(remote), "A computer cannot be opened as a registry key");
        Reject(() => service.CreateKey(local, "Child"), "Creating a key at the computer root is rejected");
        Reject(
            () => service.Write(local, new RegistryValue("test", RegistryValueType.Binary, [1]), null),
            "Writing a value at the computer root is rejected");
        Reject(() => service.DeleteKey(localHives[0]), "Root hives remain protected from deletion");
        Reject(() => service.RenameKey(localHives[0], "Renamed"), "Root hives remain protected from renaming");
        Reject(
            () => RegistryWorkspace.ValidateLocation(local with { SubKey = "Invalid" }, true),
            "Malformed computer locations cannot be persisted");
        Reject(
            () => RegistryWorkspace.ValidateLocation(remote with { Machine = "SERVER\\HKLM" }, true),
            "A server name cannot disguise a hive as a computer root");
        Reject(
            () => RegistrySearch.Validate(new SavedSearch("Invalid", [local], new SearchOptions("needle"))),
            "Saved searches contain real hive roots rather than a computer placeholder");

        var workspace = new RegistryWorkspace("Computers", [local, remote, key], [], []);
        var restored = JsonSerializer.Deserialize<RegistryWorkspace>(JsonSerializer.Serialize(workspace))!;
        restored.Validate();
        check(restored.Tabs.SequenceEqual(workspace.Tabs), "Workspaces retain computer roots alongside key tabs");
        var tab = JsonSerializer.Deserialize<SavedTab>(JsonSerializer.Serialize(new SavedTab(remote.Address, true)))!;
        check(
            RegistryLocation.Parse(tab.Address, tab.Is32Bit ? RegistryView.Registry32 : RegistryView.Registry64) == remote,
            "Saved tabs restore a server root and view");
        check(new Favorite("Server", remote.Address, true).Matches(remote), "Favorites can navigate to a computer root");

        var journalFolder = Directory.CreateTempSubdirectory("RegeditComputerRoot.");
        try
        {
            var journal = new RegistryJournal(journalFolder.FullName);
            journal.RecordActivity("Computer activity", local, "Test activity without registry changes", () => { });
            check(journal.ReadEntries().Single().ActivityLocation == local, "Whole-computer activities retain their scope in the journal");
        }
        finally
        {
            foreach (var file in journalFolder.GetFiles())
            {
                file.Delete();
            }

            journalFolder.Delete();
        }

        var viewModel = new BrowserViewModel(service, localHives[0]);
        await viewModel.UpCommand.ExecuteAsync(null);
        check(
            viewModel.Location == local && !viewModel.CanGoUp && !viewModel.IsKeyLocation && viewModel.HasSubKeys,
            "Up from a hive opens its computer and disables key editing");
        check(
            viewModel.Entries.Count == 5
            && viewModel.Entries.All(entry => entry.IsKey && entry.Type == "Hive" && !entry.IsDefaultPlaceholder),
            "Computer pane contains only hives with no default value");
        check(
            viewModel.Title == "Computer · 32-bit" && viewModel.Status == "5 hives",
            "Computer title and status describe the hive overview");
        var previousEntries = viewModel.Entries;
        var entryUpdates = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Entries))
            {
                entryUpdates++;
            }
        };
        await viewModel.NavigateAsync(remote);
        check(
            viewModel.Error is null && viewModel.Title == "SERVER01 · 32-bit" && viewModel.Entries.Count == 2,
            "Server overview loads without querying a remote registry");
        check(
            entryUpdates == 1 && previousEntries.Count == 5 && !ReferenceEquals(previousEntries, viewModel.Entries),
            "Navigation publishes one complete list without mutating the previous contents");
        entryUpdates = 0;
        viewModel.SelectedEntry = viewModel.Entries[0];
        viewModel.Filter = "LOC";
        var firstFilter = viewModel.PendingFilter;
        viewModel.Filter = "LOCAL_MACHINE";
        check(
            entryUpdates == 0 && !viewModel.HasSelection && !viewModel.DeleteCommand.CanExecute(null),
            "Typing leaves the current list in place and invalidates edit selection immediately");
        await Task.WhenAll(firstFilter, viewModel.PendingFilter);
        check(
            entryUpdates == 1 && viewModel.Entries.Count == 1 && viewModel.Entries[0].Key!.Hive == RegistryHive.LocalMachine,
            "A typing burst publishes only the final matching list");
        viewModel.Filter = "USERS";
        var clearedFilter = viewModel.PendingFilter;
        viewModel.Filter = string.Empty;
        await clearedFilter;
        check(viewModel.Entries.Count == 2 && entryUpdates == 2, "Clearing the text restores rows immediately and cancels the old match");
        viewModel.Filter = "missing";
        var previousLocationFilter = viewModel.PendingFilter;
        await viewModel.BackCommand.ExecuteAsync(null);
        await previousLocationFilter;
        check(viewModel.Filter.Length == 0 && viewModel.Entries.Count == 5, "Pending filter results cannot replace another key's rows");
        check(viewModel.Location == local, "Back restores the previous computer root");
        await viewModel.ForwardCommand.ExecuteAsync(null);
        check(viewModel.Location == remote, "Forward restores the server root");
        await viewModel.NavigateAsync(remote with { View = RegistryView.Registry64 });
        check(
            !viewModel.Is32Bit
            && viewModel.Title == "SERVER01"
            && viewModel.Entries.All(entry => entry.Key!.View == RegistryView.Registry64),
            "Changing the computer view updates every listed hive");

        if (!registryTests)
        {
            return;
        }

        var marker = "Regedit.Scope." + Guid.NewGuid().ToString("N");
        using (var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
        {
            var subKey = @"Software\Classes\" + marker;
            using (var aliasKey = currentUser.CreateSubKey(subKey))
            {
                aliasKey.SetValue("Marker", marker);
            }

            try
            {
                using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
                using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry32);
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                using var merged = classes.OpenSubKey(marker);
                using var backing = users.OpenSubKey(identity.User!.Value + "\\" + subKey);
                check(
                    Equals(merged?.GetValue("Marker"), marker) && Equals(backing?.GetValue("Marker"), marker),
                    "An explicitly browsed HKCR marker is also reachable through its user hive in the same registry view");
            }
            finally
            {
                currentUser.DeleteSubKeyTree(subKey);
            }
        }

        var hive = RegistryLocation.Parse("HKCU");
        await viewModel.NavigateAsync(hive.GetComputer());
        await viewModel.NavigateAsync(viewModel.Entries.Single(entry => entry.Key == hive).Key!);
        check(
            viewModel.Error is null && viewModel.Location == hive && viewModel.IsKeyLocation,
            "Opening an overview hive reads the actual registry and enables editing");
        await viewModel.BackCommand.ExecuteAsync(null);
        check(viewModel.Location == hive.GetComputer() && viewModel.Entries.Count == 5, "Back from an opened hive restores the overview");
    }
}
