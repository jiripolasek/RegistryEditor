// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ToolPageDesignTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var target = new RegistryTargetViewModel(true);
        Reject(target.Location, check, "An explicit target cannot be empty");
        target.Address = "Computer";
        Reject(target.Location, check, "The Computer overview is not a registry target");
        target.Address = @"HKCU\Software\Example";
        var branch = target.Watch();
        check(
            branch.ValueName is null && branch.Location.View == RegistryView.Registry64,
            "An explicit branch watch preserves its local location and 64-bit view");
        target.ViewIndex = 1;
        target.WatchValue = true;
        Reject(target.Watch, check, "A value watch requires a name or an explicit default-value choice");
        target.DefaultValue = true;
        var unnamed = target.Watch();
        check(
            unnamed.ValueName == string.Empty && unnamed.Location.View == RegistryView.Registry32,
            "The default value and a branch remain distinct explicit targets");
        target.DefaultValue = false;
        target.ValueName = " Theme ";
        check(target.Watch().ValueName == " Theme ", "An explicit value name retains its exact whitespace");
        target.ValueName = " ";
        check(target.Watch().ValueName == " ", "A whitespace-only registry value name is distinct from the default value");
        target.ValueName = "invalid\0name";
        Reject(target.Watch, check, "Watch target validation rejects an invalid value name");
        target.Address = @"\\remote\HKLM\Software";
        Reject(target.Location, check, "An explicit watch cannot target a remote registry");
        target.Address = @"HKCU\Software";
        target.ViewIndex = -1;
        Reject(target.Location, check, "An invalid registry-view selection cannot silently select a view");

        var options = new SearchOptions(
            "Theme", Keys: false, Data: false, MatchCase: true, Whole: true, Types: [RegistryValueType.String, RegistryValueType.DWord]);
        var description = ToolPresentation.SearchOptionsText(options);
        check(
            description == "Search in: Value names\nMatch: Case sensitive · Whole text\nValue types: REG_SZ, REG_DWORD",
            "Saved-search details expose matching fields, case, whole-text and registry-type constraints");
        check(
            ToolPresentation.SearchOptionsText(new SearchOptions("Theme")).EndsWith("Value types: All")
            && ToolPresentation.SearchOptionsText(new SearchOptions("Theme")).Contains("Ignore case · Substring"),
            "Saved-search details describe default matching and unrestricted value types");
        var search = new SavedSearch("Theme search", [branch.Location], options);
        var workspace = new RegistryWorkspace("Investigation", [branch.Location], [search], [unnamed]);
        var sections = ToolPresentation.WorkspaceComparisons(workspace).ToArray();
        check(
            sections.Select(section => section.BeforeLabel).SequenceEqual(["Tabs", "Saved searches", "Watches"])
            && sections[0].Before.EndsWith("64-bit")
            && sections[1].Before == "Theme search"
            && sections[2].Before.EndsWith("32-bit · Value: (Default)"),
            "Workspace preview names its contents and preserves registry views and explicit default-value watches");
        check(
            ToolPresentation.WorkspaceComparisons(workspace with { Tabs = [], Searches = [] }).Single().BeforeLabel == "Watches",
            "Workspace previews omit empty content sections");

        var collection = new Collection();
        var alpha = Row("Alpha", @"HKCU\Software\Alpha", "Theme");
        var beta = Row("Beta", @"HKLM\Software\Beta", "Startup");
        collection.Replace([alpha, beta]);
        collection.Selected = [alpha];
        collection.Query = "aLPHa";
        check(
            collection.DisplayRows.SequenceEqual([alpha]) && collection.Selected.SequenceEqual([alpha]),
            "Tool collection filtering is case-insensitive and retains a visible selection");
        collection.Query = "Theme";
        check(collection.DisplayRows.SequenceEqual([alpha]), "Tool collection filters include descriptive text");
        collection.Query = @"HKLM\Software";
        check(
            collection.DisplayRows.SequenceEqual([beta]) && collection.Selected.Count == 0,
            "Registry-path filtering removes a hidden selection");
        collection.Query = "missing";
        check(collection.IsEmpty && collection.CountText.StartsWith("0 items"), "An empty filter result reports the visible count");
        collection.Query = string.Empty;
        check(
            collection.DisplayRows.Count == 2 && collection.Selected.Count == 0,
            "Clearing a tool filter restores rows without reviving hidden selection");
        collection.Query = "Beta";
        collection.Selected = [beta];
        var replacement = Row("Beta", @"HKLM\Software\Beta", "Changed");
        collection.Replace([alpha, replacement]);
        check(
            collection.DisplayRows.SequenceEqual([replacement]) && collection.Selected.SequenceEqual([replacement]),
            "Refreshing a filtered named collection restores its current row and selection");

        var notes = new NotesViewModel(
            NotesStoreTests.CreateStore(new AppSettings()), (_, _, _) => Task.CompletedTask, (_, _) => { }, false, _ => { },
            _ => Task.FromResult(true));
        notes.CurrentLocation = branch.Location;
        check(notes.Selected is null, "A dedicated Notes page does not select an invisible browser location");
        RegistryLocation? requested = RegistryLocation.Parse(@"HKCU\Software\ExplicitNote", RegistryView.Registry32);
        notes.TargetRequested += () => Task.FromResult<RegistryLocation?>(requested);
        await notes.NewCommand.ExecuteAsync(null);
        check(notes.Selected?.Location == requested, "New note uses the explicitly chosen key and registry view");
        notes.Text = "Retain this draft";
        var selected = notes.Selected;
        requested = null;
        await notes.NewCommand.ExecuteAsync(null);
        check(
            ReferenceEquals(selected, notes.Selected) && notes.Text == "Retain this draft",
            "Canceling an explicit target request preserves the selected note and its text");
        notes.Deactivate();
    }

    private static void Reject<T>(Func<T> action, Action<bool, string> check, string name)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            check(true, name);
            return;
        }

        check(false, name);
    }

    private static InvestigationRow Row(string name, string path, string description)
    {
        return new("Saved search", name, path, description, string.Empty, string.Empty, new object());
    }

    private sealed class Collection() : InvestigationViewModel("Saved searches", string.Empty, "Empty", string.Empty)
    {
        internal override IRelayCommand ActivateCommand { get; } = new RelayCommand(() => { });

        internal void Replace(IEnumerable<InvestigationRow> rows)
        {
            this.ReplaceNamedRows(rows);
        }
    }
}
