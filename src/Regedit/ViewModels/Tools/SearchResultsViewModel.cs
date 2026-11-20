// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class SearchResultsViewModel : InvestigationViewModel
{
    internal event Action? RunStarted;

    internal event Action<InvestigationRow>? ResultSelected;

    private readonly IDisposable _connection;
    private readonly InvestigationStore _store;
    private readonly Func<SavedSearch, CancellationToken, Task<RegistrySearchResults>> _find;

    private readonly Func<SavedSearch, Action<RegistrySearchMatch[]>, CancellationToken, Task<RegistrySearchResults>>? _findProgressively;

    private readonly Func<RegistrySearchMatch, CancellationToken, Task> _navigate;
    private readonly Action<RegistrySearchMatch, bool> _open;
    private readonly BulkObservableCollection<InvestigationRow> _filteredRows = [];
    private int _run;
    private int _navigation;
    private string _filterText = string.Empty;

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    public override BulkObservableCollection<InvestigationRow> DisplayRows =>
        this._filterText.Length == 0 ? this.Rows : this._filteredRows;

    public override string DisplayEmptyTitle => this._filterText.Length == 0 ? this.EmptyTitle : "No results match the filter";

    public override string DisplayEmptyDescription => this._filterText.Length == 0
        ? this.EmptyDescription
        : "Try a different name, path or data text.";

    public override string HeaderTitle => this._filterText.Length == 0
        ? $"{this.Title} ({this.Rows.Count:N0})"
        : $"{this.Title} ({this.DisplayRows.Count:N0}/{this.Rows.Count:N0})";

    public override string Tooltip => this.Description;

    public override bool IsBusy => this.RunCommand.IsRunning;

    public bool IsIdle => !this.IsBusy;

    internal SavedSearch Search { get; }

    public string ScopeDescription => Scope(this.Search);

    internal override IRelayCommand ActivateCommand => this.OpenCommand;

    internal SearchResultsViewModel(
        SavedSearch search,
        InvestigationStore store,
        Func<SavedSearch, CancellationToken, Task<RegistrySearchResults>> find,
        Action<RegistrySearchMatch, bool> open,
        Func<RegistrySearchMatch, CancellationToken, Task>? navigate = null,
        Func<SavedSearch, Action<RegistrySearchMatch[]>, CancellationToken, Task<RegistrySearchResults>>? findProgressively = null)
        : base("Find: " + search.Options.Text, "Scope: " + Scope(search), "Ready to search", "Choose Run search to scan the saved scope.")
    {
        this.Search = search;
        this._connection = RemoteRegistryCredentials.Retain(search.Roots.Select(root => root.Machine));
        this._store = store;
        this._find = find;
        this._findProgressively = findProgressively;
        this._open = open;
        this._navigate = navigate
                         ?? ((match, _) =>
                         {
                             open(match, true);
                             return Task.CompletedTask;
                         });
        ((INotifyPropertyChanged)this.Rows).PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Item[]")
            {
                this.OnPropertyChanged(nameof(this.HeaderTitle));
            }
        };
        this.RunCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(this.RunCommand.IsRunning))
            {
                this.OnPropertyChanged(nameof(this.IsBusy));
                this.OnPropertyChanged(nameof(this.IsIdle));
            }
        };
        this.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(this.Description))
            {
                this.OnPropertyChanged(nameof(this.Tooltip));
            }
        };
    }

    partial void OnFilterChanged(string value)
    {
        this.UpdateDisplayedRows(() =>
        {
            this._filterText = value.Trim();
            this._filteredRows.ReplaceAll(this._filterText.Length == 0 ? [] : this.Rows.Where(this.MatchesFilter));
            this.OnPropertyChanged(nameof(this.DisplayRows));
        });
        this.OnPropertyChanged(nameof(this.HeaderTitle));
        this.OnPropertyChanged(nameof(this.DisplayEmptyTitle));
        this.OnPropertyChanged(nameof(this.DisplayEmptyDescription));
    }

    private bool MatchesFilter(InvestigationRow row)
    {
        return row.Title.Contains(this._filterText, StringComparison.OrdinalIgnoreCase)
               || row.Path.Contains(this._filterText, StringComparison.OrdinalIgnoreCase)
               || row.TableSummary.Contains(this._filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void PublishRows(InvestigationRow[] rows, bool append = false)
    {
        if (append)
        {
            if (this._filterText.Length > 0)
            {
                this._filteredRows.AddRange(rows.Where(this.MatchesFilter), reset: false);
            }

            this.Rows.AddRange(rows, reset: false);
            return;
        }

        this.UpdateDisplayedRows(() =>
        {
            if (this._filterText.Length > 0)
            {
                this._filteredRows.ReplaceAll(rows.Where(this.MatchesFilter));
            }

            this.Rows.ReplaceAll(rows);
        });
    }

    internal RegistrySearchMatch? SelectNext()
    {
        var index = this.Selected.Count == 0 ? 0 : this.DisplayRows.IndexOf(this.Selected[^1]) + 1;
        if (this.IsClosed || index >= this.DisplayRows.Count)
        {
            return null;
        }

        var row = this.DisplayRows[index];
        this.Selected = (InvestigationRow[])[row];
        this.ResultSelected?.Invoke(row);
        return (RegistrySearchMatch)row.Source;
    }

    private static string Scope(SavedSearch query)
    {
        return string.Join(
            ", ",
            query.Roots.Select(root => root.Address + (root.View == Microsoft.Win32.RegistryView.Registry32 ? " · 32-bit" : " · 64-bit")));
    }

    private bool IsCurrent(int run)
    {
        return !this.IsClosed && run == this._run;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken token)
    {
        if (this.IsClosed)
        {
            return;
        }

        using var connection = RemoteRegistryCredentials.Retain(this.Search.Roots.Select(root => root.Machine));
        var run = ++this._run;
        var scope = Scope(this.Search);
        this.RunStarted?.Invoke();
        this.Description = "Searching · " + scope;
        this.EmptyTitle = "Searching the registry…";
        this.EmptyDescription = "Cancel search to stop the scan. Earlier results remain available.";
        try
        {
            var (result, published) = await this.FindAsync(run, token);
            token.ThrowIfCancellationRequested();
            if (!this.IsCurrent(run))
            {
                return;
            }

            if (!published)
            {
                var rows = await Task.Run(
                    () => Array.ConvertAll(
                        result.Matches, match =>
                        {
                            token.ThrowIfCancellationRequested();
                            return new InvestigationRow(match);
                        }), token);
                token.ThrowIfCancellationRequested();
                if (!this.IsCurrent(run))
                {
                    return;
                }

                this.Selected = [];
                this.PublishRows(rows);
            }

            if (!this.IsCurrent(run))
            {
                return;
            }

            this.Description = $"{result.Matches.Length:N0} matches · {result.SkippedKeys:N0} inaccessible keys skipped · {scope}";
            this.EmptyTitle = "No matches found";
            this.EmptyDescription = "Try different search text or adjust the scope and filters.";
            if (result.Truncated)
            {
                this.Report(
                    "Result limit reached", "Showing the first 100,000 matches. Narrow the search scope or type filters.",
                    NoticeKind.Warning);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!this.IsCurrent(run))
            {
                return;
            }

            this.Description = $"Search canceled · {this.Rows.Count:N0} matches · {scope}";
            this.EmptyTitle = "Search canceled";
            this.EmptyDescription = "Run the search again when you are ready.";
            if (this.Rows.Count > 0)
            {
                this.Report("Search canceled", "Results already displayed remain available.");
            }
        }
        catch (Exception ex)
        {
            if (!this.IsCurrent(run))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                this.Description = $"Search canceled · {this.Rows.Count:N0} matches · {scope}";
                this.EmptyTitle = "Search canceled";
                this.EmptyDescription = "Run the search again when you are ready.";
                return;
            }

            this.Description = "Search failed · " + scope;
            this.EmptyTitle = "Search could not finish";
            this.EmptyDescription = "Review the error below before trying again.";
            this.Report("Search failed", ex.Message, NoticeKind.Error);
        }
    }

    private async Task<(RegistrySearchResults Result, bool Published)> FindAsync(int run, CancellationToken token)
    {
        if (this._findProgressively is null)
        {
            return (await this._find(this.Search, token), false);
        }

        var batches = Channel.CreateBounded<RegistrySearchMatch[]>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        using var producer = CancellationTokenSource.CreateLinkedTokenSource(token);

        async Task<RegistrySearchResults> ScanAsync()
        {
            try
            {
                return await this._findProgressively(
                    this.Search, batch => batches.Writer.WriteAsync(batch, producer.Token).AsTask().GetAwaiter().GetResult(),
                    producer.Token);
            }
            finally
            {
                batches.Writer.TryComplete();
            }
        }

        var scan = Task.Run(ScanAsync);
        var published = false;
        try
        {
            await foreach (var batch in batches.Reader.ReadAllAsync())
            {
                if (!this.IsCurrent(run) || token.IsCancellationRequested)
                {
                    continue;
                }

                var rows = Array.ConvertAll(batch, match => new InvestigationRow(match));
                if (!published)
                {
                    this.Selected = [];
                    this.PublishRows(rows);
                    published = true;
                }
                else
                {
                    this.PublishRows(rows, append: true);
                }

                this.Description = $"Searching · {this.Rows.Count:N0} matches · {Scope(this.Search)}";
                await Task.Yield();
            }

            return (await scan, published);
        }
        finally
        {
            producer.Cancel();
            try
            {
                await scan;
            }
            catch
            {
                // Observe producer failure without replacing a presentation exception.
            }
        }
    }

    protected override void SelectionChanged()
    {
        base.SelectionChanged();
        this.OpenNewTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSingle), AllowConcurrentExecutions = true)]
    private async Task OpenAsync(CancellationToken token)
    {
        if (this.IsClosed || this.Selected is not [{ Source: RegistrySearchMatch match }])
        {
            return;
        }

        var navigation = ++this._navigation;
        try
        {
            await this._navigate(match, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (navigation == this._navigation && !token.IsCancellationRequested)
            {
                this.Report("Could not open result", match.Location.Address + "\n" + error.Message, NoticeKind.Error);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingle))]
    private void OpenNewTab()
    {
        if (!this.IsClosed && this.Selected is [{ Source: RegistrySearchMatch match }])
        {
            this._open(match, true);
        }
    }

    [RelayCommand]
    private void OpenInBackground(InvestigationRow? row)
    {
        if (!this.IsClosed && row?.Source is RegistrySearchMatch match)
        {
            this._open(match, false);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (await this.RequestNameAsync(this.Search.Name) is { } name && !this.IsClosed)
            {
                this._store.SaveSearch(this.Search with { Name = name.Trim() });
                this.Report("Search saved", name.Trim(), NoticeKind.Success);
            }
        }
        catch (Exception ex)
        {
            this.Report("Could not save search", ex.Message, NoticeKind.Error);
        }
    }

    internal override void Close()
    {
        base.Close();
        ++this._run;
        ++this._navigation;
        this.RunCommand.Cancel();
        this.OpenCommand.Cancel();
        this._connection.Dispose();
    }
}
