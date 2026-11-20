// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class SearchLifecycleTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\SearchLifecycleTests");
        var query = new SavedSearch("Test", [root], new SearchOptions("needle"));
        var settings = new AppSettings();
        var store = new InvestigationStore(settings, () => { });
        var firstMatch = new RegistrySearchMatch(
            root.Child("First"), string.Empty, new RegistryValue(string.Empty, RegistryValueType.Binary, [1]), "Data", "needle");
        var secondMatch = firstMatch with { Location = root.Child("Second"), ValueName = "Named" };
        {
            var batches = Enumerable.Range(0, 16).Select(batch => Enumerable.Range(batch * 64, 64).Select(index => new RegistrySearchMatch(
                root, "Value" + index, new RegistryValue((index % 2 == 0 ? "Even" : "Odd") + index, RegistryValueType.String, []), "Name",
                "match")).ToArray()).ToArray();
            var streamed = new SearchResultsViewModel(
                query, store, (_, _) => throw new InvalidOperationException(), (_, _) => { }, findProgressively: (_, publish, _) =>
                {
                    foreach (var batch in batches)
                    {
                        publish(batch);
                    }

                    return Task.FromResult(new RegistrySearchResults(batches.SelectMany(batch => batch).ToArray(), 0, false));
                });
            streamed.Filter = "Even";
            var resets = 0;
            var filteredResets = 0;
            var replacements = 0;
            var restorations = 0;
            IReadOnlyList<InvestigationRow>? selection = null;
            var notifications = 0;
            var filteredNotifications = 0;
            var titleChanges = 0;
            var emptyChanges = 0;
            var countChanges = 0;
            var titlesCurrent = true;
            streamed.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(streamed.IsEmpty))
                {
                    emptyChanges++;
                }

                if (args.PropertyName == nameof(streamed.CountText))
                {
                    countChanges++;
                }

                if (args.PropertyName == nameof(streamed.HeaderTitle))
                {
                    titleChanges++;
                    titlesCurrent &= streamed.DisplayRows.Count * 2 == streamed.Rows.Count;
                }
            };
            streamed.Rows.CollectionChanged += (_, args) =>
            {
                notifications++;
                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                {
                    resets++;
                }
            };
            streamed.DisplayRows.CollectionChanged += (_, args) =>
            {
                filteredNotifications++;
                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                {
                    filteredResets++;
                }
            };
            streamed.RowsReplacing += () => replacements++;
            streamed.SelectionRestored += _ =>
            {
                restorations++;
                streamed.Selected = selection = [streamed.DisplayRows[0]];
            };
            await streamed.RunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            check(
                streamed.Rows.Count == 1024 && streamed.DisplayRows.Count == 512,
                "Large progressive batches retain every result and apply the live filter");
            check(
                resets == 1
                && filteredResets == 1
                && replacements == 1
                && restorations == 1
                && notifications == 1 + (15 * 64)
                && filteredNotifications == 1 + (15 * 32),
                "Only the first progressive batch resets lists and restores selection; appends notify item additions");
            check(titleChanges == batches.Length && titlesCurrent, "Each progressive batch reports its final filtered header count once");
            check(
                emptyChanges == batches.Length + 1 && countChanges == batches.Length + 2,
                "Appended batches update only counts and empty state without replaying selection");
            check(
                ReferenceEquals(streamed.Selected, selection) && ReferenceEquals(streamed.Selected.Single(), streamed.DisplayRows[0]),
                "Incremental search appends keep the selection without replaying it");
        }
        {
            var scans = 0;
            var textMatch = firstMatch with
            {
                Value = new RegistryValue(
                    "TextValue", RegistryValueType.String, System.Text.Encoding.Unicode.GetBytes("Displayed data\0"))
            };
            var filtered = new SearchResultsViewModel(
                query, store, (_, _) =>
                {
                    scans++;
                    return Task.FromResult(new RegistrySearchResults([firstMatch, textMatch, secondMatch], 0, false));
                }, (_, _) => { });
            await filtered.RunCommand.ExecuteAsync(null);
            var selected = filtered.Rows[1];
            filtered.Selected = [selected];
            foreach (var text in new[] { " textvalue ", "DISPLAYED DATA" })
            {
                filtered.Filter = text;
                check(
                    filtered.DisplayRows.SequenceEqual([selected]) && filtered.Selected.SequenceEqual([selected]),
                    "Result filter matches names and displayed data without losing visible selection");
            }

            filtered.Filter = "second";
            check(
                filtered.DisplayRows.Count == 1 && filtered.DisplayRows[0].Path.EndsWith("Second") && filtered.Selected.Count == 0,
                "Result filter matches paths and clears hidden selection");
            filtered.Filter = "absent";
            check(
                filtered.IsEmpty && filtered.DisplayEmptyTitle == "No results match the filter" && filtered.Rows.Count == 3,
                "Empty filter view retains the complete search result set");
            filtered.Filter = string.Empty;
            check(
                filtered.DisplayRows.Count == 3 && scans == 1 && !filtered.IsEmpty,
                "Clearing a result filter restores every result without rerunning the registry scan");
        }
        foreach (var finish in new[] { "complete", "cancel", "close" })
        {
            var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new SearchResultsViewModel(
                query, store, (_, _) => throw new InvalidOperationException("Streaming required"), (_, _) => { },
                findProgressively: (_, publish, token) => Task.Run(
                    async () =>
                    {
                        publish([firstMatch]);
                        await continueScan.Task.WaitAsync(token);
                        publish([secondMatch]);
                        await finishScan.Task.WaitAsync(token);
                        return new RegistrySearchResults([firstMatch, secondMatch], 0, false);
                    }, token));
            view.Rows.CollectionChanged += (_, _) =>
            {
                if (view.Rows.Count == 1)
                {
                    firstBatch.TrySetResult();
                }

                if (view.Rows.Count == 2)
                {
                    secondBatch.TrySetResult();
                }
            };
            var scan = view.RunCommand.ExecuteAsync(null);
            await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var selected = view.Rows[0];
            view.Selected = [selected];
            view.Filter = "first";
            check(view.IsBusy && !scan.IsCompleted, "First matches appear while the search is still running");
            continueScan.SetResult();
            await secondBatch.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(
                view.Selected.SequenceEqual([selected]) && ReferenceEquals(view.Rows[0], selected),
                "Progressive append preserves selected row identity and existing results");
            check(
                view.DisplayRows.SequenceEqual([selected]) && view.Rows.Count == 2,
                "Progressive results respect the current filter while retaining hidden matches");
            view.Filter = "second";
            check(
                view.DisplayRows.Count == 1 && view.DisplayRows[0].Path.EndsWith("Second") && view.Selected.Count == 0,
                "Changing the filter during a scan reveals previously hidden matches");
            if (finish == "cancel")
            {
                view.RunCancelCommand.Execute(null);
            }
            else if (finish == "close")
            {
                view.Close();
            }
            else
            {
                finishScan.SetResult();
            }

            await scan.WaitAsync(TimeSpan.FromSeconds(3));
            check(!view.IsBusy && view.Rows.Count == 2, "Streaming " + finish + " retains displayed results and stops progress");
        }

        {
            var synchronous = new SearchResultsViewModel(
                query, store, (_, _) => throw new InvalidOperationException("Streaming required"), (_, _) => { },
                findProgressively: (_, publish, _) =>
                {
                    for (var batch = 0; batch < 5; batch++)
                    {
                        publish([firstMatch]);
                    }

                    return Task.FromResult(new RegistrySearchResults(Enumerable.Repeat(firstMatch, 5).ToArray(), 0, false));
                });
            await synchronous.RunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
            check(synchronous.Rows.Count == 5, "Synchronous producers cannot block the UI while bounded batches wait for consumption");
        }
        foreach (var late in new[] { "success", "canceled", "error" })
        {
            var gates = new[] { Gate(), Gate() };
            var tokens = new List<CancellationToken>();
            var notices = new List<string>();
            var view = new SearchResultsViewModel(
                query, store, (_, token) =>
                {
                    tokens.Add(token);
                    return gates[tokens.Count - 1].Task;
                }, (_, _) => { });
            view.Notice += (title, _, _) => notices.Add(title);
            var older = view.RunCommand.ExecuteAsync(null);
            check(!view.RunCommand.CanExecute(null), "A running search disables its UI Run command");
            var newer = view.RunCommand.ExecuteAsync(null);
            check(
                tokens.Count == 2 && tokens[0].IsCancellationRequested && !tokens[1].IsCancellationRequested,
                "Direct Toolkit ExecuteAsync overlaps despite CanExecute and cancels the previous token");
            gates[1].SetResult(new RegistrySearchResults([secondMatch], 0, false));
            await newer;
            var description = view.Description;
            var installed = view.Rows.Single();
            if (late == "success")
            {
                gates[0].SetResult(new RegistrySearchResults([firstMatch], 0, false));
            }
            else if (late == "canceled")
            {
                gates[0].SetCanceled(tokens[0]);
            }
            else
            {
                gates[0].SetException(new IOException("late failure"));
            }

            await older;
            check(
                ReferenceEquals(view.Rows.Single(), installed) && view.Description == description && notices.Count == 0,
                "Superseded search cannot install late " + late + " rows or status");
        }

        foreach (var fail in new[] { false, true })
        {
            var gate = Gate();
            var view = new SearchResultsViewModel(query, store, (_, _) => gate.Task, (_, _) => { });
            var notices = 0;
            view.Notice += (_, _, _) => notices++;
            var pending = view.RunCommand.ExecuteAsync(null);
            view.Close();
            var description = view.Description;
            if (fail)
            {
                gate.SetException(new IOException("after close"));
            }
            else
            {
                gate.SetResult(new RegistrySearchResults([firstMatch], 0, false));
            }

            await pending;
            check(
                view.Rows.Count == 0 && view.Description == description && notices == 0,
                "Closing a results tab suppresses late " + (fail ? "failure" : "success and cancellation") + " presentation");
        }

        {
            var gate = Gate();
            var view = new SearchResultsViewModel(query, store, (_, _) => gate.Task, (_, _) => { });
            var retained = new InvestigationRow(firstMatch);
            view.Rows.Add(retained);
            var notices = 0;
            view.Notice += (_, _, _) => notices++;
            var pending = view.RunCommand.ExecuteAsync(null);
            view.RunCancelCommand.Execute(null);
            gate.SetException(new IOException("provider failed after cancellation"));
            await pending;
            check(
                view.Description.StartsWith("Search canceled") && ReferenceEquals(view.Rows.Single(), retained) && notices == 0,
                "Cancel preserves prior rows and suppresses a provider's later error");
        }
        {
            var firstOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var notices = 0;
            var view = new SearchResultsViewModel(
                query, store, (_, _) => Task.FromResult(new RegistrySearchResults([], 0, false)), (_, _) => { },
                (_, _) => ++calls == 1 ? firstOpen.Task : lastOpen.Task);
            view.Notice += (_, _, _) => notices++;
            view.Selected = [new InvestigationRow(firstMatch)];
            var older = view.OpenCommand.ExecuteAsync(null);
            view.Selected = [new InvestigationRow(secondMatch)];
            check(view.OpenCommand.CanExecute(null), "Another result stays actionable while the earlier destination is loading");
            var newer = view.OpenCommand.ExecuteAsync(null);
            lastOpen.SetResult();
            await newer;
            firstOpen.SetException(new IOException("superseded destination error"));
            await older;
            check(calls == 2 && notices == 0, "A newer result can open concurrently and suppresses the earlier navigation's late error");
        }
        var opened = new List<(RegistrySearchMatch Match, bool Select)>();
        var navigated = new List<RegistrySearchMatch>();
        var commands = new SearchResultsViewModel(
            query, store, (_, _) => Task.FromResult(new RegistrySearchResults([], 0, false)),
            (match, select) => opened.Add((match, select)), (match, _) =>
            {
                navigated.Add(match);
                return Task.CompletedTask;
            });
        commands.Selected = [new InvestigationRow(firstMatch)];
        await commands.OpenCommand.ExecuteAsync(null);
        commands.OpenNewTabCommand.Execute(null);
        commands.OpenInBackgroundCommand.Execute(commands.Selected[0]);
        check(
            navigated.SequenceEqual([firstMatch])
            && opened is [{ Select: true }, { Select: false }]
            && opened.All(item => ReferenceEquals(item.Match, firstMatch) && item.Match.ValueName == string.Empty),
            "Ordinary, explicit foreground and background commands retain exact unnamed-value targets");
        commands.Close();
        await commands.OpenCommand.ExecuteAsync(null);
        commands.OpenNewTabCommand.Execute(null);
        commands.OpenInBackgroundCommand.Execute(commands.Selected[0]);
        check(navigated.Count == 1 && opened.Count == 2, "Closed results cannot navigate through any command route");
        if (registryTests)
        {
            await RunRegistryAsync(check);
        }
    }

    private static TaskCompletionSource<RegistrySearchResults> Gate()
    {
        return new TaskCompletionSource<RegistrySearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task RunRegistryAsync(Action<bool, string> check)
    {
        var registry = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.SearchNavigationTests." + Guid.NewGuid().ToString("N"));
        registry.CreateKey(root.Parent!, root.Name);
        foreach (var name in new[] { "First", "Second", "Repurposed" })
        {
            registry.CreateKey(root, name);
        }

        registry.Write(root.Child("First"), new RegistryValue(string.Empty, RegistryValueType.Binary, [1]), null);
        registry.Write(root.Child("Second"), new RegistryValue("Named", RegistryValueType.Binary, [2]), null);
        var settings = new AppSettings();
        var shell = new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry, target => new BrowserViewModel(registry, target),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings));
        try
        {
            shell.StartSearch(
                new SavedSearch("Native navigation", [root], new SearchOptions(Guid.NewGuid().ToString("N"), Values: false, Data: false)));
            var source = (SearchResultsViewModel)shell.ActiveBottomTab!;
            await source.RunCommand.ExecutionTask!;
            var first = new RegistrySearchMatch(
                root.Child("First") with { View = RegistryView.Registry32 }, string.Empty,
                new RegistryValue(string.Empty, RegistryValueType.Binary, [1]), "Data", "one");
            var second = new RegistrySearchMatch(
                root.Child("Second"), "Named", new RegistryValue("Named", RegistryValueType.Binary, [2]), "Data", "two");
            source.Rows.ReplaceAll([new InvestigationRow(first), new InvestigationRow(second)]);
            source.Selected = [source.Rows[0]];
            await source.OpenCommand.ExecuteAsync(null);
            var destination = (BrowserTab)shell.ActiveTab!;
            var count = shell.Tabs.Count;
            var selected = new List<string>();
            destination.Browser.ValueSelectionRequested += selected.Add;
            shell.SelectTab(source);
            source.Selected = [source.Rows[1]];
            await source.OpenCommand.ExecuteAsync(null);
            check(
                ReferenceEquals(shell.ActiveTab, destination)
                && shell.Tabs.Count == count
                && destination.Browser.Location == second.Location
                && selected is ["Named"], "Ordinary result activation reuses its owned browser and selects the exact named value and view");
            shell.SelectTab(source);
            source.Selected = [source.Rows[0]];
            await source.OpenCommand.ExecuteAsync(null);
            check(
                ReferenceEquals(shell.ActiveTab, destination)
                && destination.Browser.Location == first.Location
                && selected[^1] == string.Empty, "Reused result destination selects the unnamed value in the original 32-bit view");
            await destination.Browser.NavigateAsync(root.Child("Repurposed"));
            await destination.Browser.NavigateAsync(first.Location);
            shell.SelectTab(source);
            await source.OpenCommand.ExecuteAsync(null);
            var replacement = (BrowserTab)shell.ActiveTab!;
            check(
                !ReferenceEquals(replacement, destination) && shell.Tabs.Count == count + 1,
                "Navigating away and back releases a search destination even when its address matches again");
            await replacement.Browser.NavigateAsync(replacement.Browser.Location, false);
            shell.SelectTab(source);
            source.Selected = [source.Rows[1]];
            await source.OpenCommand.ExecuteAsync(null);
            check(
                ReferenceEquals(shell.ActiveTab, replacement),
                "Refreshing the same result keeps its browser destination owned by the search");
            shell.SelectTab(source);
            source.OpenNewTabCommand.Execute(null);
            var foreground = (BrowserTab)shell.ActiveTab!;
            check(
                !ReferenceEquals(foreground, replacement)
                && foreground.InitialValue == "Named"
                && foreground.Browser.Location == second.Location,
                "Explicit foreground new-tab opening does not replace the search's ordinary destination");
            shell.SelectTab(source);
            source.OpenInBackgroundCommand.Execute(source.Rows[0]);
            check(
                ReferenceEquals(shell.ActiveBottomTab, source)
                && shell.Tabs[^1] is BrowserTab { InitialValue: "" } background
                && background.Browser.Location == first.Location, "Middle-open retains source selection, unnamed value and registry view");
            await source.OpenCommand.ExecuteAsync(null);
            check(
                ReferenceEquals(shell.ActiveTab, replacement),
                "Foreground and background explicit opens leave ordinary destination reuse intact");
            shell.SelectTab(source);
            shell.CloseTab(replacement);
            await source.OpenCommand.ExecuteAsync(null);
            check(
                shell.ActiveTab is BrowserTab last && !ReferenceEquals(last, replacement),
                "Closing a destination causes a later result to open a new browser");

            var nextDestination = shell.OpenBrowser(root.Child("Repurposed"));
            source.Selected = [source.Rows[0]];
            await shell.FindNextResultCommand.ExecuteAsync(null);
            var afterNext = shell.Tabs.Count;
            source.Selected = [source.Rows[0]];
            await source.OpenCommand.ExecuteAsync(null);
            check(
                shell.ActiveTab == nextDestination && shell.Tabs.Count == afterNext && nextDestination.Browser.Location == first.Location,
                "Ordinary result activation reuses the current browser destination established by F3");

            shell.StartSearch(
                new SavedSearch(
                    "Real cancel", [new RegistryLocation(RegistryHive.LocalMachine), new RegistryLocation(RegistryHive.Users)],
                    new SearchOptions(Guid.NewGuid().ToString("N"))));
            var scan = (SearchResultsViewModel)shell.ActiveBottomTab!;
            await Task.Delay(75);
            check(scan.RunCommand.IsRunning, "Local HKLM/HKU no-match scan is still active before native cancellation");
            scan.RunCancelCommand.Execute(null);
            await scan.RunCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
            check(
                scan.Description.StartsWith("Search canceled") && scan.Rows.Count == 0 && !scan.RunCancelCommand.CanExecute(null),
                "The results Cancel command stops a real local registry scan and leaves stable canceled state");
        }
        finally
        {
            shell.WindowClosed();
            await Task.WhenAll(
                shell.Tabs.Concat(shell.BottomTabs).OfType<SearchResultsViewModel>()
                    .Select(tab => tab.RunCommand.ExecutionTask ?? Task.CompletedTask));
            using var parent = registry.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
        }
    }
}
