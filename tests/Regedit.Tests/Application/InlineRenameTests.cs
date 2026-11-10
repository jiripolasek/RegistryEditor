// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Tests.Application;

/// <summary>Renaming a list row in place: what may be edited, quick validation and the write with dialog fallback.</summary>
internal static class InlineRenameTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var software = RegistryLocation.Parse(@"HKCU\Software");
        var browser = new BrowserViewModel(new RegistryService(), software);
        var prompts = 0;
        browser.EditPromptRequested += _ =>
        {
            prompts++;
            return Task.FromResult(false);
        };
        var parent = new RegistryEntry(null, null, ParentLocation: software.Parent);
        var unset = new RegistryEntry(null, null, true);
        var root = new RegistryEntry(software.Parent, null);
        var value = new RegistryEntry(
            null,
            new RegistryValue(
                "Example", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "x")));
        var key = new RegistryEntry(software.Child("Example child"), null);
        check(
            !browser.CanRenameEntry(parent) && !browser.CanRenameEntry(unset) && !browser.CanRenameEntry(root) &&
            browser.CanRenameEntry(value) && browser.CanRenameEntry(key),
            "Parent rows, the unset default value and hive roots cannot be edited in place");
        check(
            browser.RenameEntryError(key, string.Empty) == "Enter a name." &&
            browser.RenameEntryError(key, @"a\b")?.Contains("backslash") == true &&
            browser.RenameEntryError(value, @"a\b") is null && browser.RenameEntryError(parent, "x") is not null,
            "Inline names are checked before writing: empty names and backslashes in key names are refused");

        browser.ShowKeysInList = true;
        await browser.NavigateAsync(software);
        var sibling = browser.Entries.FirstOrDefault(entry => entry.IsKey);
        check(
            sibling is not null &&
            browser.RenameEntryError(key, sibling.Name.ToUpperInvariant()) == "A key with this name already exists." &&
            browser.RenameEntryError(value, sibling.Name) is null,
            "A key name already used by a sibling key is refused, case-insensitively; values have their own names");
        check(prompts == 0, "Checking inline names never opens the rename dialog");

        if (!registryTests)
        {
            return;
        }

        var service = new RegistryService();
        var fixture = software.Child("RegeditInlineRename." + Guid.NewGuid().ToString("N"));
        service.CreateKey(software, fixture.Name);
        try
        {
            service.CreateKey(fixture, "Before");
            service.Write(
                fixture,
                new RegistryValue(
                    "Old", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "data")), null);
            service.Write(
                fixture,
                new RegistryValue(
                    "Hidden", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "other")),
                null);
            var list = new BrowserViewModel(service, fixture);
            var shown = new List<RegistryNamePrompt>();
            list.EditPromptRequested += form =>
            {
                shown.Add((RegistryNamePrompt)form);
                return Task.FromResult(false);
            };
            list.ShowKeysInList = true;
            await list.NavigateAsync(fixture);

            await list.RenameEntryAsync(list.Entries.Single(entry => entry.IsKey && entry.Name == "Before"), "After");
            check(
                shown.Count == 0 && list.Entries.Any(entry => entry.IsKey && entry.Name == "After") &&
                !list.Entries.Any(entry => entry.Name == "Before"),
                "An inline key rename writes without a dialog and refreshes the list");
            check(
                list.SelectedEntries is [{ IsKey: true, Name: "After" }],
                "The renamed key is selected again after the list reloads");

            await list.RenameEntryAsync(list.Entries.Single(entry => entry.Name == "Old"), "New");
            check(
                shown.Count == 0 && service.ReadValue(fixture, "New")?.DisplayData == "data" &&
                service.ReadValue(fixture, "Old") is null, "An inline value rename keeps the data under the new name");
            check(
                list.SelectedEntries is [{ IsKey: false, Name: "New" }],
                "The renamed value is selected again after the list reloads");

            var dialog = new BrowserViewModel(service, fixture) { ShowKeysInList = true };
            dialog.EditPromptRequested += form =>
            {
                ((RegistryNamePrompt)form).Name = "ViaDialog";
                return Task.FromResult(true);
            };
            await dialog.NavigateAsync(fixture);
            dialog.SelectedEntry = dialog.Entries.Single(entry => entry.IsKey && entry.Name == "After");
            await dialog.RenameCommand.ExecuteAsync(null);
            check(
                dialog.SelectedEntries is [{ IsKey: true, Name: "ViaDialog" }],
                "A rename through the dialog also keeps the renamed item selected");

            // A collision the list could not see (the other value is filtered out) fails in the registry.
            list.Filter = "New";
            await list.RenameEntryAsync(list.Entries.Single(entry => entry.Name == "New"), "Hidden");
            check(
                shown.Count == 1 && shown[0].Name == "Hidden" && shown[0].Error is not null &&
                service.ReadValue(fixture, "New")?.DisplayData == "data" &&
                service.ReadValue(fixture, "Hidden")?.DisplayData == "other" && list.SelectedEntries.Count == 0,
                "A failed inline rename reopens the rename dialog with the typed name and the error, changing nothing");
            check(list.CanPrompt, "Closing the recovery dialog leaves the list ready for the next edit");
        }
        finally
        {
            service.DeleteKey(fixture);
        }
    }
}
