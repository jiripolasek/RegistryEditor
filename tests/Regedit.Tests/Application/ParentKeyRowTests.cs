// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ParentKeyRowTests
{
    public static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var location = RegistryLocation.Parse("HKCU\\Software\\Example", RegistryView.Registry32);
        var view = new BrowserViewModel(new RegistryService(), location) { ShowKeysInList = true };
        check(
            view.SortColumn == RegistrySortColumn.Name && !view.SortDescending,
            "Registry sorting starts with Name ascending");
        var sortChanges = new List<string>();
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(view.SortColumn) or nameof(view.SortDescending))
            {
                sortChanges.Add(args.PropertyName);
            }
        };
        view.SortCommand.Execute(RegistrySortColumn.Name);
        check(
            view.SortColumn == RegistrySortColumn.Name && view.SortDescending,
            "Activating the current registry sort reverses direction");
        view.SortCommand.Execute(RegistrySortColumn.Type);
        check(
            view.SortColumn == RegistrySortColumn.Type && !view.SortDescending,
            "Choosing another registry sort starts ascending");
        view.SortCommand.Execute(RegistrySortColumn.Name);
        check(
            sortChanges.Contains(nameof(view.SortColumn)) && sortChanges.Contains(nameof(view.SortDescending)) &&
            view.SortColumn == RegistrySortColumn.Name && !view.SortDescending,
            "Each sort activation exposes its current column and direction without changing the initial order contract");
        check(
            BrowserViewModel.SortLabel(RegistrySortColumn.Name, view.SortColumn, view.SortDescending) == "Name ↑" &&
            BrowserViewModel.SortStatus(RegistrySortColumn.Name, view.SortColumn, view.SortDescending) ==
            "Sorted ascending" &&
            BrowserViewModel.SortStatus(RegistrySortColumn.Data, view.SortColumn, view.SortDescending) == "Not sorted",
            "Compiled sort presentations distinguish the active direction from inactive columns");
        check(view.Entries.Count == 0, "Parent row is opt-in");
        view.ShowKeysInList = false;
        view.ShowParentKeyInList = true;
        check(view.Entries.Count == 0, "Parent preference alone does not show a row in values-only mode");
        view.ShowKeysInList = true;
        var parent = view.Entries.Single();
        check(
            parent.Name == ".." && parent.ParentLocation == location.GetNavigationParent() &&
            parent.ParentLocation?.View == RegistryView.Registry32,
            "Parent row uses the navigation parent and retains the registry view");
        check(
            parent is
            {
                IsParent: true, IsKey: false, IsStored: false, Key: null, Value: null, IsDefaultPlaceholder: false
            }, "Parent row is separate from stored keys, values, and default placeholders");
        check(
            parent.Type == "Parent key" && parent.Data == parent.ParentLocation!.Address &&
            parent.AccessibleName.Contains(parent.Data),
            "Parent row describes its destination for display and accessibility");
        check(
            new RegistryEntry(location, null).IsStored &&
            new RegistryEntry(null, new RegistryValue(string.Empty, RegistryValueType.String, [])).IsStored &&
            !new RegistryEntry(null, null, true).IsStored,
            "Stored-item actions include real default values but exclude placeholders");
        view.SelectedEntry = parent;
        var parentEntries = view.Entries;
        await view.SortCommand.ExecuteAsync(RegistrySortColumn.Name);
        check(
            ReferenceEquals(view.Entries, parentEntries) && ReferenceEquals(view.Entries.Single(), parent) &&
            ReferenceEquals(view.SelectedEntry, parent),
            "Sorting an unchanged parent-only list preserves the collection, row identity and selection");
        view.ShowKeysInList = false;
        check(
            view.SelectedEntry is null && view.Entries.Count == 0 && view.ShowParentKeyInList,
            "Hiding keys clears the parent selection but retains its preference");
        view.ShowKeysInList = true;
        view.ShowParentKeyInList = false;
        check(view.Entries.Count == 0, "Disabling the parent option removes the row immediately");

        foreach (var address in new[] { "HKCU", "\\\\SERVER\\HKLM", "\\\\SERVER\\HKLM\\Software" })
        {
            var target = RegistryLocation.Parse(address, RegistryView.Registry32);
            var other = new BrowserViewModel(new RegistryService(), target)
            {
                ShowKeysInList = true, ShowParentKeyInList = true
            };
            check(
                other.Entries.Single().ParentLocation == target.GetNavigationParent(),
                $"Parent row preserves the navigation scope of {address}");
            await other.NavigateAsync(target.GetComputer());
            check(
                other.Entries.All(entry => !entry.IsParent) && !other.CanGoUp,
                $"Computer root has no parent row for {address}");
        }

        var directory = Directory.CreateTempSubdirectory("RegeditParentRow.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, "{\"ShowKeysInList\":true}");
            var settings = AppSettings.Load(path);
            check(
                settings.LoadError is null && settings.ShowKeysInList && !settings.ShowParentKeyInList,
                "Older settings leave the parent row disabled");
            settings.ShowParentKeyInList = true;
            settings.Save(path);
            check(AppSettings.Load(path).ShowParentKeyInList, "Parent-row preference persists");
            settings.ShowParentKeyInList = false;
            settings.Save(path);
            check(!AppSettings.Load(path).ShowParentKeyInList, "Disabled parent-row preference persists");
        }
        finally
        {
            foreach (var file in directory.GetFiles())
            {
                file.Delete();
            }

            directory.Delete();
        }

        if (!registryTests)
        {
            return;
        }

        var service = new RegistryService();
        var software = RegistryLocation.Parse("HKCU\\Software");
        var fixture = software.Child("RegeditParentRowTests." + Guid.NewGuid().ToString("N"));
        service.CreateKey(software, fixture.Name);
        try
        {
            service.CreateKey(fixture, "Child");
            service.CreateKey(fixture, "..");
            service.Write(
                fixture,
                new RegistryValue(
                    "Marker", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "value")),
                null);
            view = new BrowserViewModel(service, fixture) { ShowKeysInList = true, ShowParentKeyInList = true };
            await view.NavigateAsync(fixture);
            check(
                view.Error is null && view.Entries[0].IsParent && view.Entries.Count(entry => entry.Name == "..") == 2,
                "Virtual parent row coexists with a real key named ..");
            var stored = view.Entries.Where(entry => entry.IsStored).ToArray();
            check(
                stored.Length == 3 && stored.All(entry => !entry.IsParent),
                "Stored-item selection excludes parent and unset-default rows");
            var parentRow = view.Entries[0];
            var valueRow = view.Entries.Single(entry => entry.Name == "Marker");
            view.SetSelection([parentRow, valueRow]);
            view.SortCommand.Execute(RegistrySortColumn.Data);
            foreach (var column in new[] { RegistrySortColumn.Name, RegistrySortColumn.Type, RegistrySortColumn.Data })
            {
                view.SortCommand.Execute(column);
                check(view.Entries[0].IsParent, $"Parent row stays first when sorting {column} ascending");
                view.SortCommand.Execute(column);
                check(view.Entries[0].IsParent, $"Parent row stays first when sorting {column} descending");
            }

            check(
                ReferenceEquals(view.Entries[0], parentRow) &&
                view.SelectedEntries.ToHashSet().SetEquals([parentRow, valueRow]),
                "Reordering preserves selection of the same parent and value row instances");

            view.Filter = "Marker";
            await view.PendingFilter;
            check(
                view.Entries.Count == 2 && view.Entries[0].IsParent && view.Status == "2 keys · 1 value · 1 shown",
                "Filtered counts exclude the pinned parent row");
            view.Filter = "no matches";
            await view.PendingFilter;
            check(
                view.Entries.Count == 1 && view.Entries[0].IsParent && view.Status.EndsWith("0 shown"),
                "Parent navigation remains available with no filter matches");
            await view.NavigateAsync(fixture.Child("Child"));
            check(
                view.Entries[0].ParentLocation == fixture && !view.HasSubKeys && view.Status == "0 keys · 0 values",
                "A parent row does not make a leaf report child keys");
            await view.NavigateAsync(view.Entries[0].ParentLocation!);
            check(
                view.Location == fixture && view.Error is null,
                "Activating the parent target navigates to the containing key");
            await view.BackCommand.ExecuteAsync(null);
            check(view.Location == fixture.Child("Child"), "Parent navigation participates in back history");
            var singular = fixture.Child("Singular");
            service.CreateKey(fixture, singular.Name);
            service.CreateKey(singular, "OnlyChild");
            service.Write(
                singular,
                new RegistryValue(
                    "OnlyValue", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "one")),
                null);
            await view.NavigateAsync(singular);
            check(view.Status == "1 key · 1 value", "Browser status uses singular key and value labels");
        }
        finally
        {
            service.DeleteKey(fixture);
        }
    }
}
