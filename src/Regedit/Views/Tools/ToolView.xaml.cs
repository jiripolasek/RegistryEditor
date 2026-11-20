// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Tools;

public sealed partial class ToolView : UserControl, IToolSurface, IPanelView
{
    private readonly MainWindow _window;
    private readonly string _watchErrorKey = "watch-error-" + Guid.NewGuid().ToString("N");
    private readonly bool _compact;
    private bool _loaded, _refreshing, _busy;
    private bool _focusOnLoad, _focusSelection;
    private bool _showingJournalError;
    private bool _detailsExpanded, _detailsCollapsed;
    private bool _docked;
    private readonly List<ToolCommand> _commands = [];
    private readonly List<(ToolCommand Command, Func<bool> Enabled)> _conditionalCommands = [];
    private readonly List<RelayCommand> _runCommands = [];
    private NotificationHandle? _watchErrorNotice;
    private ToolCommand? _inspectCommand, _revertCommand;
    private ToolCommand? _filterCommand;
    private ScrollViewer? _watchScroll;
    private bool _followingWatch;

    internal WatchSurfaceViewModel? WatchSurface { get; }

    internal ToolResultActionsViewModel ResultActions { get; }

    internal NotesViewModel? Notes { get; }

    public ToolKind Tool { get; }

    public Button MenuButton => this.PageHeader.MenuButton;

    internal RegistryLocation? NoteLocation => this.Notes?.Selected?.Location;

    private BulkObservableCollection<InvestigationRow> Rows { get; } = [];

    internal JournalViewModel JournalHistory => this._window.JournalHistory;

    internal JournalSurfaceViewModel? JournalSurface { get; }

    private InvestigationRow[] Selected => this.Results.SelectedItems.OfType<InvestigationRow>().ToArray();

    public static Visibility TextVisibility(string text)
    {
        return string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static Visibility ValueVisibility(RegistryValue? value)
    {
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public ToolView(MainWindow window, ToolKind tool, bool compact = false)
    {
        this._window = window;
        this.Tool = tool;
        this._compact = compact;
        if (tool == ToolKind.Journal)
        {
            this.JournalSurface = new JournalSurfaceViewModel(
                window.JournalHistory, () => window.ViewModel.ContextLocation);
            this.JournalSurface.PropertyChanged += this.JournalSurface_Changed;
            this.JournalSurface.EntryRequested += this.JournalEntry_Requested;
        }

        if (tool == ToolKind.Watches)
        {
            this.WatchSurface = window.CreateWatchSurface();
            this.WatchSurface.TargetRequested += this.RequestWatchAsync;
            this.WatchSurface.ActionFailed += (operation, error) =>
                window.Logger.LogFailure(LogLevel.Warning, operation, error);
        }

        if (tool == ToolKind.Notes)
        {
            this.Notes = new NotesViewModel(
                window.ViewModel.NoteStore, window.ViewModel.NavigateFromToolsAsync,
                location => window.ViewModel.OpenBrowser(location, select: false), compact, ClipboardText.Set,
                note => new RegistryDialogs(window).ConfirmAsync(
                    "Delete note?", "Delete the note for " + ToolPresentation.Address(note.Location) + "?",
                    "Delete note"));
            this.Notes.ActionFailed
                += (operation, error) => window.Logger.LogFailure(LogLevel.Warning, operation, error);
            this.Notes.PropertyChanged += this.Notes_Changed;
            this.Notes.TargetRequested += this.RequestNoteAsync;
        }

        this.ResultActions = new ToolResultActionsViewModel(ClipboardText.Set);
        this.ResultActions.Notice += (title, message, kind) =>
        {
            if (!window.IsClosed)
            {
                StatusNotifications.Show(window.Notifications, title, message, kind);
            }
        };
        this.ResultActions.ActionFailed += (operation, error) =>
            window.Logger.LogFailure(LogLevel.Warning, operation, error);

        this.InitializeComponent();
        this.LocalControls.PresentationChanged
            += (_, _) => this.ResultColumns.ShowColumns = this.LocalControls.ShowColumns;
        this.InitializeResults(window, "Tool." + tool);
        if (tool is ToolKind.Journal or ToolKind.Watches)
        {
            ListContextMenu.Attach(this.Results, this.CreateResultMenu);
        }

        if (tool == ToolKind.Notes)
        {
            ListContextMenu.Attach(this.Results, this.CreateNoteMenu);
        }

        this.ApplyListSpacing(window.Settings.ListRowPadding);
        this.Details.InspectValueRequested += this.InspectValue;
        this.Details.CopyBytesRequested += value => this.ResultActions.CopyExactBytesCommand.Execute(value);
        this.WatchViewing.Visibility = this.PanelWatchViewing.Visibility
            = tool == ToolKind.Watches ? Visibility.Visible : Visibility.Collapsed;
        this.PanelJournalFilters.Visibility = tool == ToolKind.Journal ? Visibility.Visible : Visibility.Collapsed;
        this.PanelWatchPicker.Visibility = tool == ToolKind.Watches ? Visibility.Visible : Visibility.Collapsed;
        if (tool != ToolKind.Journal)
        {
            this.Details.RelatedContent = null;
        }

        this.MenuButton.Click += this._window.ApplicationMenu_Click;
        // Set the source before handoff selection; a Loading-time binding would reset the grouped view.
        this.Results.ItemsSource = this.Rows;
        this.PageHeader.Title = tool.Choice().Title;
        this.PageHeader.Description = tool switch
        {
            ToolKind.Journal => window.Journal.AdministratorOnly
                ? "Changes made while running as administrator"
                : "Changes made without administrator privileges",
            ToolKind.Watches => "Local watches. Rapid writes may coalesce; the writer is not identified.",
            _ => "Local notes for registry locations"
        };
        this.PageHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (!compact)
        {
            this.PageHeader.SetAction(
                "Show in panel", "ShowInPanelButton", _ => this._window.ShowToolInPanel(this),
                tool is ToolKind.Journal or ToolKind.Watches ? Symbol.DockBottom : Symbol.DockRight);
        }

        this.Layout.Padding = new Thickness(compact ? 0 : 24);
        this.LocalControls.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (!compact)
        {
            this.Layout.Style = (Style)Application.Current.Resources["ToolPageSurfaceStyle"];
        }

        this.NoteTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        this.LocalControls.SearchPlaceholder = tool == ToolKind.Watches
            ? "Search observations…"
            : "Search " + tool.Choice().Label.ToLowerInvariant() + "…";
        this.ScopePanel.Visibility = tool == ToolKind.Journal ? Visibility.Visible : Visibility.Collapsed;
        if (tool != ToolKind.Journal)
        {
            this.LocalControls.FiltersContent = null;
        }

        this.WatchTarget.Visibility = tool == ToolKind.Watches ? Visibility.Visible : Visibility.Collapsed;
        if (tool == ToolKind.Journal)
        {
            Grid.SetColumn(this.CaptureStatus, 0);
            Grid.SetColumnSpan(this.CaptureStatus, 2);
        }

        if (tool != ToolKind.Watches)
        {
            this.WatchContext.SecondaryContent = null;
        }

        if (tool == ToolKind.Notes)
        {
            this.Search.PlaceholderText = "Find a note by path or text";
            this.Results.SelectionMode = ListViewSelectionMode.Single;
            this.Collection.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            this.NotePanel.Visibility = Visibility.Visible;
            if (compact)
            {
                this.NotePanel.Style = null;
            }

            this.Count.Visibility = Visibility.Collapsed;
            MiddleClick.Attach(
                this.OpenNoteButton, _ => this.Notes?.Selected,
                note => this.Notes?.OpenBackgroundCommand.Execute(note));
        }

        this.BuildCommands();
        this.PanelSearch = new PanelSearch(
            "Search tool items", null, this.LocalControls.SearchPlaceholder, null, () => this.Search.Text,
            text => this.Search.Text = text);
        MiddleClick.Attach(
            this.Results, args => !this._busy &&
                                  MiddleClick.Container<ListViewItem>(this.Results, args)?.Content is InvestigationRow
                                  {
                                      Source: NoteDraft note
                                  }
                ? note
                : null, note => this.Notes?.OpenBackgroundCommand.Execute(note));

        this.RefreshCommands();
    }

    private void BuildCommands()
    {
        if (this.Tool is ToolKind.Journal or ToolKind.Watches || this.Tool == ToolKind.Notes && !this._compact)
        {
            var inspect = new ToolCommand("Show details", Symbol.View)
            {
                AutomationId = "InspectDetails", IsToggle = true, ShortcutText = "Alt+P"
            };
            inspect.Command = new RelayCommand(() => this.SetDetailsExpanded(inspect.IsChecked));
            this._inspectCommand = inspect;
            this.LocalControls.SetPreviewCommand(inspect);
            this._commands.Add(inspect);
        }

        if (this.Tool is ToolKind.Journal or ToolKind.Watches)
        {
            this._filterCommand = new ToolCommand("Filter and scope", Symbol.Filter)
            {
                Flyout = this.PanelFilterFlyout
            };
        }

        switch (this.Tool)
        {
            case ToolKind.Journal:
                this.BuildJournalCommands();
                break;
            case ToolKind.Watches:
                this.BuildWatchCommands();
                break;
            case ToolKind.Notes:
                this.BuildNoteCommands();
                break;
        }

        this.Commands.SetCommands(this._commands.Where(command => command != this._inspectCommand));
    }

    private void BuildJournalCommands()
    {
        var journal = this.JournalSurface!;
        this._revertCommand = new ToolCommand("Revert edit…", Symbol.Undo)
        {
            AutomationId = "JournalRevert", Command = this.JournalHistory.RevertCommand
        };
        var archive = new ToolCommand(journal.ArchiveAction, Symbol.MoveToFolder)
        {
            AutomationId = "JournalArchive",
            Command = journal.ToggleArchiveCommand,
            ToolTip = "Hide or unarchive this entry while retaining its recorded changes."
        };
        journal.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(JournalSurfaceViewModel.ArchiveAction))
            {
                archive.Label = journal.ArchiveAction;
            }
        };
        this._commands.Add(this._revertCommand);
        this._commands.Add(archive);
        this._commands.Add(
            new ToolCommand("Archive all edits", Symbol.MoveToFolder, ToolCommandPlacement.Overflow)
            {
                AutomationId = "JournalArchiveAll",
                Command = this.JournalHistory.ArchiveAllCommand,
                ToolTip
                    = "Archive all active entries across all locations, regardless of filters. Recorded changes are retained."
            });
        this.AddScriptCommand();
        this.AddRunCommand(
            new ToolCommand("Refresh", Symbol.Refresh, ToolCommandPlacement.Overflow),
            this._window.JournalHistory.RefreshAsync);
    }

    private void BuildWatchCommands()
    {
        var watches = this.WatchSurface!;
        watches.NewCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IAsyncRelayCommand.IsRunning) && !watches.NewCommand.IsRunning)
            {
                // Refresh the capture after the target dialog has finished closing.
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (this._loaded)
                    {
                        this.SelectWatch(watches.Selected);
                    }
                });
            }
        };
        var pause = new ToolCommand(watches.PauseLabel, watches.IsViewingPaused ? Symbol.Play : Symbol.Pause)
        {
            Command = watches.PauseViewingCommand,
            ToolTip = "Freeze this view while capture continues. Resume to show pending observations."
        };
        watches.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WatchSurfaceViewModel.PauseLabel))
            {
                pause.Label = watches.PauseLabel;
                pause.Symbol = watches.IsViewingPaused ? Symbol.Play : Symbol.Pause;
            }
        };
        this._commands.Add(
            new ToolCommand("New watch…", Symbol.Add)
            {
                AutomationId = "NewWatchButton", Command = watches.NewCommand
            });
        var stop = new ToolCommand("Stop watching", Symbol.Stop) { Command = watches.StopCommand };
        this._commands.Add(stop);
        this.AddScriptCommand();
        var restart = new ToolCommand("Watch again", Symbol.Refresh)
        {
            Command = watches.RestartCommand,
            ToolTip = "Start a fresh session and baseline. The stopped session and its observations are retained."
        };
        this._commands.Add(restart);

        void UpdateCaptureCommands()
        {
            stop.IsVisible = pause.IsVisible = watches.IsCapturing;
            restart.IsVisible = watches.HasCapture && !watches.IsCapturing;
        }

        watches.PropertyChanged += (_, _) => UpdateCaptureCommands();
        UpdateCaptureCommands();
        this._commands.Add(
            new ToolCommand("Remove stopped watch", Symbol.Delete, ToolCommandPlacement.Overflow)
            {
                Command = watches.RemoveCommand
            });
        this._commands.Add(
            new ToolCommand("Clear observations", Symbol.Clear, ToolCommandPlacement.Overflow)
            {
                Command = watches.ClearCommand
            });
        this._commands.Add(pause);
        this._commands.Add(
            new ToolCommand("Open key", Symbol.OpenFile, ToolCommandPlacement.Overflow)
            {
                Command = watches.OpenCommand,
                Attach = button => MiddleClick.Attach(
                    button, _ => watches.Selected, _ => watches.OpenBackgroundCommand.Execute(null))
            });
    }

    private void BuildNoteCommands()
    {
        var notes = this.Notes!;
        if (!this._compact)
        {
            this._commands.Add(new ToolCommand("New note…", Symbol.Add) { Command = notes.NewCommand });
        }

        var current = new ToolCommand("Note for current key", Symbol.Edit)
        {
            IsVisible = this._compact && notes.ShowUseCurrentKey,
            IconSource = new FontIconSource
            {
                FontFamily = new FontFamily(ToolPresentation.FluentSystemIconsFontFamily), Glyph = "\uF56D"
            },
            Command = notes.UseCurrentKeyCommand,
            ToolTip = notes.CurrentKeyHelp,
            Attach = element =>
            {
                if (element is ButtonBase button)
                {
                    button.Click += this.CurrentNote_Click;
                }
            }
        };
        notes.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NotesViewModel.CurrentKeyHelp))
            {
                current.ToolTip = notes.CurrentKeyHelp;
            }
            else if (args.PropertyName == nameof(NotesViewModel.ShowUseCurrentKey))
            {
                current.IsVisible = this._compact && notes.ShowUseCurrentKey;
            }
        };
        this._commands.Add(current);
    }

    private void AddRunCommand(ToolCommand command, Func<Task> action)
    {
        var relay = new RelayCommand(() => _ = this.RunAsync(action, command.Label), () => !this._busy);
        command.Command = relay;
        this._runCommands.Add(relay);
        this._commands.Add(command);
    }

    public string PanelCommandsId => "Commands";

    public IReadOnlyList<ToolCommand> PanelCommands => this.Tool == ToolKind.Notes && this._compact ? this._commands :
        this._filterCommand is null ? [.. this._commands, this.LocalControls.ViewCommand] :
        [this._filterCommand, .. this._commands, this.LocalControls.ViewCommand];

    public PanelSearch? PanelSearch { get; private set; }

    // The inner list of the column control; selection, menus, density and scrolling helpers act on it.
    private ListView Results => this.ResultColumns.ListView;

    private TextBox Search => this.LocalControls.SearchBox;

    private async Task<WatchDefinition?> RequestWatchAsync()
    {
        var target = new RegistryTargetViewModel(true);
        var form = new RegistryTargetForm(target);
        WatchDefinition? definition = null;
        var accepted = await new RegistryDialogs(this._window).ShowFormAsync(
            "New watch", form, "Start watch", () =>
            {
                definition = target.Watch();
                return Task.CompletedTask;
            }, initialFocus: form.InitialFocus);
        return accepted ? definition : null;
    }

    private async Task<RegistryLocation?> RequestNoteAsync()
    {
        var target = new RegistryTargetViewModel(false);
        var form = new RegistryTargetForm(target);
        RegistryLocation? location = null;
        var accepted = await new RegistryDialogs(this._window).ShowFormAsync(
            "New note", form, "Create note", () =>
            {
                location = target.Location();
                return Task.CompletedTask;
            }, initialFocus: form.InitialFocus);
        return accepted ? location : null;
    }

    private void InitializeResults(MainWindow window, string listId)
    {
        if (this.Tool == ToolKind.Notes)
        {
            var columns = this.ResultColumns.Columns;
            columns.First(column => column.Id == "Subject").Header = "Key";
            columns.First(column => column.Id == "Summary").Header = "Note";
            columns.Remove(columns.First(column => column.Id == "Time"));
        }
        else
        {
            this.ResultColumns.Columns.Move(3, 2);
        }

        var list = this.Results;
        AutomationProperties.SetName(list, "Tool items");
        list.ItemContainerStyle = (Style)Application.Current.Resources["ToolListItemStyle"];
        list.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)this.Resources["ToolGroupHeader"] });
        list.DoubleTapped += this.Results_DoubleTapped;
        list.PreviewKeyDown += this.Results_KeyDown;
        list.SelectionChanged += this.Selection_Changed;
        this.ResultColumns.ItemAutomationName = item => (item as InvestigationRow)?.AccessibleName;
        window.ColumnLayouts.Attach(this.ResultColumns, listId);
    }

    public void SetDocked(bool docked)
    {
        if (this._docked == docked)
        {
            return;
        }

        this._docked = docked;
        this.Layout.Style = docked || this._compact
            ? null
            : (Style)Application.Current.Resources["ToolPageSurfaceStyle"];
        this.PageHeader.Visibility = docked || this._compact ? Visibility.Collapsed : Visibility.Visible;
        this.Count.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        this.ResultColumns.ShowColumns = this.LocalControls.ShowColumns;
        if (!docked && this.JournalSurface is { } journal)
        {
            journal.ScopeIndex = 0;
        }

        // The panel header renders search, filters and commands; the view's own controls stay in place.
        this.LocalControls.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        this.WatchContext.Visibility
            = docked || this.Context.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!docked)
        {
            this.PanelFilterFlyout.Hide();
        }

        this.RefreshRows();
    }

    internal void ApplyListSpacing(double spacing)
    {
        ListDensity.SetSpacing(this.Results, spacing);
    }

    private async Task RunAsync(Func<Task> action, [CallerMemberName] string operation = "")
    {
        if (this._busy)
        {
            return;
        }

        this._busy = true;
        this.Commands.IsEnabled = this.NoteCommands.IsEnabled = false;
        this.RefreshCommands();
        try
        {
            this.Notice.IsOpen = false;
            await action();
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                this._window.Logger.LogFailure(LogLevel.Warning, operation, ex);
            }

            if (!this._window.IsClosed)
            {
                StatusNotifications.Show(
                    this._window.Notifications, "Could not complete the action", ex.Message, NoticeKind.Error);
            }
        }
        finally
        {
            this._busy = false;
            this.Commands.IsEnabled = this.NoteCommands.IsEnabled = true;
            this.RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        if (this.Tool == ToolKind.Journal)
        {
            this.SyncResultSelection();
            var entry = this.Results.SelectedItems.Count == 1
                ? (this.Results.SelectedItem as InvestigationRow)?.Source as JournalEntry
                : null;
            this.JournalSurface!.SelectedEntry = entry;
            this._revertCommand!.CommandParameter = entry;
            this._revertCommand.ToolTip
                = this.JournalHistory.RevertUnavailableReason(entry) ?? "Revert the selected edit";
        }

        foreach (var (command, enabled) in this._conditionalCommands)
        {
            command.IsEnabled = !this._busy && enabled();
        }

        foreach (var relay in this._runCommands)
        {
            relay.NotifyCanExecuteChanged();
        }
    }

    public void RequestFocus(bool preferSelection = false)
    {
        this._focusOnLoad = true;
        this._focusSelection = preferSelection;
        if (this._loaded)
        {
            this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (this._loaded && this._focusOnLoad)
                    {
                        this.FocusPrimary();
                    }
                });
        }
    }

    private void FocusPrimary()
    {
        this._focusOnLoad = false;
        if (this.Tool == ToolKind.Notes && this.Notes?.Selected is not null)
        {
            this.NoteEditor.Focus(FocusState.Keyboard);
        }
        else if (this._focusSelection && this.Results.SelectedItem is { } selected)
        {
            this.Results.ScrollIntoView(selected);
            this.Results.Focus(FocusState.Keyboard);
        }
        else
        {
            this.Search.Focus(FocusState.Keyboard);
        }
    }

    private async void View_Loaded(object sender, RoutedEventArgs args)
    {
        if (this._loaded)
        {
            return;
        }

        this._loaded = true;
        this.Notes?.Activate();
        this._window.ViewModel.ToolsChanged += this.Tools_Changed;
        this._window.ContextChanged += this.Context_Changed;
        if (this.Tool == ToolKind.Journal)
        {
            this.JournalSurface!.Activate();
            this._window.JournalHistory.Changed += this.Journal_Changed;
            this._window.JournalHistory.EligibilityChanged += this.JournalEligibility_Changed;
        }

        if (this.WatchSurface is { } watches)
        {
            watches.PropertyChanged += this.WatchSurface_Changed;
            watches.VisibleObservations.CollectionChanged += this.Observations_Changed;
            this._refreshing = true;
            this.WatchPicker.ItemsSource = this.PanelWatchPicker.ItemsSource = this._window.ViewModel.Watches;
            this._refreshing = false;
            watches.Activate();
            this.SelectWatch(watches.Selected);
            this.UpdateWatchError();
        }

        this.Context_Changed(null, EventArgs.Empty);
        this.RefreshRows();
        if (this._focusOnLoad)
        {
            this.RequestFocus(this._focusSelection);
        }

        if (this.Tool == ToolKind.Journal)
        {
            await this._window.JournalHistory.RefreshAsync();
        }
    }

    private void View_Unloaded(object sender, RoutedEventArgs args)
    {
        this._loaded = false;
        this.Notes?.Deactivate();
        this._focusOnLoad = false;
        this._window.ViewModel.ToolsChanged -= this.Tools_Changed;
        this._window.ContextChanged -= this.Context_Changed;
        this._window.JournalHistory.Changed -= this.Journal_Changed;
        this._window.JournalHistory.EligibilityChanged -= this.JournalEligibility_Changed;
        this.JournalSurface?.Deactivate();
        this.ResultActions.ReleaseCache();
        if (this.WatchSurface is { } watches)
        {
            watches.Deactivate();
            watches.PropertyChanged -= this.WatchSurface_Changed;
            watches.VisibleObservations.CollectionChanged -= this.Observations_Changed;
        }

        if (this._watchScroll is not null)
        {
            this._watchScroll.ViewChanged -= this.WatchScroll_Changed;
        }

        this._watchScroll = null;
    }

    private void Tools_Changed(object? sender, EventArgs args)
    {
        if (this.Tool == ToolKind.Notes)
        {
            this.RefreshRows();
        }
    }

    private void Journal_Changed(object? sender, EventArgs args)
    {
        this.RefreshRows();
    }

    private void JournalEligibility_Changed(object? sender, EventArgs args)
    {
        this.RefreshCommands();
        this.UpdateDetails();
    }

    private void Context_Changed(object? sender, EventArgs args)
    {
        this.JournalSurface?.RefreshScope();
        if (this.Notes is { } notes)
        {
            notes.CurrentLocation = this._window.ViewModel.ContextLocation;
        }

        if (this.Tool != ToolKind.Watches)
        {
            this.RefreshRows();
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs args)
    {
        this.PanelSearch?.NotifyTextChanged();
        if (this.JournalSurface is { } surface)
        {
            surface.Query = this.Search.Text;
        }
        else if (this.WatchSurface is { } watches)
        {
            watches.Query = this.Search.Text;
        }
        else if (this._loaded)
        {
            this.RefreshRows();
        }
    }

    private void JournalSurface_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (this._loaded && !this._refreshing && this.JournalSurface is { IsRevealing: false } &&
            args.PropertyName is nameof(JournalSurfaceViewModel.Query) or nameof(JournalSurfaceViewModel.ScopeIndex)
                or nameof(JournalSurfaceViewModel.ArchiveFilter) or nameof(JournalSurfaceViewModel.OperationFilter))
        {
            this.RefreshRows();
        }
    }

    private void JournalEntry_Requested(JournalEntry entry)
    {
        this._refreshing = true;
        this.Search.Text = this.JournalSurface!.Query;
        this._refreshing = false;
        this.RefreshRows();
        this.Results.SelectedItem
            = this.Rows.FirstOrDefault(row => row.Source is JournalEntry target && target.Id == entry.Id);
        if (this.Results.SelectedItem is { } selected)
        {
            this.Results.ScrollIntoView(selected);
        }

        this.Results.Focus(FocusState.Programmatic);
    }

    private void RestoreSelection(InvestigationRow[] previous)
    {
        var sources = previous.Select(row => row.Source).ToHashSet(ReferenceEqualityComparer.Instance);
        var ids = sources.OfType<JournalEntry>().Select(entry => entry.Id).ToHashSet();
        var matches = this.Rows.Where(row =>
            sources.Contains(row.Source) || row.Source is JournalEntry entry && ids.Contains(entry.Id));
        if (this.Results.SelectionMode == ListViewSelectionMode.Single)
        {
            this.Results.SelectedItem = matches.FirstOrDefault();
            return;
        }

        this.Results.SelectedItems.Clear();
        foreach (var row in matches)
        {
            this.Results.SelectedItems.Add(row);
        }
    }

    private void RefreshRows()
    {
        if (this._refreshing)
        {
            return;
        }

        this._refreshing = true;
        try
        {
            var selected = this.Selected;
            var restoreViewport = ResultListViewport.Capture(this.Results, this.Rows);
            this.Rows.ReplaceAll(
                this.Tool switch
                {
                    ToolKind.Journal => this.JournalSurface!.VisibleRows,
                    ToolKind.Watches => this.WatchSurface!.VisibleObservations.Select(InvestigationRow.Observation),
                    ToolKind.Notes => this.Notes!.VisibleRows(this._window.ViewModel.Notes, this.Search.Text),
                    _ => []
                });

            if (this.Tool == ToolKind.Journal && !this._docked)
            {
                var groups = this.Rows.GroupBy(row => ((JournalEntry)row.Source).Time.LocalDateTime.Date)
                    .Select(group => new ToolRowGroup(
                        ToolPresentation.DayLabel(((JournalEntry)group.First().Source).Time, DateTime.Today), group))
                    .ToArray();
                this.Results.ItemsSource = new CollectionViewSource { Source = groups, IsSourceGrouped = true }.View;
            }
            else
            {
                this.Results.ItemsSource = this.Rows;
            }

            this.RestoreSelection(selected);
            (this.EmptyTitle.Text, this.EmptyDescription.Text) = ToolPresentation.EmptyState(
                this.Tool, !string.IsNullOrEmpty(this.Search.Text));
            if (this.WatchSurface is { } watch && this.Search.Text.Length == 0)
            {
                this.EmptyTitle.Text = !watch.HasCapture ? "No watches yet" :
                    watch.IsCapturing ? "Waiting for changes" : "No observations captured";
                this.EmptyDescription.Text = !watch.HasCapture
                    ?
                    "Choose New watch to capture an explicit local registry target."
                    : watch.IsCapturing
                        ? "Changes to this target will appear here."
                        : "This capture ended without observing a change.";
            }

            this.Empty.Visibility = this.Rows.Count == 0 && !(this.Tool == ToolKind.Notes && this._compact)
                ? Visibility.Visible
                : Visibility.Collapsed;
            this.ShowContext(
                this.Tool switch
                {
                    ToolKind.Journal => this.JournalSurface!.Context(this._window.Journal.AdministratorOnly),
                    ToolKind.Watches => this.WatchSurface!.CaptureStatus,
                    _ => string.Empty
                });
            if (this.Tool == ToolKind.Journal && this._window.JournalHistory.Error is { } error)
            {
                this._showingJournalError = true;
                StatusNotice.Show(this.Notice, "Journal history incomplete", error, InfoBarSeverity.Error);
            }
            else if (this.Tool == ToolKind.Journal && this.JournalHistory.ArchiveError is { } archiveError)
            {
                this._showingJournalError = true;
                StatusNotice.Show(
                    this.Notice, "Journal archive state unavailable", archiveError, InfoBarSeverity.Warning);
            }
            else if (this._showingJournalError)
            {
                this._showingJournalError = false;
                this.Notice.IsOpen = false;
            }

            this.UpdateDetails();
            this.RefreshCommands();
            restoreViewport?.Invoke();
        }
        finally
        {
            this._refreshing = false;
        }
    }

    void IToolSurface.CopyContextFrom(IToolSurface surface)
    {
        var source = (ToolView)surface;
        this._refreshing = true;
        this.JournalSurface?.CopyFiltersFrom(source.JournalSurface!);
        if (!this._docked && this.JournalSurface is { } journal)
        {
            journal.ScopeIndex = 0;
        }

        this.LocalControls.ShowColumns = source.LocalControls.ShowColumns;
        this.Search.Text = source.Search.Text;
        this._refreshing = false;
        if (this.Notes is { } notes)
        {
            notes.Selected = source.Notes?.Selected;
        }

        if (this.WatchSurface is { } watches && source.WatchSurface is { } sourceWatches)
        {
            watches.CopyFrom(sourceWatches);
            this.SelectWatch(watches.Selected);
        }

        this.RefreshRows();
        this.RestoreSelection(source.Selected);
        this.UpdateDetails();
        this.RefreshCommands();
    }

    public void SelectWatch(WatchSession? watch)
    {
        if (this.WatchSurface is null)
        {
            return;
        }

        this._refreshing = true;
        this.WatchSurface.Selected = watch;
        this.WatchPicker.SelectedItem = this.PanelWatchPicker.SelectedItem = watch;
        this._refreshing = false;
        this.RefreshRows();
        this.UpdateWatchState();
    }

    private void Watch_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!this._refreshing)
        {
            this.SelectWatch(((ComboBox)sender).SelectedItem as WatchSession);
        }
    }

    private void UpdateWatchError()
    {
        if (this._window.IsClosed)
        {
            return;
        }

        this._watchErrorNotice = StatusNotifications.UpdateError(
            this._window.Notifications, this._watchErrorNotice, "Could not complete the watch action",
            this.WatchSurface?.Error, this._watchErrorKey);
    }

    private void WatchSurface_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (!this._loaded || this._refreshing || this.WatchSurface is not { } watches)
        {
            return;
        }

        if (args.PropertyName == nameof(WatchSurfaceViewModel.Selected))
        {
            this.SelectWatch(watches.Selected);
        }

        this.ShowContext(watches.CaptureStatus);
        this.UpdateWatchState();
        if (args.PropertyName is nameof(WatchSurfaceViewModel.IsFollowing)
            or nameof(WatchSurfaceViewModel.IsViewingPaused))
        {
            this.FollowLatestObservation();
        }

        if (args.PropertyName == nameof(WatchSurfaceViewModel.Error))
        {
            this.UpdateWatchError();
        }

        this.RefreshCommands();
    }

    private void UpdateWatchState()
    {
        if (this.WatchSurface is not { } watches)
        {
            return;
        }

        this.WatchTarget.Visibility = watches.HasCapture ? Visibility.Visible : Visibility.Collapsed;
        var showViewing = watches.HasCapture && watches.IsCapturing &&
                          (watches.IsViewingPaused || !watches.IsFollowing);
        this.WatchViewing.Visibility = this.PanelWatchViewing.Visibility
            = showViewing ? Visibility.Visible : Visibility.Collapsed;
        this.WatchContext.SecondaryContent = showViewing ? this.WatchViewing : null;
        this.CaptureIcon.Visibility = watches.HasCapture ? Visibility.Visible : Visibility.Collapsed;
        this.CaptureIcon.Glyph = watches.IsCapturing ? "\uE768" : "\uE71A";
        this.PanelWatchPicker.Visibility = watches.HasMultipleCaptures ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Observations_Changed(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (this._refreshing)
        {
            return;
        }

        if (args.Action != NotifyCollectionChangedAction.Add)
        {
            this.RefreshRows();
        }
        else
        {
            this.Rows.AddRange(args.NewItems!.OfType<WatchObservation>().Select(InvestigationRow.Observation));
            this.Empty.Visibility = this.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            this.UpdateCount();
            this.RefreshCommands();
        }

        this.FollowLatestObservation();
    }

    private void FollowLatestObservation()
    {
        if (!this._loaded || this.WatchSurface is not { IsFollowing: true, IsViewingPaused: false } ||
            this.Rows.Count == 0)
        {
            return;
        }

        this._followingWatch = true;
        this.Results.UpdateLayout();
        if (this._watchScroll is null && ResultListViewport.FindScrollViewer(this.Results) is { } scroll)
        {
            this._watchScroll = scroll;
            scroll.ViewChanged += this.WatchScroll_Changed;
        }

        this.Results.ScrollIntoView(this.Rows[^1]);
        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => this._followingWatch = false);
    }

    private void WatchScroll_Changed(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!this._followingWatch && this._loaded && this._watchScroll is { } scroll &&
            scroll.VerticalOffset < scroll.ScrollableHeight - 2 && this.WatchSurface is { } watches)
        {
            watches.IsFollowing = false;
        }
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (this._refreshing)
        {
            return;
        }

        this.RefreshCommands();
        if (this.WatchSurface is { } watches)
        {
            watches.SelectedObservations = this.Selected.Select(row => row.Source).OfType<WatchObservation>().ToArray();
            if (watches.SelectedObservations.Any(item => !ReferenceEquals(
                    item, watches.VisibleObservations.LastOrDefault())))
            {
                watches.IsFollowing = false;
            }
        }

        if (this.Notes is { } notes && this.Selected is [{ Source: NoteDraft note }])
        {
            notes.Selected = note;
        }

        this.UpdateDetails();
    }

    private void Results_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (this.Tool is ToolKind.Journal or ToolKind.Watches)
        {
            if (args.Key == VirtualKey.Enter && this.Selected.Length == 1)
            {
                args.Handled = true;
                this.SetDetailsExpanded(true);
            }
            else if (args.Key == VirtualKey.Escape && this._detailsExpanded)
            {
                args.Handled = true;
                this.SetDetailsExpanded(false);
            }
        }

        if (this.WatchSurface is { } watches)
        {
            if (args.Key is VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home)
            {
                watches.IsFollowing = false;
            }
            else if (args.Key == VirtualKey.End)
            {
                watches.FollowLatestCommand.Execute(null);
            }
        }

        if (this.Tool == ToolKind.Notes && args.Key == VirtualKey.Enter && this.Selected.Length == 1)
        {
            args.Handled = true;
            this.NoteEditor.Focus(FocusState.Keyboard);
        }
        else if (this.Notes is { } notes && args.Key == VirtualKey.Delete && this.Selected.Length == 1)
        {
            args.Handled = true;
            notes.DeleteCommand.Execute(null);
        }
    }

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if ((this.Tool is ToolKind.Journal or ToolKind.Watches && this.Selected.Length > 0) ||
            (this.Tool == ToolKind.Notes && this.Notes?.HasNote == true))
        {
            this.SetDetailsExpanded(true);
        }
        else if (this.Tool == ToolKind.Notes && this.Selected.Length == 1)
        {
            this.NoteEditor.Focus(FocusState.Keyboard);
        }
    }

    private void ShowContext(string text)
    {
        this.Context.Text = this.PanelContext.Text = text;
        ToolTipService.SetToolTip(this.Context, text);
        this.Context.Visibility = this.PanelContext.Visibility = TextVisibility(text);
        this.WatchContext.Visibility = this._docked || text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InspectDetails_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.Tool is ToolKind.Journal or ToolKind.Watches && this.Selected.Length == 1)
        {
            args.Handled = true;
            this.SetDetailsExpanded(this._inspectCommand?.IsChecked != true);
        }
    }

    private void SetDetailsExpanded(bool expanded)
    {
        this._detailsExpanded = expanded;
        this._detailsCollapsed = !expanded;
        this.UpdateDetails();
        if (expanded)
        {
            if (this.Tool == ToolKind.Notes)
            {
                this.NoteEditor.Focus(FocusState.Keyboard);
            }
            else
            {
                this.Details.FocusComparison();
            }
        }
        else
        {
            this.Results.Focus(FocusState.Keyboard);
        }
    }

    private async void InspectValue(RegistryValue value)
    {
        var dialog = new AppDialog(
            "Value inspector", new ValueInspector(new ValueInspectorViewModel(value)), "Close", string.Empty);
        await this._window.Dialogs.ShowAsync(dialog);
    }

    private void Notes_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NotesViewModel.Selected) && this._loaded)
        {
            this.RefreshRows();
            this._refreshing = true;
            try
            {
                this.Results.SelectedItem
                    = this.Rows.FirstOrDefault(row => ReferenceEquals(row.Source, this.Notes?.Selected));
            }
            finally
            {
                this._refreshing = false;
            }

            if (this.Notes?.NewCommand.IsRunning == true)
            {
                this.RequestFocus();
            }
        }

        if (sender is NotesViewModel notes && args.PropertyName == nameof(NotesViewModel.Error))
        {
            if (notes.Error is { } error)
            {
                StatusNotice.Show(this.Notice, "Could not complete the action", error, InfoBarSeverity.Error);
            }
            else
            {
                this.Notice.IsOpen = false;
            }
        }
    }

    private void SaveNote_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.Notes is { } notes)
        {
            args.Handled = true;
            notes.SaveCommand.Execute(null);
        }
    }

    private void CurrentNote_Click(object sender, RoutedEventArgs args)
    {
        this.RequestFocus();
    }

    // Hands the selection to the actions view model in display order, which script replay relies on.
    private void SyncResultSelection()
    {
        var selected = this.Results.SelectedItems.OfType<InvestigationRow>().ToHashSet();
        this.ResultActions.Select(selected.Count == 0 ? [] : this.Rows.Where(selected.Contains).ToArray());
    }

    private void AddScriptCommand()
    {
        // This flyout outlives selection changes, and command-bound items in a closed flyout keep the
        // CanExecute state they were created with. The parent button is the gate (HasChanges below),
        // so the items stay enabled and run the commands on click.
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var (text, command) in new (string, IRelayCommand)[]
                 {
                     ("Copy PowerShell", this.ResultActions.CopyPowerShellCommand),
                     ("Copy reg.exe (.cmd)", this.ResultActions.CopyRegExeCommand)
                 })
        {
            var item = new MenuFlyoutItem
            {
                Text = text, Icon = new FontIcon { Glyph = SymbolGlyphs.Get(Symbol.Copy) }
            };
            item.Click += (_, _) =>
            {
                this.SyncResultSelection();
                command.Execute(null);
            };
            menu.Items.Add(item);
        }

        var copy = new ToolCommand("Copy script", Symbol.Copy) { Flyout = menu };
        this._commands.Add(copy);
        this._conditionalCommands.Add((copy, () => this.ResultActions.HasChanges));
    }
}
