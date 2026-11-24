// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;


namespace JPSoftworks.Regedit.Tests.Application;

internal static class RegistryListColumnsTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var browser = new BrowserViewModel(new RegistryService(), RegistryLocation.Parse("HKCU"));
        browser.SetListColumns([RegistrySortColumn.Name, RegistrySortColumn.Children]);
        var failures = new List<Exception>();
        browser.DetailsLoadFailed += failures.Add;
        var failure = new InvalidOperationException("Metadata notification failed");
        var faulty = new RegistryEntry(RegistryLocation.Parse(@"HKCU\Software"), null);
        faulty.PropertyChanged += (_, _) => throw failure;
        await browser.LoadVisibleDetailsAsync([faulty]);
        check(
            failures.Count == 1 && ReferenceEquals(failures[0], failure),
            "Unexpected failures after awaiting visible metadata are observed and reported for logging");
        var missing = new RegistryEntry(RegistryLocation.Parse(@"HKCU\Software\RegeditMissing." + Guid.NewGuid().ToString("N")), null);
        await browser.LoadVisibleDetailsAsync([missing]);
        check(
            failures.Count == 1 && missing.ChildrenText == "Unavailable" && missing.LastWriteText == "Unavailable",
            "An ordinary missing key remains unavailable without reporting an unexpected metadata failure");
        browser.CancelPending();

        var directory = Directory.CreateTempSubdirectory("RegeditColumns.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, """{"FontFamily":"Segoe UI","FontSize":15}""");
            var settings = AppSettings.Load(path);
            var preferences = new SettingsViewModel(settings, change => settings.UpdatePreferences(change, path));
            check(
                preferences.ListColumns.SequenceEqual(RegistryColumns.Defaults())
                && preferences.ShowListToolbar
                && !preferences.UseCustomDataFont
                && preferences.DataFontFamily == MonospaceFonts.FallbackFamily
                && preferences.DataFontSize == 15,
                "Legacy preferences keep their columns and size while automatic Data typography defaults to monospace");
            preferences.ToggleColumnCommand.Execute(RegistrySortColumn.Children);
            preferences.MoveColumnLeftCommand.Execute(RegistrySortColumn.Children);
            preferences.MoveColumn(RegistrySortColumn.Data, 0);
            preferences.ToggleColumnCommand.Execute(RegistrySortColumn.Type);
            preferences.UseCustomDataFont = true;
            preferences.DataFontFamily = " Consolas ";
            preferences.DataFontSize = 21;
            preferences.ShowListToolbar = false;
            var saved = AppSettings.Load(path);
            check(
                saved.ListColumns.SequenceEqual([RegistrySortColumn.Data, RegistrySortColumn.Name, RegistrySortColumn.Children])
                && !saved.ShowListToolbar
                && saved.DataFont == new RegistryFont("Consolas", 21)
                && saved.FontSize == 15
                && saved.FontFamily == "Segoe UI",
                "Column order, toolbar visibility and independent Data typography survive a new settings instance");
            preferences.DataFontSize = double.NaN;
            check(
                preferences.IsSaveErrorOpen && preferences.DataFontSize == 21 && AppSettings.Load(path).DataFont == saved.DataFont,
                "An invalid Data font size rolls back without changing the accepted font");
            preferences.UseCustomDataFont = false;
            check(
                preferences.DataFontFamily == MonospaceFonts.FallbackFamily
                && preferences.DataFontSize == 15
                && AppSettings.Load(path).DataFont is null,
                "Disabling the override clears the custom Data font and restores the picker defaults");
            preferences.ToggleColumnCommand.Execute(RegistrySortColumn.Children);
            preferences.ToggleColumnCommand.Execute(RegistrySortColumn.Name);
            preferences.ToggleColumnCommand.Execute(RegistrySortColumn.Data);
            check(AppSettings.Load(path).ListColumns.SequenceEqual([RegistrySortColumn.Data]), "The last visible column remains available");
            var onlyData = new BrowserViewModel(
                new RegistryService(), RegistryLocation.Parse("HKCU"), new RegistryEntryClipboard(), settings: settings);
            check(onlyData.SortColumn == RegistrySortColumn.Data, "A new tab sorts by the first visible column when Name is hidden");
            onlyData.RestoreLayout(new BrowserLayout(SortColumn: "Name", SortDescending: true));
            check(onlyData.SortColumn == RegistrySortColumn.Data && !onlyData.SortDescending, "An old tab falls back from a hidden sort");
            onlyData.CancelPending();
            check(
                RegistryColumns.FromNames(["Future", "Size", "Size", "0", "Notes"])
                    .SequenceEqual([RegistrySortColumn.Size, RegistrySortColumn.Notes])
                && RegistryColumns.FromNames(["Future"]).SequenceEqual(RegistryColumns.Defaults()),
                "Unknown and duplicate column IDs recover without hiding the entire header");
            var layout = new BrowserLayout().WithWidth(RegistrySortColumn.Notes, 301);
            settings.SaveSession([new SavedTab("HKCU", Layout: layout with { SortColumn = "Notes", SortDescending = true })], false, path);
            var restored = AppSettings.Load(path).Tabs.Single().Layout!;
            check(
                restored.Width(RegistrySortColumn.Notes) == 301 && restored.SortColumn == "Notes" && restored.SortDescending,
                "The optional column width and sort persist in the tab layout");
            if (registryTests)
            {
                await CheckRegistryAsync(settings, path, check);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task CheckRegistryAsync(AppSettings settings, string path, Action<bool, string> check)
    {
        var service = new RegistryService();
        var software = RegistryLocation.Parse(@"HKCU\Software");
        var root = software.Child("RegeditColumns." + Guid.NewGuid().ToString("N"));
        service.CreateKey(software, root.Name);
        using var notes = NotesStoreTests.CreateStore(settings, move => settings.Save(path, move));
        var browser = new BrowserViewModel(service, root, new RegistryEntryClipboard(), settings: settings, notes: notes)
        {
            ShowKeysInList = true, ShowParentKeyInList = true
        };
        try
        {
            var first = root.Child("A");
            var last = root.Child("Z");
            service.CreateKey(root, first.Name);
            service.CreateKey(root, last.Name);
            service.CreateKey(last, "Grandchild");
            var unnamed = new RegistryValue(string.Empty, RegistryValueType.DWord, [1, 0, 0, 0]);
            service.Write(root, unnamed, null);
            service.Write(root, new RegistryValue("(Default)", RegistryValueType.Binary, []), null);
            service.Write(root, new RegistryValue("Alpha", RegistryValueType.Binary, new byte[16]), null);
            service.Write(root, new RegistryValue("Zulu", RegistryValueType.Binary, [0, 0]), null);
            var originalNote = notes.Get(last);
            originalNote.Text = "Original note";
            notes.Save(originalNote);
            await browser.NavigateAsync(root);
            browser.SetListColumns(RegistryColumns.All);
            var keyTime = service.ReadKeyDetails(root, false).LastWriteTimeUtc;
            browser.ShowKeysInList = false;
            check(
                keyTime is { } written
                && browser.Entries.Count == 4
                && browser.Entries.All(row =>
                    row.LastWriteTimeUtc == written
                    && row.LastWriteText == written.ToLocalTime().ToString("G")
                    && row.LastWriteHelp.Contains("containing key", StringComparison.Ordinal)),
                "Values-only rows share the containing key's real last-write time, including the stored default");
            browser.ShowKeysInList = true;
            check(
                browser.Entries[0].IsParent && browser.Entries[0].LastWriteText.Length == 0, "The navigation row has no modification time");
            var a = browser.Entries.Single(item => item.Key == first);
            var z = browser.Entries.Single(item => item.Key == last);
            check(
                !a.DetailsRead && !z.DetailsRead && z.NotePreview == "Original note",
                "Enabling metadata columns reads saved notes without enumerating child keys");
            await browser.LoadVisibleDetailsAsync([z]);
            check(
                !a.DetailsRead
                && z.DetailsRead
                && z.LinkRead
                && z.ChildrenText == "1 key · 0 values"
                && z.LastWriteTimeUtc == service.ReadKeyDetails(last, false).LastWriteTimeUtc
                && z.LastWriteText.Length > 0, "Realizing one child populates its own metadata without opening its sibling");
            browser.Filter = "Zulu";
            await browser.SortCommand.ExecuteAsync(RegistrySortColumn.LastWrite);
            check(!a.DetailsRead, "Sorting a filtered value list leaves unrelated key metadata unread");
            await browser.SortCommand.ExecuteAsync(RegistrySortColumn.Name);
            browser.Filter = string.Empty;
            var draft = new NoteDraft(first, string.Empty) { Text = "Updated note" };
            notes.Save(draft);
            check(a.NotePreview == "Updated note", "Saving a note updates an already displayed key");
            draft.Text = string.Empty;
            notes.Save(draft);
            check(a.NotePreview.Length == 0, "Removing a saved note clears the displayed cell");
            var alpha = browser.Entries.Single(entry => entry.Name == "Alpha");
            var zulu = browser.Entries.Single(entry => entry.Name == "Zulu");
            var exactTarget = new KeyNote(root, string.Empty) { ValueName = "Alpha" };
            var sharedTarget = exactTarget with { MatchRule = new ValueNoteRule(RegistryValueType.Binary, 16) };
            var sharedDraft = notes.Get(sharedTarget);
            sharedDraft.Text = "Saved shared note";
            notes.Save(sharedDraft);
            var exactDraft = notes.Get(exactTarget);
            exactDraft.Text = "Unsaved exact draft";
            check(alpha.NotePeek == "Saved shared note" && alpha.SavedNote is { } shown && notes.Get(shown) == sharedDraft,
                "The browser marker retains the identity of the displayed note even when an unsaved exact draft matches");
            notes.Save(exactDraft);
            check(alpha.NotePeek == "Unsaved exact draft" && alpha.SavedNote is { } saved && notes.Get(saved) == exactDraft,
                "Saving an exact note updates the browser marker text and target together");
            browser.SetSelection([alpha, zulu]);
            foreach (var column in RegistryColumns.All)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await browser.SortCommand.ExecuteAsync(column);
                    var entries = browser.Entries;
                    check(
                        entries.Count == 7
                        && entries[0].IsParent
                        && entries[1].IsKey
                        && entries[2].IsKey
                        && entries[3].IsDefaultValue
                        && entries.Skip(4).All(entry => entry.Value?.Name.Length > 0),
                        $"Parent, keys, stored default and named values stay grouped for {column}, descending={browser.SortDescending}");
                    if (column == RegistrySortColumn.Children)
                    {
                        check(entries[1] == (browser.SortDescending ? z : a), "Children sorts by actual immediate counts");
                    }

                    if (column == RegistrySortColumn.Size)
                    {
                        var sizes = entries.Skip(4).Select(entry => entry.Value!.Data.Length);
                        check(
                            sizes.SequenceEqual(browser.SortDescending ? [16, 2, 0] : [0, 2, 16]),
                            "Size orders numerically, including empty stored data");
                    }
                }
            }

            check(browser.SelectedEntries.ToHashSet().SetEquals([alpha, zulu]), "Sorting and late metadata preserve multi-selection");
            browser.Filter = "Zulu";
            await browser.PendingFilter;
            check(
                browser.Entries.Count == 2 && browser.Entries[0].IsParent && browser.Entries[1] == zulu && !browser.HasSelection,
                "Filtering retains the parent and clears selections including hidden rows");
            service.DeleteValue(root, unnamed);
            browser.Filter = string.Empty;
            await browser.RefreshCommand.ExecuteAsync(null);
            var refreshedTime = service.ReadKeyDetails(root, false).LastWriteTimeUtc;
            check(
                refreshedTime > keyTime
                && browser.Entries.Where(row => !row.IsKey && !row.IsParent).All(row => row.LastWriteTimeUtc == refreshedTime),
                "Refresh updates the containing-key time on unchanged values and the newly unset default");
            check(
                browser.Entries[3].IsDefaultPlaceholder
                && browser.Entries[3].SizeText.Length == 0
                && browser.Entries.Single(entry => entry.Value?.Name == "(Default)").SizeText == "0",
                "An unset default remains the first value and is distinct from an empty named value");

            notes.Delete(exactDraft);
            alpha = browser.Entries.Single(entry => entry.Name == "Alpha");
            zulu = browser.Entries.Single(entry => entry.Name == "Zulu");
            var zuluNote = notes.Get(new KeyNote(root, string.Empty) { ValueName = "Zulu" });
            zuluNote.Text = "Unchanged value note";
            notes.Save(zuluNote);
            var untouched = zulu.SavedNote;
            browser.SelectedEntry = alpha;
            browser.EditPromptRequested += prompt =>
            {
                ((RegistryValueEditorViewModel)prompt).Data = "01";
                return Task.FromResult(true);
            };
            await browser.EditBinaryCommand.ExecuteAsync(null);
            check(alpha.Value?.Data.Length == 1 && !alpha.HasNote && untouched is not null && zulu.SavedNote == untouched &&
                  browser.Entries.Contains(alpha) && browser.Entries.Contains(zulu),
                "An in-place edit reevaluates its rule note when byte length changes and retains neighboring rows and notes");
        }
        finally
        {
            browser.CancelPending();
            service.DeleteKey(root);
        }
    }
}
