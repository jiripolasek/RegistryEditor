// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed partial class MainWindowViewModel : ObservableObject
{
    internal event Action<WatchSession>? WatchOpened;

    internal event Action<WatchDefinition>? WatchProposed;

    internal event Action<WatchSession>? WatchFailed;

    public event Action? CloseRequested;

    public event Action<WindowTab>? TabClosed;

    public event EventHandler? ToolsChanged;

    internal event Action<bool>? SettingsSectionRequested;

    private readonly Func<RegistryLocation, BrowserViewModel> _createBrowser;
    private readonly RegistryHistoryBroadcaster? _historyBroadcaster;
    private readonly InvestigationStore _investigations;
    private readonly JournalViewModel? _journalHistory;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly List<BrowserTab> _recentBrowsers = [];
    private readonly RegistryService _registry;

    private readonly HashSet<WatchSession> _reportedWatchFailures = [];

    private readonly Dictionary<SearchResultsViewModel, (BrowserTab Tab, int Navigation)> _searchDestinations = [];
    private readonly SearchHistoryStore? _searchHistory;
    private readonly AppSettings _settings;
    private BrowserTab? _contextTab;

    private bool _replaceRestored;

    internal Func<Action, Task> DispatchWatchUpdate { get; set; } = action =>
    {
        action();
        return Task.CompletedTask;
    };

    public int RunningWatchCount => this.Watches.Count(watch => watch.IsRunning);

    public string RunningWatchText =>
        $"{this.RunningWatchCount:N0} {(this.RunningWatchCount == 1 ? "watch" : "watches")} running";

    public bool HasRunningWatches => this.RunningWatchCount > 0;

    public ObservableCollection<WindowTab> Tabs { get; } = [];

    public ObservableCollection<WatchSession> Watches { get; } = [];

    internal NotesStore NoteStore { get; }

    public ReadOnlyObservableCollection<NoteDraft> Notes => this.NoteStore.Notes;

    internal IReadOnlyList<string> RecentSearches => this._searchHistory?.Queries ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleBrowser))]
    public partial WindowTab? ActiveTab { get; set; }

    public BrowserViewModel? VisibleBrowser => (this.ActiveTab as BrowserTab)?.Browser;

    public BrowserViewModel? ContextBrowser => this._contextTab?.Browser;

    public RegistryLocation? ContextLocation => this.ContextBrowser?.Location;

    [ObservableProperty]
    public partial bool IsPaneOpen { get; set; }

    [ObservableProperty]
    public partial ToolKind SelectedTool { get; set; }

    [ObservableProperty]
    public partial bool IsPanePinned { get; set; }

    public double PaneWidth { get; private set; }

    internal WindowPlacement? Placement { get; set; }

    internal Func<WindowPlacement>? CapturePlacement { get; set; }

    public bool IsClosing { get; private set; }

    public bool CanCloseWindow { get; private set; }

    internal bool IsDialogOpen { get; private set; }

    private bool CanDisconnect => this.ContextLocation?.Machine is not null;

    internal MainWindowViewModel(
        AppSettings settings,
        InvestigationStore investigations,
        RegistryService registry,
        Func<RegistryLocation, BrowserViewModel> createBrowser,
        ILogger<MainWindowViewModel> logger,
        NotesStore notes,
        RegistryHistoryBroadcaster? historyBroadcaster = null,
        JournalViewModel? journalHistory = null,
        SearchHistoryStore? searchHistory = null)
    {
        this._logger = logger;
        this._settings = settings;
        this._searchHistory = searchHistory;
        this._investigations = investigations;
        this._registry = registry;
        this._createBrowser = createBrowser;
        this._historyBroadcaster = historyBroadcaster;
        this._journalHistory = journalHistory;
        if (historyBroadcaster is not null)
        {
            historyBroadcaster.Changed += this.RegistryHistoryChangedAsync;
        }

        this.NoteStore = notes;
        notes.Changed += this.NotesChanged;
        notes.SaveFailed += this.NoteSaveFailed;
        this.SelectedTool = Enum.TryParse<ToolKind>(settings.SelectedTool, out var tool) && Enum.IsDefined(tool)
            ? tool
            : ToolKind.Favorites;
        this.IsPaneOpen = this.IsPanePinned = settings.ToolPanePinned;
        this.PaneWidth = settings.ToolPaneWidth;
        this.Watches.CollectionChanged += (_, _) => this.WatchesChanged();
    }

    internal void ResizePane(double delta)
    {
        this.PaneWidth = Math.Clamp(this.PaneWidth + delta, 300, 680);
    }

    internal bool TryBeginDialog(bool closeConfirmation = false)
    {
        if (this.IsDialogOpen || this.CanCloseWindow || !closeConfirmation && this.IsClosing)
        {
            return false;
        }

        this.IsDialogOpen = true;
        this.OnPropertyChanged(nameof(this.IsDialogOpen));
        return true;
    }

    internal void EndDialog()
    {
        this.IsDialogOpen = false;
        this.OnPropertyChanged(nameof(this.IsDialogOpen));
    }

    internal void Initialize(RegistryLocation? initialLocation)
    {
        this._replaceRestored = initialLocation is null;
        if (initialLocation is not null)
        {
            this.OpenBrowser(initialLocation);
            return;
        }

        if (this._settings.RestoredLayout is { } layout)
        {
            this.Placement = layout.Placement;
            this.IsPaneOpen = layout.PaneOpen;
            this.IsPanePinned = layout.PanePinned;
            this.PaneWidth = layout.PaneWidth;
            this.SelectedTool = Enum.Parse<ToolKind>(layout.SelectedTool);
            this.BottomPaneHeight = layout.BottomPaneHeight;
        }

        foreach (var tab in this._settings.Tabs)
        {
            try
            {
                this.OpenBrowser(RegistryLocation.Parse(tab.Address, tab.Is32Bit ? RegistryView.Registry32 : RegistryView.Registry64));
                ((BrowserTab)this.Tabs[^1]).Browser.RestoreLayout(tab.Layout);
            }
            catch (ArgumentException)
            {
                // Skip an invalid restored address and keep the rest of the session.
            }
        }

        if (this.Tabs.Count == 0)
        {
            this.OpenBrowser(RegistryLocation.Parse("HKCU"));
        }

        if (this._settings.RestoredLayout is { BottomPaneOpen: true, BottomTool: "Journal" or "Watches" } bottom)
        {
            this.OpenBottomTool(Enum.Parse<ToolKind>(bottom.BottomTool));
        }
    }

    partial void OnActiveTabChanged(WindowTab? value)
    {
        if (value is SearchResultsViewModel search)
        {
            this._activeSearch = search;
        }

        if (value is BrowserTab browser)
        {
            this._recentBrowsers.Remove(browser);
            this._recentBrowsers.Add(browser);
            this.SetContextTab(browser);
        }
    }

    private void SetContextTab(BrowserTab? tab)
    {
        if (this._contextTab == tab)
        {
            return;
        }

        if (this.ContextBrowser is { } previous)
        {
            previous.Navigated -= this.ContextNavigated;
        }

        this._contextTab = tab;
        if (this.ContextBrowser is { } current)
        {
            current.Navigated += this.ContextNavigated;
        }

        this.OnPropertyChanged(nameof(this.ContextBrowser));
        this.ContextNavigated(this, EventArgs.Empty);
    }

    private void ContextNavigated(object? sender, EventArgs args)
    {
        this.OnPropertyChanged(nameof(this.ContextLocation));
        this.DisconnectCommand.NotifyCanExecuteChanged();
    }

    internal BrowserTab OpenBrowser(RegistryLocation location, string? selectedValue = null, bool select = true)
    {
        var tab = new BrowserTab(this._createBrowser(location), selectedValue);
        this.AttachBrowser(tab, select);
        return tab;
    }

    private void AttachBrowser(BrowserTab tab, bool select)
    {
        tab.Browser.Search.ResultsRequested += this.StartSearch;
        tab.Browser.WatchRequested += this.ProposeWatch;
        this.Tabs.Add(tab);
        if (this._contextTab is null)
        {
            this.SetContextTab(tab);
        }

        if (select || this.ActiveTab is null)
        {
            this.ActiveTab = tab;
        }
    }

    internal void OpenInBackground(RegistryLocation location)
    {
        this.OpenBrowser(location, select: false);
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (this.ContextBrowser is { Location.Machine: not null } browser)
        {
            await browser.NavigateAsync(new RegistryLocation(RegistryHive.CurrentUser, View: browser.Location.View));
        }
    }

    [RelayCommand]
    private void NewTab()
    {
        this.OpenBrowser(this.ContextLocation ?? new RegistryLocation());
    }

    [RelayCommand]
    private void NextTab()
    {
        this.CycleTab(1);
    }

    [RelayCommand]
    private void PreviousTab()
    {
        this.CycleTab(-1);
    }

    private void CycleTab(int offset)
    {
        if (this.Tabs.Count > 0)
        {
            this.ActiveTab = this.Tabs[(this.Tabs.IndexOf(this.ActiveTab!) + offset + this.Tabs.Count) % this.Tabs.Count];
        }
    }

    [RelayCommand]
    private void CloseCurrentTab()
    {
        if (this.ActiveTab is { } tab)
        {
            this.CloseTab(tab);
        }
    }

    internal void DuplicateTab(BrowserTab tab)
    {
        var index = this.Tabs.IndexOf(tab);
        if (this.IsClosing || index < 0)
        {
            return;
        }

        var duplicate = this.OpenBrowser(tab.Browser.Location);
        this.Tabs.Move(this.Tabs.IndexOf(duplicate), index + 1);
    }

    internal void CloseOtherTabs(WindowTab tab)
    {
        if (this.IsClosing || !this.Tabs.Contains(tab))
        {
            return;
        }

        foreach (var other in this.Tabs.Where(other => other != tab).ToArray())
        {
            this.CloseTab(other);
        }
    }

    internal void CloseTabsToRight(WindowTab tab)
    {
        var index = this.Tabs.IndexOf(tab);
        if (this.IsClosing || index < 0)
        {
            return;
        }

        foreach (var other in this.Tabs.Skip(index + 1).ToArray())
        {
            this.CloseTab(other);
        }
    }

    internal void CloseTab(WindowTab tab)
    {
        if (this.BottomTabs.Contains(tab))
        {
            this.CloseBottomTab(tab);
            return;
        }

        var index = this.Tabs.IndexOf(tab);
        if (this.IsClosing || index < 0)
        {
            return;
        }

        if (this.Tabs.Count == 1)
        {
            if (this.BottomTabs.Count == 0)
            {
                this.ExitCommand.Execute(null);
                return;
            }

            this.OpenBrowser(new RegistryLocation());
        }

        if (this.ActiveTab == tab)
        {
            this.ActiveTab = this.Tabs[index + 1 < this.Tabs.Count ? index + 1 : index - 1];
        }

        if (tab is BrowserTab closedBrowser)
        {
            this.RememberClosedTab(closedBrowser, index);
        }

        this.Tabs.Remove(tab);
        if (tab is BrowserTab browser)
        {
            this._recentBrowsers.Remove(browser);
            if (this._contextTab == tab)
            {
                this.SetContextTab(this._recentBrowsers.LastOrDefault() ?? this.Tabs.OfType<BrowserTab>().FirstOrDefault());
            }
        }

        this.CancelTab(tab);
    }

    private void CancelTab(WindowTab tab)
    {
        if (tab is SearchResultsViewModel results)
        {
            this._searchDestinations.Remove(results);
            if (this._activeSearch == results)
            {
                this._activeSearch = this.ActiveTab as SearchResultsViewModel
                                     ?? this.ActiveBottomTab as SearchResultsViewModel
                                     ?? this.BottomTabs.Concat(this.Tabs).OfType<SearchResultsViewModel>().LastOrDefault();
            }
        }

        if (tab is BrowserTab browser)
        {
            browser.Browser.Search.ResultsRequested -= this.StartSearch;
            browser.Browser.WatchRequested -= this.ProposeWatch;
            browser.Browser.CancelPending();
            browser.Close();
        }
        else if (tab is InvestigationViewModel investigation)
        {
            investigation.Close();
        }
        else if (tab is RegistryUsageTab usage)
        {
            usage.ScanCommand.Cancel();
        }

        this.TabClosed?.Invoke(tab);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        this.ShowSettings(false);
    }

    [RelayCommand]
    private void OpenShortcuts()
    {
        this.ShowSettings(true);
    }

    private void ShowSettings(bool shortcuts)
    {
        this.SelectTab(this.Tabs.OfType<SettingsTab>().FirstOrDefault() ?? new SettingsTab());
        this.CloseUnpinnedPane();
        this.SettingsSectionRequested?.Invoke(shortcuts);
    }

    [RelayCommand]
    private void OpenTool(ToolKind tool)
    {
        if (tool is ToolKind.Journal or ToolKind.Watches)
        {
            this.OpenBottomTool(tool);
        }
        else
        {
            this.OpenToolTab(tool);
        }
    }

    internal void OpenRegistryUsage(RegistryLocation? location)
    {
        if (this.IsClosing || location is not { IsComputer: false })
        {
            return;
        }

        var tab = new RegistryUsageTab(this._registry, location);
        this.SelectTab(tab);
        tab.ScanCommand.Execute(location);
    }

    internal void OpenToolTab(ToolKind tool, bool fromPanel = false)
    {
        this.SelectTab(this.Tabs.OfType<ToolTab>().FirstOrDefault(tab => tab.Tool == tool) ?? new ToolTab(tool));
        if (fromPanel || !this.IsPanePinned || this.SelectedTool == tool)
        {
            this.IsPaneOpen = false;
        }
    }

    internal void SelectTab(WindowTab tab)
    {
        if (this.BottomTabs.Contains(tab))
        {
            this.SelectBottomTab(tab);
            return;
        }

        if (!this.Tabs.Contains(tab))
        {
            this.Tabs.Add(tab);
        }

        this.ActiveTab = tab;
    }

    internal void ShowToolInPanel(ToolKind tool, RegistryLocation? note)
    {
        this.SelectedTool = tool;
        this.IsPaneOpen = true;
        if (this._contextTab is { } tab && (note is null || new KeyNote(note, string.Empty).Matches(tab.Browser.Location)))
        {
            this.ActiveTab = tab;
        }
        else
        {
            this.OpenBrowser(note ?? new RegistryLocation());
        }
    }

    internal async Task NavigateFromToolsAsync(RegistryLocation location, bool newTab = false)
    {
        if (!newTab && this.VisibleBrowser is { } browser)
        {
            await browser.NavigateAsync(location);
            if (browser.Error is { } error)
            {
                throw new IOException(error);
            }
        }
        else
        {
            this.OpenBrowser(location);
        }

        this.CloseUnpinnedPane();
    }

    private void CloseUnpinnedPane()
    {
        if (!this.IsPanePinned)
        {
            this.IsPaneOpen = false;
        }
    }

    internal void SaveLayout(string? filePath = null)
    {
        this.Placement = this.CapturePlacement?.Invoke() ?? this.Placement;
        var tabs = this.Tabs.OfType<BrowserTab>().Select(tab => new SavedTab(
            tab.Browser.Location.Address, tab.Browser.Location.View == RegistryView.Registry32, tab.Browser.CaptureLayout())).ToList();
        this._settings.SaveSession(
            tabs, this._replaceRestored, filePath,
            new WindowLayout(
                this.Placement, this.IsPaneOpen, this.IsPanePinned, this.PaneWidth, this.SelectedTool.ToString(), this.IsBottomPaneOpen,
                this.BottomPaneHeight, (this.ActiveBottomTab as ToolTab)?.Tool.ToString()));
    }

    internal void StartSearch(SavedSearch search)
    {
        var tab = this.CreateSearch(search);
        this.SelectBottomTab(tab);
        if (this._contextTab is { } browser)
        {
            this._searchDestinations[tab] = (browser, browser.Browser.NavigationVersion);
        }

        _ = tab.RunCommand.ExecuteAsync(null);
        _ = this._searchHistory?.RecordAsync(search.Options.Text);
    }

    private SearchResultsViewModel CreateSearch(SavedSearch search)
    {
        SearchResultsViewModel? tab = null;
        tab = new SearchResultsViewModel(
            search, this._investigations, (query, token) => Task.Run(() => RegistrySearch.FindAll(this._registry, query, token), token),
            (match, select) => this.OpenBrowser(match.Location, match.ValueName, select),
            (match, token) => this.NavigateSearchResultAsync(tab!, match, token),
            (query, progress, token) => Task.FromResult(RegistrySearch.FindAll(this._registry, query, token, progress: progress)));
        return tab;
    }

    private async Task NavigateSearchResultAsync(SearchResultsViewModel source, RegistrySearchMatch match, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!this.Tabs.Contains(source) && !this.BottomTabs.Contains(source))
        {
            return;
        }

        if (!this._searchDestinations.TryGetValue(source, out var destination)
            || !this.Tabs.Contains(destination.Tab)
            || destination.Tab.Browser.NavigationVersion != destination.Navigation)
        {
            var opened = this.OpenBrowser(match.Location, match.ValueName);
            this._searchDestinations[source] = (opened, opened.Browser.NavigationVersion);
            return;
        }

        this.SelectTab(destination.Tab);
        var browser = destination.Tab.Browser;
        var navigation = browser.NavigateToMatchAsync(new SearchHit(match.Location, match.ValueName), token);
        var version = browser.NavigationVersion;
        this._searchDestinations[source] = (destination.Tab, version);
        await navigation;
        if (browser.NavigationVersion == version && browser.Error is { } error)
        {
            throw new IOException(error);
        }
    }

    [RelayCommand]
    private void OpenSavedSearches()
    {
        this.SelectTab(
            this.Tabs.OfType<SavedSearchesViewModel>().FirstOrDefault()
            ?? new SavedSearchesViewModel(this._investigations, this.StartSearch));
    }

    [RelayCommand]
    private void OpenWorkspaces()
    {
        this.SelectTab(
            this.Tabs.OfType<WorkspacesViewModel>().FirstOrDefault()
            ?? new WorkspacesViewModel(this._investigations, this.CaptureWorkspace, this.RestoreWorkspaceAsync));
    }

    internal async Task<WorkspaceRestoreResult> RestoreWorkspaceAsync(
        RegistryWorkspace workspace,
        WorkspaceRestoreOptions options,
        CancellationToken token)
    {
        workspace.Validate();
        var originalTabs = this.Tabs.Concat(this.BottomTabs).Where(tab => tab is BrowserTab or SearchResultsViewModel).ToArray();
        var source = this.ActiveTab;
        var stagedBrowsers = new List<BrowserTab>();
        var stagedSearches = new List<SearchResultsViewModel>();
        var preparedWatches = new List<WatchSession>();
        var ownedWatches = new List<WatchSession>();
        var errors = new List<string>();
        var committed = false;

        void CheckCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (this.IsClosing || this.CanCloseWindow)
            {
                throw new InvalidOperationException("The window is closing; its current tabs were kept.");
            }
        }

        try
        {
            CheckCurrent();
            foreach (var location in workspace.Tabs)
            {
                BrowserViewModel? browser = null;
                try
                {
                    browser = this._createBrowser(location);
                    await browser.NavigateAsync(location, false).WaitAsync(token);
                    CheckCurrent();
                    if (browser.Error is { } error)
                    {
                        throw new IOException(error);
                    }

                    stagedBrowsers.Add(new BrowserTab(browser, null));
                    browser = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    errors.Add("Tab: " + WorkspacesViewModel.Target(location) + " · " + error.Message);
                }
                finally
                {
                    browser?.CancelPending();
                }
            }

            foreach (var search in workspace.Searches)
            {
                try
                {
                    if (options.StartInvestigations)
                    {
                        await Task.Run(
                            () =>
                            {
                                foreach (var root in search.Roots)
                                {
                                    token.ThrowIfCancellationRequested();
                                    using var key = this._registry.Open(root);
                                }
                            }, token).WaitAsync(token);
                    }

                    CheckCurrent();
                    stagedSearches.Add(this.CreateSearch(search));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    errors.Add(
                        "Search: "
                        + search.Name
                        + " · "
                        + string.Join(", ", search.Roots.Select(WorkspacesViewModel.Target))
                        + " · "
                        + error.Message);
                }
            }

            if (options.StartInvestigations)
            {
                foreach (var definition in workspace.Watches)
                {
                    try
                    {
                        var watch = this.Watches.Concat(preparedWatches).FirstOrDefault(candidate => MatchesWatch(
                            candidate, definition.Location, definition.ValueName));
                        if (watch is null)
                        {
                            watch = new WatchSession(this._registry, definition, this.DispatchWatchUpdate);
                            ownedWatches.Add(watch);
                        }

                        if (!await watch.BaselineReady.WaitAsync(token) || !watch.IsRunning)
                        {
                            throw watch.Error ?? new IOException("The watch stopped before its baseline was ready.");
                        }

                        CheckCurrent();
                        preparedWatches.Add(watch);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        errors.Add(
                            "Watch: "
                            + WatchSurfaceViewModel.FormatTarget(definition.Location, definition.ValueName)
                            + " · "
                            + error.Message);
                    }
                }
            }

            CheckCurrent();
            if (this.IsDialogOpen)
            {
                throw new InvalidOperationException(
                    "Another dialog opened while the workspace was preparing. Close it and retry the workspace.");
            }

            foreach (var watch in preparedWatches.Where(watch => !watch.IsRunning))
            {
                errors.Add("Watch: " + watch.Target + " · " + (watch.Error?.Message ?? "The watch stopped during preparation."));
            }

            foreach (var browser in stagedBrowsers)
            {
                this.AttachBrowser(browser, false);
            }

            foreach (var search in stagedSearches)
            {
                this.BottomTabs.Add(search);
            }

            var watches = 0;
            foreach (var watch in preparedWatches.Distinct().Where(watch => watch.IsRunning))
            {
                var existing = this.Watches.FirstOrDefault(candidate => MatchesWatch(
                    candidate, watch.Definition.Location, watch.Definition.ValueName));
                if (existing is null)
                {
                    this.AttachWatch(watch);
                }

                watches++;
            }

            committed = true;
            var replaced = options.ReplaceTabs && errors.Count == 0 && stagedBrowsers.Count + stagedSearches.Count > 0;
            if (replaced)
            {
                if (this.Tabs.All(originalTabs.Contains))
                {
                    this.OpenBrowser(stagedSearches[0].Search.Roots[0]);
                }

                foreach (var tab in originalTabs)
                {
                    this.CloseTab(tab);
                }
            }

            if (options.StartInvestigations)
            {
                foreach (var search in stagedSearches)
                {
                    _ = search.RunCommand.ExecuteAsync(null);
                }
            }

            if (errors.Count > 0 && source is not null && this.Tabs.Contains(source))
            {
                this.ActiveTab = source;
            }
            else if (stagedSearches.LastOrDefault() is { } search)
            {
                if (stagedBrowsers.LastOrDefault() is { } destination)
                {
                    this.ActiveTab = destination;
                }

                this.SelectBottomTab(search);
            }
            else if (stagedBrowsers.LastOrDefault() is { } browser)
            {
                this.ActiveTab = browser;
            }

            return new WorkspaceRestoreResult(
                stagedBrowsers.Count + stagedSearches.Count, options.StartInvestigations ? stagedSearches.Count : 0, watches, replaced,
                errors.ToArray());
        }
        finally
        {
            if (!committed)
            {
                foreach (var browser in stagedBrowsers)
                {
                    browser.Browser.CancelPending();
                    browser.Close();
                }

                foreach (var search in stagedSearches)
                {
                    search.Close();
                }
            }

            var unattached = ownedWatches.Where(watch => !this.Watches.Contains(watch)).ToArray();
            foreach (var watch in unattached)
            {
                watch.Stop();
            }

            await Task.WhenAll(unattached.Select(watch => watch.Completion));
        }
    }

    private void ProposeWatch(RegistryLocation location, string? valueName)
    {
        var proposal = new WatchDefinition(location, valueName);
        if (this.CanStartProposedWatch(proposal))
        {
            this.WatchProposed?.Invoke(proposal);
        }
    }

    private bool CanStartProposedWatch(WatchDefinition? proposal)
    {
        return proposal?.Location is { IsComputer: false, Machine: null } && !this.IsClosing && !this.CanCloseWindow;
    }

    [RelayCommand(CanExecute = nameof(CanStartProposedWatch))]
    private void StartProposedWatch(WatchDefinition? proposal)
    {
        if (this.CanStartProposedWatch(proposal))
        {
            this.OpenWatch(proposal!.Location, proposal.ValueName);
        }
    }

    internal void OpenWatch(RegistryLocation location, string? valueName = null)
    {
        var session = this.StartWatch(location, valueName, this.DispatchWatchUpdate);
        this.RevealWatch(session);
    }

    [RelayCommand]
    private void RevealWatch(WatchSession? session)
    {
        if (session is null || !this.Watches.Contains(session))
        {
            return;
        }

        this.OpenBottomTool(ToolKind.Watches);
        this.WatchOpened?.Invoke(session);
    }

    [RelayCommand(CanExecute = nameof(HasRunningWatches))]
    private void ShowRunningWatch()
    {
        this.RevealWatch(this.Watches.LastOrDefault(watch => watch.IsRunning));
    }

    private void WatchesChanged()
    {
        this.OnPropertyChanged(nameof(this.RunningWatchCount));
        this.OnPropertyChanged(nameof(this.RunningWatchText));
        this.OnPropertyChanged(nameof(this.HasRunningWatches));
        this.ShowRunningWatchCommand.NotifyCanExecuteChanged();
        this.ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WatchStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(WatchSession.IsRunning) || sender is not WatchSession session)
        {
            return;
        }

        this.WatchesChanged();
        this.ReportWatchFailure(session);
    }

    private void ReportWatchFailure(WatchSession session)
    {
        if (!session.IsRunning && session.Error is { } error && !this.IsClosing && this._reportedWatchFailures.Add(session))
        {
            this._logger.LogFailure(LogLevel.Warning, "Watch stopped unexpectedly", error);
            this.WatchFailed?.Invoke(session);
        }
    }

    internal bool RemoveWatch(WatchSession session)
    {
        if (session.IsRunning || !this.Watches.Contains(session))
        {
            return false;
        }

        session.PropertyChanged -= this.WatchStateChanged;
        this._reportedWatchFailures.Remove(session);
        return this.Watches.Remove(session);
    }

    internal RegistryWorkspace CaptureWorkspace(string name)
    {
        var workspace = new RegistryWorkspace(
            name, this.Tabs.OfType<BrowserTab>().Select(tab => tab.Browser.Location).ToArray(),
            this.Tabs.Concat(this.BottomTabs).OfType<SearchResultsViewModel>().Select(tab => tab.Search).ToArray(),
            this.Watches.Select(watch => watch.Definition).Distinct().ToArray());
        workspace.Validate();
        return workspace;
    }

    internal WatchSession StartWatch(RegistryLocation location, string? valueName, Func<Action, Task> dispatch)
    {
        if (this.IsClosing || this.CanCloseWindow)
        {
            throw new InvalidOperationException("Wait until the window has finished closing or cancel closing before starting a watch.");
        }

        var existing = this.Watches.FirstOrDefault(watch => MatchesWatch(watch, location, valueName));
        if (existing is not null)
        {
            return existing;
        }

        var session = new WatchSession(this._registry, new WatchDefinition(location, valueName), dispatch);
        this.AttachWatch(session);
        return session;
    }

    private static bool MatchesWatch(WatchSession watch, RegistryLocation location, string? valueName)
    {
        return watch.IsRunning
               && new KeyNote(watch.Definition.Location, string.Empty).Matches(location)
               && string.Equals(watch.Definition.ValueName, valueName, StringComparison.OrdinalIgnoreCase);
    }

    private void AttachWatch(WatchSession session)
    {
        this.Watches.Add(session);
        session.PropertyChanged += this.WatchStateChanged;
        this.ReportWatchFailure(session);
    }

    private void NotesChanged(object? sender, EventArgs args)
    {
        this.ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NoteSaveFailed(NoteDraft note, Exception error)
    {
        this._logger.LogFailure(LogLevel.Error, "Save note", error);
    }

    internal NoteDraft GetNote(RegistryLocation location)
    {
        return this.NoteStore.Get(location);
    }

    [RelayCommand]
    private void Exit()
    {
        if (!this.IsClosing && !this.CanCloseWindow)
        {
            this.CloseRequested?.Invoke();
        }
    }

    internal async Task<bool> TryCloseAsync(Func<string?, Task<NoteCloseChoice>> confirmDirtyNotes)
    {
        if (this.IsClosing || this.CanCloseWindow || this.IsDialogOpen)
        {
            return false;
        }

        this.IsClosing = true;
        this.NoteStore.Pause();
        var files = this._registryFiles.ToArray();
        try
        {
            if (!await PrepareRegistryFilesAsync(files))
            {
                return false;
            }

            Dictionary<NoteDraft, string> discarded = [];
            this.NoteStore.Flush();
            while (this.Notes.Any(note => note.IsDirty))
            {
                var error = this.Notes.FirstOrDefault(note => note.SaveError is not null)?.SaveError;
                var choice = await confirmDirtyNotes(error is null ? null : "Could not save notes: " + error);
                if (choice == NoteCloseChoice.Cancel)
                {
                    return false;
                }

                if (choice == NoteCloseChoice.Discard)
                {
                    discarded = this.Notes.Where(note => note.IsDirty).ToDictionary(note => note, note => note.Text);
                    break;
                }

                this.NoteStore.Flush();
            }

            void CheckDrafts()
            {
                if (files.Any(model => !model.CanCompleteClose))
                {
                    throw new InvalidOperationException("A registry file changed while closing. Review your changes and close again.");
                }

                if (this.Notes.Any(note => note.IsDirty && (!discarded.TryGetValue(note, out var text) || text != note.Text)))
                {
                    throw new InvalidOperationException("Notes changed while closing. Review your changes and close again.");
                }
            }

            CheckDrafts();
            this.SaveLayout();
            foreach (var watch in this.Watches)
            {
                watch.Stop();
            }

            await Task.WhenAll(this.Watches.Select(watch => watch.Completion));
            CheckDrafts();
            foreach (var model in files)
            {
                model.CompleteClose();
            }

            this.CanCloseWindow = true;
            return true;
        }
        finally
        {
            this.IsClosing = false;
            if (!this.CanCloseWindow)
            {
                foreach (var model in files)
                {
                    model.CancelPendingClose();
                }

                this.NoteStore.Resume();
            }
        }
    }

    internal void WindowClosed()
    {
        if (this._historyBroadcaster is not null)
        {
            this._historyBroadcaster.Changed -= this.RegistryHistoryChangedAsync;
        }

        this.CanCloseWindow = true;
        this.NoteStore.Changed -= this.NotesChanged;
        this.NoteStore.SaveFailed -= this.NoteSaveFailed;
        this.NoteStore.Dispose();
        foreach (var watch in this.Watches)
        {
            watch.PropertyChanged -= this.WatchStateChanged;
            watch.Stop();
        }

        foreach (var tab in this.Tabs.Concat(this.BottomTabs))
        {
            this.CancelTab(tab);
        }
    }
}
