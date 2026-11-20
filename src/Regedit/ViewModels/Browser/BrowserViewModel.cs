// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Browser;

public sealed partial class BrowserViewModel : ObservableObject
{
    internal event Action? TreeRefreshRequested;

    internal event Func<RegistryLocation, (string OldName, string NewName)?, Task>? TreeBranchChanged;

    internal event Action<string>? ValueSelectionRequested;

    internal event Func<ValueInspectorViewModel, Task>? InspectorRequested;

    internal event Action<RegistryLocation, string?>? WatchRequested;

    internal event Action? NotesRequested;

    internal event Action<Exception>? DetailsLoadFailed;

    internal event Func<RegistryEditPrompt, Task<bool>>? EditPromptRequested;

    internal event Action<string>? CopyTextRequested;

    internal event Action<Exception>? EditFailed;

    internal event Func<RegistryLocation, Task>? ElevationRequested;

    internal event EventHandler? RegistryChanged;

    public event EventHandler? Navigated;

    private readonly NavigationHistory _history;
    private readonly RegistryEntryClipboard _clipboard;
    private readonly FavoritesStore? _favorites;
    private readonly AppSettings? _settings;
    private readonly SettingsViewModel? _preferences;
    private readonly Action<Uri> _launchWebSearch;
    private readonly RegistryHistoryBroadcaster? _historyBroadcaster;
    private readonly RegistryListMetadata _metadata;
    private readonly CancellationTokenSource _editCancellation = new();
    private IReadOnlyList<RegistrySortColumn> _columns = RegistryColumns.Defaults();

    private List<RegistryEntry> _allEntries = [];
    private RegistryEntry? _parentEntry;
    private int _request;
    private int _navigationVersion;

    private bool _closed;
    private Task _pendingNavigation = Task.CompletedTask;
    private CancellationTokenSource? _filterCancellation;

    internal RegistryService Registry { get; }

    internal RegistryBranchCommands Branches { get; }

    internal RegistryAddressSuggestions Addresses { get; }

    internal BrowserSnapshotsViewModel Snapshots { get; }

    internal BrowserSearchViewModel Search { get; }

    internal Func<RegistryLocation, Task>? AddFavoriteRequested { get; set; }

    internal int NavigationVersion => this._navigationVersion;

    internal bool HasSnapshot { get; private set; }

    internal Task PendingFilter { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(EditValueCommand), nameof(EditBinaryCommand), nameof(DuplicateValueCommand), nameof(CreateKeyCommand),
        nameof(CreateValueCommand), nameof(RenameCommand), nameof(DeleteCommand), nameof(RenameTreeKeyCommand),
        nameof(DeleteTreeKeyCommand), nameof(PasteCommand), nameof(PasteIntoCommand), nameof(ToggleFavoriteCommand),
        nameof(SwitchHiveCommand), nameof(UndoCommand), nameof(RedoCommand))]
    internal partial bool CanPrompt { get; private set; } = true;

    private RegistryEntry? SingleEntry => this.SelectedEntries.Count == 1 ? this.SelectedEntries[0] : null;

    internal RegistryLocation SelectedFavoriteLocation => this.SingleEntry?.Key ?? this.Location;

    private RegistryLocation? EntryDestination => this.SingleEntry?.ParentLocation ?? this.SingleEntry?.Key;

    private StoredRegistryItem? SingleStoredItem =>
        this.SingleEntry is { } row ? StoredRegistryItem.FromRow(this.Location, row) : null;

    internal bool IsAdministrator { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<RegistryEntry> Entries { get; private set; } = [];

    public int VisibleEntryCount => this.Entries.Count(entry => !entry.IsParent);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Connection), nameof(Is32Bit), nameof(CanGoUp), nameof(IsKeyLocation))]
    [NotifyCanExecuteChangedFor(
        nameof(WatchBranchCommand), nameof(WatchValueCommand), nameof(UpCommand), nameof(CreateKeyCommand), nameof(CreateValueCommand),
        nameof(DuplicateValueCommand), nameof(PasteCommand), nameof(ToggleFavoriteCommand), nameof(RevealFileCommand),
        nameof(OpenLinkTargetCommand), nameof(SwitchHiveCommand))]
    public partial RegistryLocation Location { get; private set; }

    public RegistryEntry? SelectedEntry
    {
        get => this.SelectedEntries.FirstOrDefault();
        set => this.SetSelection(value is null ? [] : (RegistryEntry[])[value]);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEntry), nameof(HasSelection), nameof(ActivateEntryCommand))]
    [NotifyCanExecuteChangedFor(
        nameof(NavigateEntryCommand), nameof(EditValueCommand), nameof(EditBinaryCommand), nameof(DuplicateValueCommand),
        nameof(RenameCommand), nameof(DeleteCommand), nameof(CopyCommand), nameof(InspectCommand), nameof(WatchValueCommand),
        nameof(RevealFileCommand), nameof(OpenLinkTargetCommand))]
    public partial IReadOnlyList<RegistryEntry> SelectedEntries { get; private set; } = [];

    public bool HasSelection => this.SelectedEntries.Count > 0;

    public IAsyncRelayCommand ActivateEntryCommand =>
        this.EntryDestination is not null ? this.NavigateEntryCommand : this.EditValueCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteAction), nameof(FavoriteHelp), nameof(FavoriteGlyph))]
    public partial bool IsFavorite { get; private set; }

    public string FavoriteAction => this.IsFavorite ? "Remove favorite" : "Add favorite";

    public string FavoriteHelp => this.FavoriteAction + " (Ctrl+D)";

    public string FavoriteGlyph => this.IsFavorite ? "\uE735" : "\uE734";

    private bool CanToggleFavorite => this.CanPrompt && this._favorites is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    internal partial bool IsUpdatingValue { get; private set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowKeysInList { get; set; }

    [ObservableProperty]
    public partial bool ShowParentKeyInList { get; set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "Ready";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool CanGoBack => this._history.CanGoBack;

    public bool CanGoForward => this._history.CanGoForward;

    public bool CanGoUp => this.Location.GetNavigationParent() is not null;

    public bool IsKeyLocation => !this.Location.IsComputer;

    public bool HasSubKeys => this._allEntries.Any(item => item.IsKey);

    public bool Is32Bit => this.Location.View == Microsoft.Win32.RegistryView.Registry32;

    public string Title => (!this.Location.IsComputer && this.Location.Machine is { } machine
                               ? $"{machine} · {this.Location.Name}"
                               : this.Location.Name)
                           + (this.Is32Bit ? " · 32-bit" : string.Empty);

    public string Connection => (this.Location.Machine ?? "This computer")
                                + (this.Location.View == Microsoft.Win32.RegistryView.Registry32
                                    ? " · 32-bit registry"
                                    : " · 64-bit registry");

    private bool CanNavigateEntry => this.EntryDestination is not null;

    private bool CanDuplicateValue => this.CanPrompt && this.IsKeyLocation && this.SingleEntry?.Value is not null;

    private bool CanCreateKey => this.CanPrompt && this.IsKeyLocation;

    private bool CanRename => this.CanPrompt && this.SingleStoredItem?.CanRename == true;

    private bool CanDelete => this.CanPrompt && this.DeletableSelection().Any();

    private bool CanInspect => this.SingleEntry?.Value is not null;

    private bool CanWatchBranch => this.IsKeyLocation && this.Location.Machine is null;

    private bool CanWatchValue => this.CanWatchBranch && this.SingleEntry is { IsKey: false, IsParent: false };

    private bool CanCopy => this.SingleEntry is { IsStored: true } row && row.Key?.IsComputer != true;

    private bool CanPaste => this.CanPasteInto(this.Location);

    [ObservableProperty]
    public partial BrowserLayout Layout { get; private set; } = new();

    [ObservableProperty]
    public partial RegistrySortColumn SortColumn { get; private set; } = RegistrySortColumn.Name;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    public BrowserViewModel(RegistryService registry, RegistryLocation initial)
        : this(registry, initial, new RegistryEntryClipboard())
    {
    }

    internal BrowserViewModel(
        RegistryService registry,
        RegistryLocation initial,
        RegistryEntryClipboard clipboard,
        FavoritesStore? favorites = null,
        AppSettings? settings = null,
        SettingsViewModel? preferences = null,
        Action<Uri>? launchWebSearch = null,
        RegistryHistoryBroadcaster? historyBroadcaster = null)
    {
        this.Registry = registry;
        this.Addresses = new RegistryAddressSuggestions(registry.SubKeys);
        this._metadata = new RegistryListMetadata(registry);
        this._settings = settings;
        this._preferences = preferences;
        this._historyBroadcaster = historyBroadcaster;
        this._launchWebSearch = launchWebSearch ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        if (settings is not null)
        {
            this._columns = settings.ListColumns;
            if (!this._columns.Contains(this.SortColumn))
            {
                this.SortColumn = this._columns[0];
            }

            settings.NoteSaved += this.NoteSaved;
        }

        this._history = new NavigationHistory(initial);
        this._clipboard = clipboard;
        this._favorites = favorites;
        this.Location = initial;
        this.Branches = new RegistryBranchCommands(registry);
        this.Branches.RegistryActivityCompleted += () => this.RegistryChanged?.Invoke(this, EventArgs.Empty);
        this.Branches.TreeChanged += async navigateTo =>
        {
            this.TreeRefreshRequested?.Invoke();
            await this.NavigateAsync(navigateTo ?? this.Location, navigateTo is not null);
        };
        this.Snapshots = new BrowserSnapshotsViewModel(registry);
        this.Search = new BrowserSearchViewModel(() => this.Location);
        this.Search.StatusChanged += message => this.Status = message;
        this._clipboard.PropertyChanged += this.ClipboardChanged;
        if (favorites is not null)
        {
            favorites.Changed += this.FavoritesChanged;
        }

        this.RefreshFavorite();
    }

    /// <summary>Reads subkey names off the UI thread.</summary>
    internal Task<string[]> ReadSubKeysAsync(RegistryLocation location)
    {
        return Task.Run(() => this.Registry.SubKeys(location));
    }

    internal void SetSelection(IEnumerable<RegistryEntry> entries)
    {
        this.SelectedEntries = entries.ToArray();
    }

    private void ClipboardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        this.PasteCommand.NotifyCanExecuteChanged();
        this.PasteIntoCommand.NotifyCanExecuteChanged();
    }

    partial void OnLocationChanged(RegistryLocation value) { this.RefreshFavorite(); }

    private void FavoritesChanged(object? sender, EventArgs args) { this.RefreshFavorite(); }

    private void RefreshFavorite() { this.IsFavorite = this._favorites?.Contains(this.Location) == true; }

    [RelayCommand(CanExecute = nameof(CanToggleFavorite))]
    private async Task ToggleFavoriteAsync(RegistryLocation? target)
    {
        if (!this.CanToggleFavorite || this._favorites is not { } store)
        {
            return;
        }

        var location = target ?? this.Location;
        this.CanPrompt = false;
        this.Error = null;
        try
        {
            if (store.Contains(location))
            {
                store.RemoveLocation(location);
            }
            else if (this.AddFavoriteRequested is { } add)
            {
                await add(location);
            }
        }
        catch (Exception ex)
        {
            this.EditFailed?.Invoke(ex);
            this.Error = ex.Message;
        }
        finally
        {
            this.CanPrompt = !this._closed;
        }
    }

    partial void OnFilterChanged(string value)
    {
        this._filterCancellation?.Cancel();
        this._metadata.Cancel();
        if (this.HasSelection)
        {
            this.SelectedEntry = null;
        }

        if (value.Length == 0)
        {
            this.ApplyFilter();
            this.PendingFilter = this.LoadSortDetailsAsync();
        }
        else if (!this._closed)
        {
            this.PendingFilter = this.FilterAfterTypingAsync();
        }
    }

    private async Task FilterAfterTypingAsync()
    {
        using var request = new CancellationTokenSource();
        this._filterCancellation = request;
        var token = request.Token;
        try
        {
            await Task.Delay(180, token);
            var rows = this.OrderedRows(token);
            var result = await Task.Run(() => rows.ToList(), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            this.PublishRows(result, false);
            await this.LoadSortDetailsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                this.Error = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(this._filterCancellation, request))
            {
                this._filterCancellation = null;
            }
        }
    }

    partial void OnShowKeysInListChanged(bool value)
    {
        this.ApplyFilter();
        _ = this.LoadSortDetailsAsync();
    }

    partial void OnShowParentKeyInListChanged(bool value)
    {
        this.ApplyFilter();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Use32BitViewAsync()
    {
        return this.NavigateAsync(this.Location with { View = Microsoft.Win32.RegistryView.Registry32 });
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Use64BitViewAsync()
    {
        return this.NavigateAsync(this.Location with { View = Microsoft.Win32.RegistryView.Registry64 });
    }

    private bool CanSwitchHive(RegistryLocation? source)
    {
        return this.CanPrompt && (source ?? this.Location).GetAlternateHiveLocation() is not null;
    }

    [RelayCommand(CanExecute = nameof(CanSwitchHive), AllowConcurrentExecutions = true)]
    private Task SwitchHiveAsync(RegistryLocation? source)
    {
        return this.CanPrompt && (source ?? this.Location).GetAlternateHiveLocation() is { } target
            ? this.NavigateAsync(target)
            : Task.CompletedTask;
    }

    public Task NavigateAsync(RegistryLocation target, bool addToHistory = true)
    {
        if (addToHistory)
        {
            this._navigationVersion++;
        }

        return this.LoadAsync(target, addToHistory ? () => this._history.Navigate(target) : null);
    }

    internal async Task NavigateToMatchAsync(SearchHit hit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        this.Filter = string.Empty;
        var navigation = this.NavigateAsync(hit.Location);
        var request = this._request;
        await navigation;
        token.ThrowIfCancellationRequested();
        if (this._request == request && this.Location == hit.Location && this.Error is null && hit.ValueName is { } name)
        {
            this.ValueSelectionRequested?.Invoke(name);
        }
    }

    public IReadOnlyList<(int Index, RegistryLocation Location)> GetHistory(bool forward)
    {
        return this._history.GetEntries(forward);
    }

    public Task NavigateHistoryAsync(int index, RegistryLocation target)
    {
        if (this._history.GetTarget(index) != target)
        {
            return Task.CompletedTask;
        }

        this._navigationVersion++;
        return this.LoadAsync(target, () => this._history.MoveTo(index));
    }

    public void CancelPending()
    {
        ++this._request;
        this._closed = true;
        this._filterCancellation?.Cancel();
        this._previewCancellation?.Cancel();
        this._metadata.Cancel();
        if (this._settings is not null)
        {
            this._settings.NoteSaved -= this.NoteSaved;
        }

        this._editCancellation.Cancel();
        this.Search.Close();
        this.Snapshots.Close();
        this.Branches.Close();
        this.CanPrompt = false;
        this._clipboard.PropertyChanged -= this.ClipboardChanged;
        if (this._favorites is not null)
        {
            this._favorites.Changed -= this.FavoritesChanged;
        }

        this.AddFavoriteRequested = null;
    }

    internal async Task RefreshAfterImportAsync()
    {
        if (!this._closed)
        {
            this.RegistryChanged?.Invoke(this, EventArgs.Empty);
            this.TreeRefreshRequested?.Invoke();
            await this.WaitForNavigationAsync();
            if (!this._closed)
            {
                await this.RefreshAsync();
            }
        }
    }

    private async Task WaitForNavigationAsync()
    {
        // Let a newer navigation finish before refreshing its visible snapshot.
        while (this.IsLoading && !this._closed)
        {
            await this._pendingNavigation;
        }
    }

    private Task LoadAsync(RegistryLocation target, Action? commitHistory)
    {
        return this._pendingNavigation = this.LoadLocationAsync(target, commitHistory);
    }

    private async Task LoadLocationAsync(RegistryLocation target, Action? commitHistory)
    {
        var request = ++this._request;
        this._metadata.Cancel();
        this.IsLoading = true;
        this.Error = null;
        try
        {
            RegistryWorkspace.ValidateLocation(target, true);
            var entries = await Task.Run(() =>
            {
                if (target.IsComputer)
                {
                    return target.GetHives().Select(hive => new RegistryEntry(hive, null)).ToList();
                }

                var snapshot = this.Registry.Read(target, target.Machine is null);
                var loaded = snapshot.SubKeys.Select(name => new RegistryEntry(target.Child(name), null)).Concat(
                    snapshot.Values.Select(value => RegistryEntry.ForValue(value, snapshot.LastWriteTimeUtc, target))).ToList();
                if (!snapshot.Values.Any(value => value.Name.Length == 0))
                {
                    loaded.Add(RegistryEntry.ForValue(null, snapshot.LastWriteTimeUtc, target));
                }

                return loaded;
            });
            if (request != this._request)
            {
                return;
            }

            commitHistory?.Invoke();
            var clearFilter = this.Location != target && this.Filter != string.Empty;
            this._allEntries = entries;
            this.HasSnapshot = true;
            this.Location = target;
            this.ApplyNotes();
            if (clearFilter)
            {
                this.Filter = string.Empty;
            }
            else
            {
                this.ApplyFilter();
            }

            if (commitHistory is not null)
            {
                this.OnPropertyChanged(nameof(this.CanGoBack));
                this.OnPropertyChanged(nameof(this.CanGoForward));
                this.BackCommand.NotifyCanExecuteChanged();
                this.ForwardCommand.NotifyCanExecuteChanged();
            }

            this.OnPropertyChanged(nameof(this.HasSubKeys));
            this.Navigated?.Invoke(this, EventArgs.Empty);
            await this.LoadSortDetailsAsync();
        }
        catch (Exception ex)
        {
            if (request == this._request)
            {
                this.Error = ex.Message;
            }
        }
        finally
        {
            if (request == this._request)
            {
                this.IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        return this.NavigateAsync(this.Location, false);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task UpAsync()
    {
        return this.Location.GetNavigationParent() is { } parent ? this.NavigateAsync(parent) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task BackAsync()
    {
        return this._history.CanGoBack ? this.LoadAsync(this._history.BackTarget, () => this._history.Back()) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task ForwardAsync()
    {
        return this._history.CanGoForward ? this.LoadAsync(this._history.ForwardTarget, () => this._history.Forward()) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanNavigateEntry), AllowConcurrentExecutions = true)]
    private Task NavigateEntryAsync()
    {
        return this.EntryDestination is { } key ? this.NavigateAsync(key) : Task.CompletedTask;
    }

    private bool CanEditValue()
    {
        return this.CanPrompt && this.SingleEntry is { IsKey: false, IsParent: false };
    }

    [RelayCommand(CanExecute = nameof(CanEditValue))]
    private Task EditValueAsync()
    {
        return this.EditSelectedValueAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanEditValue))]
    private Task EditBinaryAsync()
    {
        return this.EditSelectedValueAsync(true);
    }

    private Task EditSelectedValueAsync(bool binary)
    {
        if (!this.CanEditValue() || this.SingleEntry is not { } row)
        {
            return Task.CompletedTask;
        }

        return this.ShowValueEditorAsync(this.Location, row.Value, row.Value?.Type ?? RegistryValueType.String, binary, false);
    }

    private bool CanCreateValue(string? type)
    {
        return this.CanCreateKey && uint.TryParse(type, out _);
    }

    [RelayCommand(CanExecute = nameof(CanDuplicateValue))]
    private Task DuplicateValueAsync()
    {
        if (!this.CanDuplicateValue || this.SingleEntry?.Value is not { } value)
        {
            return Task.CompletedTask;
        }

        var owner = this.Location;
        var existing = this._allEntries.Where(entry => entry.Value is not null).Select(entry => entry.Value!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = value.Name.Length == 0 ? "Default" : value.Name;
        string name;
        for (var number = 1;; number++)
        {
            var suffix = number == 1 ? " - Copy" : $" - Copy ({number})";
            name = stem[..Math.Min(stem.Length, 16383 - suffix.Length)] + suffix;
            if (!existing.Contains(name))
            {
                break;
            }
        }

        var form = new RegistryNamePrompt(RegistryNameOperation.DuplicateValue, name);
        return this.PromptAndWriteAsync(
            form, new RegistryEditTarget(owner), token => this.Registry.Write(owner, value with { Name = form.Name }, null, token),
            refreshLocation: owner, reselect: entries => entries.FirstOrDefault(entry => entry.Value?.Name == form.Name));
    }

    [RelayCommand(CanExecute = nameof(CanCreateValue))]
    private Task CreateValueAsync(string? type)
    {
        return this.IsKeyLocation && uint.TryParse(type, out var valueType)
            ? this.ShowValueEditorAsync(this.Location, null, (RegistryValueType)valueType, false, true)
            : Task.CompletedTask;
    }

    private Task ShowValueEditorAsync(RegistryLocation owner, RegistryValue? original, RegistryValueType type, bool binary, bool creating)
    {
        var editor = new RegistryValueEditorViewModel(original, type, binary, creating);
        return this.PromptAndWriteAsync(
            editor, new RegistryEditTarget(owner, original?.Name), token => this.Registry.Write(owner, editor.Value, original, token));
    }

    private async Task RefreshValueAsync(RegistryLocation owner, string name)
    {
        var request = this._request;
        try
        {
            var (value, modified) = await Task.Run(() =>
            {
                var current = this.Registry.ReadValue(owner, name, out var lastWrite);
                return (current, lastWrite);
            });
            Task filter;
            do
            {
                filter = this.PendingFilter;
                await filter;
            } while (!ReferenceEquals(filter, this.PendingFilter));

            if (this._closed || request != this._request || !this.Location.IsSameLocation(owner))
            {
                return;
            }

            var index = this._allEntries.FindIndex(entry =>
                !entry.IsKey && string.Equals(entry.Value?.Name ?? string.Empty, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            this.IsUpdatingValue = true;
            var previous = this._allEntries[index];
            var removed = value is null && name.Length != 0;
            if (removed)
            {
                this._allEntries.RemoveAt(index);
            }
            else
            {
                previous.SetValue(value);
            }

            foreach (var entry in this._allEntries.Where(entry => !entry.IsKey))
            {
                entry.SetContainingKeyLastWriteTime(modified);
            }

            var selected = this.SelectedEntries;
            var oldIndex = this.Entries.IndexOf(previous);
            var newIndex = removed ? -1 : this.OrderedRows().ToList().IndexOf(previous);
            if (oldIndex >= 0)
            {
                if (newIndex < 0)
                {
                    this.Entries.RemoveAt(oldIndex);
                }
                else if (oldIndex != newIndex)
                {
                    this.Entries.Move(oldIndex, newIndex);
                }
            }
            else if (newIndex >= 0)
            {
                this.Entries.Insert(newIndex, previous);
            }

            var selection = selected.Where(this.Entries.Contains).ToArray();
            if (!selection.SequenceEqual(this.SelectedEntries))
            {
                this.SetSelection(selection);
            }
            else if (selected.Contains(previous))
            {
                this.CopyCommand.NotifyCanExecuteChanged();
                this.InspectCommand.NotifyCanExecuteChanged();
                this.RevealFileCommand.NotifyCanExecuteChanged();
                this.OpenLinkTargetCommand.NotifyCanExecuteChanged();
                this.IsPreviewCurrent = false;
                this.UpdateInspector();
            }

            this.UpdateStatus();
        }
        catch (Exception ex)
        {
            if (!this._closed && request == this._request)
            {
                this.Error = ex.Message;
            }
        }
        finally
        {
            this.IsUpdatingValue = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateKey))]
    private Task CreateKeyAsync()
    {
        var parent = this.Location;
        if (parent.IsComputer)
        {
            return Task.CompletedTask;
        }

        var form = new RegistryNamePrompt(RegistryNameOperation.CreateKey, "New Key");
        return this.PromptAndWriteAsync(
            form, new RegistryEditTarget(parent), token => this.Registry.CreateKey(parent, form.Name, token), () => parent.Child(form.Name),
            parent);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private Task RenameAsync()
    {
        return this.RenameItemAsync(this.SingleStoredItem);
    }

    private bool CanEditTreeKey(RegistryLocation? key)
    {
        return this.CanPrompt && key?.Parent is not null;
    }

    [RelayCommand(CanExecute = nameof(CanEditTreeKey))]
    private Task RenameTreeKeyAsync(RegistryLocation? key)
    {
        return key is null ? Task.CompletedTask : this.RenameItemAsync(new StoredRegistryItem.Key(key));
    }

    /// <summary>Whether a list row can be renamed in place now (not a parent row, default value or root key).</summary>
    internal bool CanRenameEntry(RegistryEntry entry)
    {
        return this.CanPrompt && StoredRegistryItem.FromRow(this.Location, entry) is { CanRename: true };
    }

    /// <summary>
    ///     Quick checks for an inline rename, before anything is written. Returns the message to show, or null when the
    ///     name may be tried. The registry still has the final word; a failed write reopens the rename dialog.
    /// </summary>
    internal string? RenameEntryError(RegistryEntry entry, string name)
    {
        if (StoredRegistryItem.FromRow(this.Location, entry) is not { CanRename: true } item)
        {
            return "This item cannot be renamed.";
        }

        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        try
        {
            if (item is StoredRegistryItem.Key)
            {
                RegistryLocation.ValidateKeyName(name);
            }
            else
            {
                RegistryService.ValidateValueName(name);
            }
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        var taken = this.Entries.Any(other =>
            !ReferenceEquals(other, entry)
            && !other.IsParent
            && other.IsKey == entry.IsKey
            && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase));
        return !taken ? null : entry.IsKey ? "A key with this name already exists." : "A value with this name already exists.";
    }

    /// <summary>
    ///     Renames a row to a name typed in place. If the write fails, the rename dialog opens with the typed name and
    ///     the error, offering the same recovery (retry, run as administrator) as a dialog rename.
    /// </summary>
    internal Task RenameEntryAsync(RegistryEntry entry, string name)
    {
        return this.RenameItemAsync(StoredRegistryItem.FromRow(this.Location, entry), name);
    }

    private Task RenameItemAsync(StoredRegistryItem? item, string? confirmedName = null)
    {
        if (item is not { CanRename: true })
        {
            return Task.CompletedTask;
        }

        var form = new RegistryNamePrompt(RegistryNameOperation.Rename, confirmedName ?? item.Name);
        var key = (item as StoredRegistryItem.Key)?.Location;
        return this.PromptAndWriteAsync(
            form, EditTarget(item), token => item.Rename(this.Registry, form.Name, token),
            () => key is null ? null : this.LocationAfterKeyChange(key, form.Name), key?.Parent, key?.Name,
            confirmed: confirmedName is not null,
            reselect: entries => entries.FirstOrDefault(entry =>
                !entry.IsParent
                && entry.IsKey == (key is not null)
                && string.Equals(entry.Name, form.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private IEnumerable<StoredRegistryItem> DeletableSelection()
    {
        return this.SelectedEntries.Select(row => StoredRegistryItem.FromRow(this.Location, row)).OfType<StoredRegistryItem>()
            .Where(item => item.CanDelete);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync()
    {
        return this.DeleteItemsAsync(this.DeletableSelection().ToArray());
    }

    [RelayCommand(CanExecute = nameof(CanEditTreeKey))]
    private Task DeleteTreeKeyAsync(RegistryLocation? key)
    {
        return key?.Parent is null ? Task.CompletedTask : this.DeleteItemsAsync([new StoredRegistryItem.Key(key)]);
    }

    private Task DeleteItemsAsync(StoredRegistryItem[] items)
    {
        if (items.Length == 0)
        {
            return Task.CompletedTask;
        }

        return this.PromptAndWriteAsync(
            new RegistryDeletePrompt(items), EditTarget(items[0]), token =>
            {
                var deleted = 0;
                try
                {
                    foreach (var item in items)
                    {
                        item.Delete(this.Registry, token);
                        deleted++;
                    }
                }
                catch (Exception ex) when (items.Length > 1 && ex is not OperationCanceledException)
                {
                    throw new IOException($"Deleted {deleted} of {items.Length} items before an error: {ex.Message}", ex);
                }
            },
            () => items.OfType<StoredRegistryItem.Key>().Select(key => this.LocationAfterKeyChange(key.Location, null))
                .FirstOrDefault(location => location is not null),
            items.OfType<StoredRegistryItem.Key>().FirstOrDefault()?.Location.Parent);
    }

    private RegistryLocation? LocationAfterKeyChange(RegistryLocation key, string? name)
    {
        var current = this.Location;
        if (current.Hive != key.Hive
            || current.View != key.View
            || !string.Equals(current.Machine, key.Machine, StringComparison.OrdinalIgnoreCase)
            || !(current.SubKey.Equals(key.SubKey, StringComparison.OrdinalIgnoreCase)
                 || current.SubKey.StartsWith(key.SubKey + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (name is null || key.Parent is not { } parent)
        {
            return key.Parent;
        }

        return current with { SubKey = parent.Child(name).SubKey + current.SubKey[key.SubKey.Length..] };
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectAsync()
    {
        if (!this.CanInspect || this.IsLoading)
        {
            return;
        }

        if (this.IsInspectorOpen)
        {
            this.UpdateInspector();
            return;
        }

        if (this.CreateInspector() is { } inspector && this.InspectorRequested is { } show)
        {
            await show(inspector);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWatchValue))]
    private void WatchValue()
    {
        if (this.CanWatchBranch && this.SingleEntry is { IsKey: false, IsParent: false } row)
        {
            this.WatchRequested?.Invoke(this.Location, row.Value?.Name ?? string.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWatchBranch))]
    private void WatchBranch()
    {
        if (this.CanWatchBranch)
        {
            this.WatchRequested?.Invoke(this.Location, null);
        }
    }

    [RelayCommand]
    private void OpenNotes()
    {
        this.NotesRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (this.CanCopy && this.SingleEntry is { } row)
        {
            this.CopyEntry(row);
        }
    }

    private bool CanCopyTreeKey(RegistryLocation? key)
    {
        return key is { IsComputer: false };
    }

    [RelayCommand(CanExecute = nameof(CanCopyTreeKey))]
    private void CopyTreeKey(RegistryLocation? key)
    {
        if (this.CanCopyTreeKey(key))
        {
            this.CopyEntry(new RegistryEntry(key, null));
        }
    }

    private void CopyEntry(RegistryEntry row)
    {
        this._clipboard.Entry = row with { };
        this.CopyTextRequested?.Invoke(row.Key?.Address ?? row.Data);
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private Task PasteAsync()
    {
        return this.PasteIntoAsync(this.Location);
    }

    private bool CanPasteInto(RegistryLocation? destination)
    {
        return this.CanPrompt && destination is { IsComputer: false } && this._clipboard.Entry is { IsStored: true };
    }

    [RelayCommand(CanExecute = nameof(CanPasteInto))]
    private async Task PasteIntoAsync(RegistryLocation? destination)
    {
        if (!this.CanPasteInto(destination) || destination is null || this._clipboard.Entry is not { } copied)
        {
            return;
        }

        using var connection = RemoteRegistryCredentials.Retain((string?[])[copied.Key?.Machine]);
        var form = new RegistryNamePrompt(
            RegistryNameOperation.Paste, copied.Key?.Name ?? copied.Value?.Name ?? string.Empty, destination.Name);
        await this.PromptAndWriteAsync(
            form, new RegistryEditTarget(destination), token =>
            {
                if (copied.Key is { } key)
                {
                    this.Registry.CopyKey(key, destination, form.Name, token);
                }
                else if (copied.Value is { } value)
                {
                    this.Registry.Write(destination, value with { Name = form.Name }, null, token);
                }
            }, changedParent: copied.Key is null ? null : destination, refreshLocation: destination);
    }

    private static RegistryEditTarget EditTarget(StoredRegistryItem item)
    {
        return item switch
        {
            StoredRegistryItem.Key key => new RegistryEditTarget(key.Location),
            StoredRegistryItem.Value value => new RegistryEditTarget(value.Parent, value.Original.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
    }

    private async Task PromptAndWriteAsync(
        RegistryEditPrompt prompt,
        RegistryEditTarget target,
        Action<CancellationToken> write,
        Func<RegistryLocation?>? destination = null,
        RegistryLocation? changedParent = null,
        string? renamedFrom = null,
        RegistryLocation? refreshLocation = null,
        bool confirmed = false,
        Func<IReadOnlyList<RegistryEntry>, RegistryEntry?>? reselect = null)
    {
        if (!this.CanPrompt || this.EditPromptRequested is not { } show)
        {
            return;
        }

        using var connection = RemoteRegistryCredentials.Retain((string?[])[target.Location.Machine]);
        this.CanPrompt = false;
        var version = this._request;
        try
        {
            // A confirmed prompt (an inline edit) writes first and only shows the dialog to recover from a failure.
            while ((confirmed || await show(prompt)) && !this._closed)
            {
                confirmed = false;
                if (prompt is RegistryValueEditorViewModel editor && !editor.Validate())
                {
                    continue;
                }

                prompt.Error = null;
                var succeeded = false;
                var recorded = new List<JournalEntry>();
                try
                {
                    await Task.Run(() =>
                    {
                        if (this.Registry.Journal is { } journal)
                        {
                            journal.TrackEdits(() => write(this._editCancellation.Token), recorded);
                        }
                        else
                        {
                            write(this._editCancellation.Token);
                        }
                    });
                    succeeded = true;
                }
                catch (OperationCanceledException) when (this._closed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    this.EditFailed?.Invoke(ex);
                    var failedTarget = prompt is RegistryValueEditorViewModel valueEditor
                        ? target with { ValueName = valueEditor.Name }
                        : target;
                    prompt.SetFailure(ex, failedTarget, this.IsAdministrator, this.ElevationRequested);
                }

                this.RememberEdit(recorded, succeeded);
                this.RegistryChanged?.Invoke(this, EventArgs.Empty);
                if (changedParent is not null && this.TreeBranchChanged is { } refreshBranch)
                {
                    await refreshBranch(
                        changedParent,
                        succeeded && renamedFrom is not null && prompt is RegistryNamePrompt name ? (renamedFrom, name.Name) : null);
                }

                if (this._closed)
                {
                    return;
                }

                if (prompt is RegistryValueEditorViewModel { IsCreating: false })
                {
                    await this.WaitForNavigationAsync();
                    if (!this._closed && this.Location.IsSameLocation(target.Location))
                    {
                        await this.RefreshValueAsync(target.Location, target.ValueName ?? string.Empty);
                    }
                }
                else if (succeeded && version == this._request && destination?.Invoke() is { } key)
                {
                    await this.NavigateAsync(key);
                }
                else
                {
                    await this.WaitForNavigationAsync();
                    if (this._closed)
                    {
                        return;
                    }

                    if (refreshLocation is null || this.Location.IsSameLocation(refreshLocation))
                    {
                        var stillAtOrigin = version == this._request;
                        var refresh = this.RefreshAsync();
                        if (stillAtOrigin)
                        {
                            version = this._request;
                        }

                        await refresh;

                        // The refresh replaces every row; keep the edited item selected (for example under its new name).
                        if (succeeded && stillAtOrigin && version == this._request && reselect?.Invoke(this.Entries) is { } edited)
                        {
                            this.SelectedEntry = edited;
                        }
                    }
                }

                if (succeeded)
                {
                    return;
                }

                if (prompt is RegistryDeletePrompt && prompt.Recovery is null)
                {
                    this.Error = prompt.Error;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            this.EditFailed?.Invoke(ex);
            this.Error = ex.Message;
        }
        finally
        {
            this.CanPrompt = !this._closed;
        }
    }

    internal void RestoreLayout(BrowserLayout? layout)
    {
        this.Layout = (layout ?? new BrowserLayout()).Normalize();
        var saved = Enum.Parse<RegistrySortColumn>(this.Layout.SortColumn);
        this.SortColumn = this._columns.Contains(saved) ? saved : this._columns[0];
        this.SortDescending = this.SortColumn == saved && this.Layout.SortDescending;
        this.ApplyFilter();
    }

    internal BrowserLayout CaptureLayout()
    {
        return this.Layout with { SortColumn = this.SortColumn.ToString(), SortDescending = this.SortDescending };
    }

    internal void SetTreeLayout(double width, bool hidden)
    {
        this.Layout = (this.Layout with { TreeWidth = width, TreeHidden = hidden }).Normalize();
    }

    /// <summary>Sets a column width; 0 makes Name and Data fill the remaining space.</summary>
    internal void SetColumnWidth(RegistrySortColumn column, double width)
    {
        if (double.IsFinite(width))
        {
            this.Layout = this.Layout.WithWidth(column, width <= 0 ? 0 : Math.Clamp(width, 80, 1600));
        }
    }

    internal void ResizeColumn(RegistrySortColumn column, double current, double delta)
    {
        var width = Math.Clamp(current + delta, 80, 1600);
        this.Layout = this.Layout.WithWidth(column, width);
    }

    public static string SortLabel(RegistrySortColumn column, RegistrySortColumn current, bool descending)
    {
        return column.Label() + (column == current ? descending ? " ↓" : " ↑" : string.Empty);
    }

    public static string SortStatus(RegistrySortColumn column, RegistrySortColumn current, bool descending)
    {
        return column == current ? descending ? "Sorted descending" : "Sorted ascending" : "Not sorted";
    }

    internal void SetListColumns(IReadOnlyList<RegistrySortColumn> columns)
    {
        if (this._columns.SequenceEqual(columns))
        {
            return;
        }

        this._metadata.Cancel();
        this._columns = columns;
        this.ApplyNotes();
        if (!columns.Contains(this.SortColumn))
        {
            this.SortColumn = columns[0];
            this.SortDescending = false;
            this.ApplyFilter(true);
        }

        _ = this.LoadSortDetailsAsync();
    }

    internal async Task LoadVisibleDetailsAsync(IEnumerable<RegistryEntry> rows)
    {
        if (this._closed || this.SortColumn.NeedsKeyDetails() || !this._columns.Any(RegistryColumns.NeedsKeyDetails))
        {
            return;
        }

        try
        {
            await this._metadata.LoadAsync(rows, this._columns.Contains(RegistrySortColumn.LinkTarget));
        }
        catch (Exception error)
        {
            this.DetailsLoadFailed?.Invoke(error);
        }
    }

    private async Task LoadSortDetailsAsync()
    {
        if (!this._closed
            && this.SortColumn.NeedsKeyDetails()
            && (this.Location.IsComputer || this.ShowKeysInList)
            && await this._metadata.LoadAsync(this.Entries, this._columns.Contains(RegistrySortColumn.LinkTarget)))
        {
            this.ApplyFilter(true);
        }
    }

    private void NoteSaved(KeyNote note)
    {
        if (!this._columns.Contains(RegistrySortColumn.Notes))
        {
            return;
        }

        var entry = this._allEntries.FirstOrDefault(row => row.Key?.IsSameLocation(note.Location) == true);
        if (entry is null)
        {
            return;
        }

        entry.SetNote(string.IsNullOrWhiteSpace(note.Text) ? string.Empty : note.Text);
        if (this.SortColumn == RegistrySortColumn.Notes)
        {
            this.ApplyFilter(true);
        }
    }

    private void ApplyNotes()
    {
        if (this._settings is null || !this._columns.Contains(RegistrySortColumn.Notes))
        {
            return;
        }

        var notes = this._settings.Notes.Where(note => note.Location.View == this.Location.View).ToLookup(
            note => note.Location.Address, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in this._allEntries)
        {
            if (entry.Key is { } key)
            {
                entry.SetNote(notes.Count == 0 ? string.Empty : notes[key.Address].FirstOrDefault()?.Text ?? string.Empty);
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SortAsync(RegistrySortColumn column)
    {
        this._metadata.Cancel();
        this.SortDescending = this.SortColumn == column && !this.SortDescending;
        this.SortColumn = column;
        this.ApplyFilter(true);
        await this.LoadSortDetailsAsync();
    }

    private void ApplyFilter(bool preserveSelection = false)
    {
        this._filterCancellation?.Cancel();
        this.PublishRows(this.OrderedRows().ToList(), preserveSelection);
    }

    private IEnumerable<RegistryEntry> OrderedRows(CancellationToken token = default)
    {
        var text = this.Filter;
        var includeKeys = this.Location.IsComputer || this.ShowKeysInList;
        var visible = this._allEntries.Where(item =>
        {
            token.ThrowIfCancellationRequested();
            return (includeKeys || !item.IsKey)
                   && (text.Length == 0
                       || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                       || item.Type.Contains(text, StringComparison.OrdinalIgnoreCase)
                       || item.Data.Contains(text, StringComparison.OrdinalIgnoreCase));
        });
        var sorted = RegistryColumns.Sort(visible, this.SortColumn, this.SortDescending);
        if (this.ShowKeysInList && this.ShowParentKeyInList && this.Location.GetNavigationParent() is { } parent)
        {
            if (this._parentEntry is null || this._parentEntry.ParentLocation != parent)
            {
                this._parentEntry = new RegistryEntry(null, null, ParentLocation: parent);
            }

            return sorted.Prepend(this._parentEntry);
        }

        return sorted;
    }

    private void PublishRows(List<RegistryEntry> entries, bool preserveSelection)
    {
        var selection = preserveSelection ? this.SelectedEntries : [];
        if (!preserveSelection || !entries.SequenceEqual(this.Entries))
        {
            if (this.HasSelection)
            {
                this.SelectedEntry = null;
            }

            this.Entries = new ObservableCollection<RegistryEntry>(entries);
            if (selection.Count > 0)
            {
                this.SetSelection(entries.Intersect(selection));
            }
        }

        this.UpdateStatus();
    }

    private void UpdateStatus()
    {
        this.OnPropertyChanged(nameof(this.VisibleEntryCount));
        var keys = this._allEntries.Count(item => item.IsKey);
        var values = this._allEntries.Count(item => !item.IsKey && !item.IsDefaultPlaceholder);
        this.Status = this.Location.IsComputer
            ? $"{keys:N0} {(keys == 1 ? "hive" : "hives")}"
            : $"{keys:N0} {(keys == 1 ? "key" : "keys")} · {values:N0} {(values == 1 ? "value" : "values")}";
        if (!string.IsNullOrEmpty(this.Filter))
        {
            this.Status += $" · {this.Entries.Count(item => !item.IsParent):N0} shown";
        }
    }
}
