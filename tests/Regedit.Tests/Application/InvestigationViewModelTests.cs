// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.Specialized;
using System.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class InvestigationViewModelTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var settings = new AppSettings();
        var registry = new RegistryService();
        var store = new InvestigationStore(settings, () => { });
        var shell = new MainWindowViewModel(
            settings, store, registry, target => new BrowserViewModel(registry, target),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        var origin = RegistryLocation.Parse(@"\\server\HKLM\Software", RegistryView.Registry32);
        var bytes = new byte[128 * 1024];
        var large = new RegistrySearchMatch(
            origin, "Large", new RegistryValue("Large", RegistryValueType.Binary, bytes), "Value name", "Large");
        var matches = Enumerable.Range(0, 250).Select(_ => new InvestigationRow(large)).ToArray();
        bytes[0] = 0xAF;
        check(
            matches.All(row => row.Title == "Large") && matches[3].Before.Contains("Raw preview: AF00") &&
            matches[3].Before.Length < 6000 && ReferenceEquals(matches[3].Before, matches[3].Before),
            "Large search results retain the typed source and format a bounded row preview once when selected");
        shell.OpenBrowser(origin);
        var source = (BrowserTab)shell.ActiveTab!;
        var search = new SavedSearch("needle", [origin], new SearchOptions("needle"));
        var gate = new TaskCompletionSource<RegistrySearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var results = new SearchResultsViewModel(
            search, store, (_, token) =>
            {
                captured = token;
                return gate.Task.WaitAsync(token);
            }, (match, select) => shell.OpenBrowser(match.Location, match.ValueName, select));
        shell.SelectTab(results);
        var running = results.RunCommand.ExecuteAsync(null);
        var searchChanges = new List<NotifyCollectionChangedAction>();
        results.Rows.CollectionChanged += (_, args) => searchChanges.Add(args.Action);
        shell.CloseTab(source);
        check(
            results.RunCommand.IsRunning && !captured.IsCancellationRequested && shell.ActiveTab == results,
            "Closing a search's originating browser keeps its results scan alive");
        var hit = new RegistrySearchMatch(
            origin.Child("Match"), string.Empty, new RegistryValue(string.Empty, RegistryValueType.Binary, [0, 255]),
            "Value name", "needle");
        gate.SetResult(new RegistrySearchResults(Enumerable.Repeat(hit, 10000).ToArray(), 2, false));
        await running;
        check(
            searchChanges is [NotifyCollectionChangedAction.Reset] && results.Rows.Count == 10000 &&
            results.Description.Contains("2 inaccessible") && !results.OpenCommand.CanExecute(null),
            "A completed 10000-match search publishes one reset and requires a selection");
        results.Selected = [results.Rows[0]];
        results.OpenInBackgroundCommand.Execute(results.Rows[0]);
        check(
            shell.ActiveTab == results && shell.Tabs[^1] is BrowserTab { InitialValue: "" } background &&
            background.Browser.Location == hit.Location,
            "Middle-open preserves the original machine, 32-bit view, key and unnamed value");
        results.OpenCommand.Execute(null);
        check(
            shell.ActiveTab is BrowserTab { InitialValue: "" } active && active.Browser.Location == hit.Location,
            "Opening a result activates a browser at the original value");
        var canceled = new SearchResultsViewModel(
            search, store, async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new RegistrySearchResults([], 0, false);
            }, (_, _) => { });
        shell.SelectTab(canceled);
        var pending = canceled.RunCommand.ExecuteAsync(null);
        shell.CloseTab(canceled);
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        check(
            canceled.RunCommand.IsCancellationRequested && canceled.Rows.Count == 0,
            "Closing the results tab cancels its scan and publishes no late rows");

        var failSave = false;
        var saves = 0;
        var sharedStore = new InvestigationStore(
            settings, () =>
            {
                if (failSave)
                {
                    throw new IOException("disk full");
                }

                saves++;
            });
        var saved = new SavedSearchesViewModel(sharedStore, _ => { });
        sharedStore.SaveSearch(search);
        saved.Selected = [saved.Rows.Single()];
        failSave = true;
        string? error = null;
        saved.Notice += (_, message, _) => error = message;
        saved.RemoveCommand.Execute(null);
        check(
            error == "disk full" && saved.Selected.Count == 1 && settings.Searches.Single() == search,
            "A failed removal rolls back the saved query and retains its selection");
        failSave = false;
        saved.RemoveCommand.Execute(null);
        check(
            saves == 2 && saved.Rows.Count == 0 && !saved.RunCommand.CanExecute(null),
            "Committed changes refresh saved searches and invalidate selection commands");

        var workspace = new RegistryWorkspace("Remote", [origin], [search], []);
        sharedStore.SaveWorkspace(workspace);
        var restored = 0;
        var workspaces = new WorkspacesViewModel(
            sharedStore, _ => workspace, (item, _, _) =>
            {
                if (item == workspace)
                {
                    restored++;
                }

                return Task.FromResult(new WorkspaceRestoreResult(1, 0, 0, false, []));
            });
        workspaces.PreviewRequested += _ => Task.FromResult<WorkspaceRestoreOptions?>(new WorkspaceRestoreOptions());
        workspaces.Selected = [workspaces.Rows.Single()];
        var consent = false;
        string[]? machines = null;
        workspaces.ConfirmRemote += names =>
        {
            machines = names;
            return Task.FromResult(consent);
        };
        await workspaces.OpenCommand.ExecuteAsync(null);
        check(
            restored == 0 && machines is ["server"], "Declining a workspace's deduplicated remote hosts opens nothing");
        consent = true;
        await workspaces.OpenCommand.ExecuteAsync(null);
        check(restored == 1, "The confirmed workspace is restored through the shell");

        store.SaveSearch(search);
        shell.OpenSavedSearchesCommand.Execute(null);
        var savedTab = (SavedSearchesViewModel)shell.ActiveTab!;
        savedTab.Selected = [savedTab.Rows.Single()];
        shell.OpenWorkspacesCommand.Execute(null);
        var workspaceTab = (WorkspacesViewModel)shell.ActiveTab!;
        workspaceTab.Selected = [workspaceTab.Rows.Single()];
        shell.OpenSavedSearchesCommand.Execute(null);
        check(
            shell.ActiveTab == savedTab && ReferenceEquals(savedTab.Selected.Single().Source, search) &&
            shell.Tabs.OfType<SavedSearchesViewModel>().Count() == 1,
            "Reopening Saved searches preserves the selected query in the existing tab");
        shell.OpenWorkspacesCommand.Execute(null);
        check(
            shell.ActiveTab == workspaceTab && ReferenceEquals(workspaceTab.Selected.Single().Source, workspace) &&
            shell.Tabs.OfType<WorkspacesViewModel>().Count() == 1,
            "Reopening Workspaces preserves the selected workspace in the existing tab");

        store.SaveSearch(search with { Name = "Another query" });
        var replacement = workspace with { Name = workspace.Name.ToUpperInvariant() };
        store.SaveWorkspace(replacement);
        check(
            savedTab.Rows.Count == 2 && ReferenceEquals(savedTab.Selected.Single().Source, search) && ReferenceEquals(
                workspaceTab.Selected.Single().Source, replacement),
            "A collection update retains other selected rows and follows a replacement with the same name");

        var many = new AppSettings();
        many.Searches.AddRange(Enumerable.Range(1, 10000).Select(n => search with { Name = n.ToString() }));
        var bulkStore = new InvestigationStore(many, () => { });
        var bulk = new SavedSearchesViewModel(bulkStore, _ => { });
        bulk.Selected = [bulk.Rows[0], bulk.Rows[5000], bulk.Rows[^1]];
        var changes = new List<NotifyCollectionChangedAction>();
        bulk.Rows.CollectionChanged += (_, args) => changes.Add(args.Action);
        var properties = new List<string?>();
        ((INotifyPropertyChanged)bulk.Rows).PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        bulkStore.RemoveSearches(new HashSet<SavedSearch> { many.Searches[5000] });
        check(
            changes is [NotifyCollectionChangedAction.Reset] && properties is ["Count", "Item[]"] &&
            bulk.Rows.Count == 9999 && bulk.Selected.SequenceEqual([bulk.Rows[0], bulk.Rows[^1]]),
            "A bulk refresh notifies once and restores only the surviving rows of a multiple selection");
        var first = bulk.Rows[0];
        try
        {
            bulk.Rows.ReplaceAll(bulk.Rows.Select(row => row == first ? row : throw new InvalidOperationException()));
        }
        catch (InvalidOperationException)
        {
        }

        check(
            bulk.Rows.Count == 9999 && bulk.Rows[0] == first && changes.Count == 1,
            "A failed projection leaves the bound collection intact and sends no reset");
        bulk.Rows.ReplaceAll(bulk.Rows.Take(2));
        bulk.Rows.Add(first);
        check(
            bulk.Rows.Count == 3 && bulk.Rows[0] == first && changes is
            [
                NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset,
                NotifyCollectionChangedAction.Add
            ], "Replacing from the same collection is safe and subsequent appends send an ordinary Add");

        var previousOptions = new SearchOptions("old", Types: [RegistryValueType.DWord, (RegistryValueType)99]);
        var reopened = new FindOptionsViewModel(origin, "needle", previousOptions);
        check(
            reopened.Types == "4, 99" && reopened.CreateSearch().Options.Types!.SequenceEqual(previousOptions.Types!),
            "Advanced Find reopens known and unknown type filters as parseable numeric codes");
        var form = new FindOptionsViewModel(
            origin, " ", new SearchOptions("old", false, Types: [RegistryValueType.DWord]))
        {
            ScopeIndex = 1, Types = "REG_BINARY"
        };
        var exact = form.CreateSearch();
        check(
            exact.Options is { Text: " ", Keys: false, Types: [RegistryValueType.Binary] } && exact.Roots.Length > 1 &&
            exact.Roots.All(root => root.Machine == origin.Machine && root.View == RegistryView.Registry32),
            "Advanced Find preserves literal whitespace and applies the selected types across the original machine and registry view");

        var requested = new List<SavedSearch>();
        var notices = new List<NoticeKind>();
        var searchInput = new BrowserSearchViewModel(() => origin) { Query = "needle" };
        searchInput.ResultsRequested += requested.Add;
        searchInput.Notice += (_, _, kind) => notices.Add(kind);
        searchInput.QuickSearchCommand.Execute(null);
        check(
            requested is [{ Options.Text: "needle" }] && requested[0].Roots.SequenceEqual([origin]),
            "Quick search creates a result set in the current branch and registry view");
        searchInput.FindRequested += options =>
        {
            options.Query = "advanced";
            options.Types = "REG_DWORD";
            return Task.FromResult<SavedSearch?>(options.CreateSearch());
        };
        await searchInput.FindCommand.ExecuteAsync(null);
        check(
            requested.Count == 2 && requested[1].Options is { Text: "advanced", Types: [RegistryValueType.DWord] } &&
            notices.Count == 0, "Advanced Find always creates a result set with the chosen options");
        searchInput.Close();
        searchInput.QuickSearchCommand.Execute(null);
        await searchInput.FindCommand.ExecuteAsync(null);
        check(requested.Count == 2, "Closing a browser prevents both search entry points from starting another search");
    }
}
