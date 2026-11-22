// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class InvestigationPage : Page, IPanelView
{
    private readonly WindowDialogPresenter _dialogs;
    private readonly ToolCommand _inspectCommand;
    private readonly List<ToolCommand> _itemCommands = [];
    private NotificationHandle? _notification;
    private bool _detailsExpanded, _detailsCollapsed;
    private bool _docked;
    private bool _restoringSelection;

    internal InvestigationViewModel ViewModel { get; }

    internal SearchResultsViewModel? Search => this.ViewModel as SearchResultsViewModel;

    internal WorkspacesViewModel? Workspaces => this.ViewModel as WorkspacesViewModel;

    internal SavedSearchesViewModel? Saved => this.ViewModel as SavedSearchesViewModel;

    internal bool IsSearch => this.Search is not null;

    internal bool IsWorkspace => this.Workspaces is not null;

    internal bool IsSaved => this.Saved is not null;

    internal Button MenuButton => this.PageHeader.MenuButton;

    // The inner list of the column control; selection, menus, density and scrolling helpers act on it.
    private ListView Results => this.ResultColumns.ListView;

    private bool IsWide => !this._docked && this.Layout.ActualWidth - this.Layout.Padding.Left - this.Layout.Padding.Right >= 1000;

    internal InvestigationPage(
        InvestigationViewModel viewModel,
        WindowDialogPresenter dialogs,
        INotificationService notifications,
        double listSpacing,
        ColumnLayoutPersistence? layouts = null)
    {
        this.ViewModel = viewModel;
        this._dialogs = dialogs;
        this.InitializeComponent();
        this.LocalControls.PresentationChanged += (_, _) => this.ResultColumns.ShowColumns = this.LocalControls.ShowColumns;
        this.InitializeResults(layouts);
        if (this.IsSearch)
        {
            ListContextMenu.Attach(this.Results, this.CreateResultMenu);
        }

        this.SearchContext.Visibility = this.IsSearch ? Visibility.Visible : Visibility.Collapsed;
        this.Count.Visibility = this.IsSearch ? Visibility.Collapsed : Visibility.Visible;
        this.ApplyListSpacing(listSpacing);
        var inspect = new ToolCommand(
            "Show details", Symbol.View, this.IsSearch ? ToolCommandPlacement.Overflow : ToolCommandPlacement.Primary)
        {
            AutomationId = "InspectDetails", IsToggle = true, ShortcutText = "Alt+P"
        };
        inspect.Command = new RelayCommand(() =>
        {
            this._detailsExpanded = inspect.IsChecked;
            this._detailsCollapsed = !inspect.IsChecked;
            this.ShowSelection(this.ViewModel.Selected);
        });
        this._inspectCommand = inspect;
        this.LocalControls.SetPreviewCommand(inspect);
        this.BuildItemCommands();
        this.ItemCommands.SetCommands(this._itemCommands.Where(command => command != this._inspectCommand));
        var panelSearch = new PanelSearch(
            this.IsSearch ? "Filter search results" : "Filter tool items", this.IsSearch ? "SearchResultFilter" : "ToolSearch",
            this.IsSearch ? "Filter results…" : "Search " + viewModel.Title.ToLowerInvariant() + "…",
            "Filter by name, registry path or displayed data", () => this.Search?.Filter ?? viewModel.Query, text => this.SetFilter(text));
        this.PanelSearch = panelSearch;
        this.LocalControls.SearchPlaceholder = panelSearch.Placeholder;
        this.LocalControls.SearchName = this.IsSearch ? "Filter search results" : "Filter tool items";
        this.LocalControls.SearchText = this.Search?.Filter ?? viewModel.Query;
        AutomationProperties.SetAutomationId(this.LocalControls.SearchBox, this.IsSearch ? "SearchResultFilter" : "ToolSearch");
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SearchResultsViewModel.Filter) or nameof(InvestigationViewModel.Query))
            {
                this.LocalControls.SearchText = this.Search?.Filter ?? viewModel.Query;
                panelSearch.NotifyTextChanged();
            }
        };

        this.Details.InspectValueRequested += this.InspectValue;
        this.Details.CopyBytesRequested += value => viewModel.CopyExactBytesCommand.Execute(value);
        viewModel.CopyTextRequested += ClipboardText.Set;
        var notificationKey = "investigation-" + Guid.NewGuid().ToString("N");
        viewModel.Notice += (title, message, kind) =>
        {
            this._notification = StatusNotifications.Show(notifications, title, message, kind, key: notificationKey);
        };
        viewModel.NameRequested += this.RequestNameAsync;
        Action? restoreViewport = null;
        viewModel.RowsReplacing += () =>
        {
            restoreViewport = ResultListViewport.Capture(this.Results, viewModel.DisplayRows);
            this._restoringSelection = true;
        };
        viewModel.SelectionRestored += selected =>
        {
            try
            {
                this.ResultColumns.RestoreSelection(selected);
            }
            finally
            {
                this._restoringSelection = false;
            }

            this.ShowSelection(selected);
            restoreViewport?.Invoke();
            restoreViewport = null;
        };
        if (this.Workspaces is { } workspaces)
        {
            workspaces.PreviewRequested += this.PreviewWorkspaceAsync;
            workspaces.ConfirmRemote += this.ConfirmRemoteAsync;
        }

        if (this.Search is { } search)
        {
            search.RunStarted += this.DismissNotification;
            search.ResultSelected += row =>
            {
                this._restoringSelection = true;
                try
                {
                    this.ResultColumns.RestoreSelection((object[])[row]);
                }
                finally
                {
                    this._restoringSelection = false;
                }

                this.ShowSelection(search.Selected);
                this.Results.ScrollIntoView(row);
            };
            this.Results.Loaded += (_, _) =>
            {
                if (search.Selected is [var row])
                {
                    this.Results.ScrollIntoView(row);
                }
            };
            MiddleClick.Attach(
                this.Results, args => MiddleClick.Container<ListViewItem>(this.Results, args)?.Content as InvestigationRow,
                row => search.OpenInBackgroundCommand.Execute(row));
        }
    }

    public string PanelCommandsId => "ItemCommands";

    public IReadOnlyList<ToolCommand> PanelCommands =>
        (ToolCommand[])[.. this._itemCommands, this.LocalControls.ViewCommand];

    public PanelSearch? PanelSearch { get; }

    public void SetDocked(bool docked)
    {
        this._docked = docked;
        this.Layout.Style = docked ? null : (Style)Application.Current.Resources["ToolPageSurfaceStyle"];
        this.PageHeader.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        this.Count.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        this.ResultColumns.ShowColumns = this.LocalControls.ShowColumns;
        this.Collection.RowSpacing = docked ? 0 : 8;
        this.SearchContext.Margin = docked ? new Thickness(12, 0, 12, 0) : new Thickness(0);

        // The panel header renders the filter and item commands; the page keeps its own bar in place.
        this.LocalControls.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        this.ShowSelection(this.ViewModel.Selected);
    }

    private void InitializeResults(ColumnLayoutPersistence? layouts)
    {
        if (!this.IsSearch)
        {
            var declared = this.ResultColumns.Columns.ToArray();
            declared[0].Header = "Name";
            declared[1].Header = this.IsWorkspace ? "Contents" : "Registry scope";
            declared[2].Header = "Find";
            this.ResultColumns.Columns.Clear();
            foreach (var column in this.IsWorkspace ? declared.Take(2) : declared)
            {
                this.ResultColumns.Columns.Add(column);
            }
        }

        var list = this.Results;
        AutomationProperties.SetName(list, this.IsSearch ? "Investigation results" : "Investigation items");
        list.ItemContainerStyle = (Style)Application.Current.Resources["ToolListItemStyle"];
        list.DoubleTapped += this.Results_DoubleTapped;
        list.PreviewKeyDown += this.Results_KeyDown;
        list.SelectionChanged += this.Selection_Changed;
        this.ResultColumns.ItemAutomationName = item => (item as InvestigationRow)?.AccessibleName;
        if (layouts is not null)
        {
            var listId = this.IsSearch ? "SearchResults" : this.IsWorkspace ? "Workspaces" : "SavedSearches";
            layouts.Attach(this.ResultColumns, listId);
        }
    }

    private void BuildItemCommands()
    {
        if (this.Search is { } search)
        {
            var run = new ToolCommand("Run search", Symbol.Refresh) { Command = search.RunCommand };
            var cancel = new ToolCommand("Cancel search", Symbol.Stop) { Command = search.RunCancelCommand };

            void UpdateRunState()
            {
                run.IsVisible = search.IsIdle;
                cancel.IsVisible = search.IsBusy;
            }

            UpdateRunState();
            search.PropertyChanged += (_, _) => UpdateRunState();
            this._itemCommands.Add(run);
            this._itemCommands.Add(cancel);
            this._itemCommands.Add(
                new ToolCommand("Open result", Symbol.Forward)
                {
                    AutomationName = "Open selected result",
                    ToolTip = "Open selected result (Enter)",
                    ShortcutText = "Enter",
                    Command = search.OpenCommand,
                    Attach = button => MiddleClick.Attach(
                        button, _ => this.ViewModel.Selected is [var row] ? row : null,
                        row => search.OpenInBackgroundCommand.Execute(row))
                });
            this._itemCommands.Add(
                new ToolCommand("Open in new tab", Symbol.NewWindow, ToolCommandPlacement.Overflow)
                {
                    IconSource = (IconSource)Application.Current.Resources["OpenInNewTabIcon"],
                    Command = search.OpenNewTabCommand,
                    Attach = button => MiddleClick.Attach(
                        button, _ => this.ViewModel.Selected is [var row] ? row : null,
                        row => search.OpenInBackgroundCommand.Execute(row))
                });
            this._itemCommands.Add(this._inspectCommand);
            this._itemCommands.Add(
                new ToolCommand("Search scope", Symbol.Filter, ToolCommandPlacement.Overflow) { Flyout = this.SearchScopeFlyout });
            this._itemCommands.Add(
                new ToolCommand("Copy query", Symbol.Copy, ToolCommandPlacement.Overflow) { Command = search.CopyQueryCommand });
            this._itemCommands.Add(
                new ToolCommand("Save search", Symbol.Save, ToolCommandPlacement.Overflow) { Command = search.SaveCommand });
            return;
        }

        this._itemCommands.Add(this._inspectCommand);
        if (this.Saved is { } saved)
        {
            this._itemCommands.Add(new ToolCommand("Run search", Symbol.Find) { Command = saved.RunCommand });
            this._itemCommands.Add(new ToolCommand("Remove selected", Symbol.Delete) { Command = saved.RemoveCommand });
        }
        else if (this.Workspaces is { } workspaces)
        {
            this._itemCommands.Add(new ToolCommand("Save workspace…", Symbol.Save) { Command = workspaces.SaveCommand });
            this._itemCommands.Add(new ToolCommand("Open workspace", Symbol.OpenFile) { Command = workspaces.OpenCommand });
            this._itemCommands.Add(new ToolCommand("Remove selected", Symbol.Delete) { Command = workspaces.RemoveCommand });
        }
    }

    private void SetFilter(string text)
    {
        if (this.Search is { } search)
        {
            search.Filter = text;
        }
        else
        {
            this.ViewModel.Query = text;
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs args)
    {
        this.SetFilter(this.LocalControls.SearchBox.Text);
    }

    private async Task<string?> RequestNameAsync(string initial)
    {
        var form = new NameForm(this.IsSearch ? "Search name" : "Workspace name", initial);
        var dialog = new AppDialog(this.IsSearch ? "Save search" : "Save current workspace", form, "Save");
        dialog.FocusOnOpen(form.Input);
        return await this._dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? form.Value : null;
    }

    private async Task<WorkspaceRestoreOptions?> PreviewWorkspaceAsync(RegistryWorkspace workspace)
    {
        var form = new WorkspaceRestoreForm(workspace);
        var dialog = new AppDialog("Open workspace", form, "Add workspace");
        form.ReplaceTabs.Checked += (_, _) => dialog.PrimaryButtonText = "Replace tabs";
        form.ReplaceTabs.Unchecked += (_, _) => dialog.PrimaryButtonText = "Add workspace";
        return await this._dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? form.Options : null;
    }

    private async Task<bool> ConfirmRemoteAsync(string[] computers)
    {
        var dialog = new AppDialog(
            "Connect to workspace computers?",
            "Opening this workspace uses your Windows credentials to read registries on: " + string.Join(", ", computers),
            "Connect and open", defaultButton: ContentDialogButton.Close);
        return await this._dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private void Results_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && this.ViewModel.ActivateCommand.CanExecute(null))
        {
            args.Handled = true;
            this.ViewModel.ActivateCommand.Execute(null);
        }
    }

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (this.ViewModel.Selected.Count == 1 && this.ViewModel.ActivateCommand.CanExecute(null))
        {
            args.Handled = true;
            this.ViewModel.ActivateCommand.Execute(null);
        }
    }

    internal void SetToolsAction(Action<Button> action)
    {
        this.PageHeader.SetAction(
            "Tools", "ToolsButton", action,
            new FontIcon
            {
                FontFamily = new FontFamily(ToolPresentation.FluentSystemIconsFontFamily),
                Glyph = "\uEE7F",
                FontSize = 16,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
    }

    internal void SetDockAction(Action action)
    {
        this.PageHeader.SetAction("Show in panel", "ShowInPanelButton", _ => action(), Symbol.DockBottom);
    }

    internal void ApplyListSpacing(double spacing)
    {
        ListDensity.SetSpacing(this.Results, spacing);
    }

    internal void DismissNotification()
    {
        this._notification?.Dismiss();
        this._notification = null;
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!this._restoringSelection)
        {
            this.ViewModel.Selected = this.Results.SelectedItems.OfType<InvestigationRow>().ToArray();
            this.ShowSelection(this.ViewModel.Selected);
        }
    }

    private void SearchScope_Click(object sender, RoutedEventArgs args)
    {
        FlyoutBase.ShowAttachedFlyout(this.Collection);
    }

    private void ShowSelection(IReadOnlyList<InvestigationRow> selected)
    {
        var wide = this.IsWide;
        var visible = selected.Count > 0 && (wide ? !this._detailsCollapsed : this._detailsExpanded);
        this.Details.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        this._inspectCommand.IsEnabled = selected.Count > 0;
        this._inspectCommand.IsChecked = visible;
        this._inspectCommand.Label = visible ? "Hide details" : "Show details";
        if (selected.Count > 1)
        {
            this.Details.Show($"{selected.Count:N0} items selected", string.Empty, "Select one item to inspect its details.");
        }
        else if (selected is [var row])
        {
            if (row.Source is RegistryWorkspace workspace)
            {
                this.Details.Show(row.Title, string.Empty, row.Path, ToolPresentation.WorkspaceComparisons(workspace));
            }
            else if (row.Source is SavedSearch search)
            {
                this.Details.Show(
                    search.Name, string.Join("\n", search.Roots.Select(ToolPresentation.Address)), string.Empty, (ToolComparison[])
                    [
                        new ToolComparison(
                            string.Empty, SearchQueryText.Format(search.Options), ToolPresentation.SearchOptionsText(search.Options), "Query", "Match options")
                    ]);
            }
            else
            {
                var comparison = row.Source switch
                {
                    RegistrySearchMatch match => new ToolComparison(
                        string.Empty, row.Before, row.After, match.Value is null ? "Key" : "Value", "Match", AfterValue: match.Value),
                    RegistryDifference difference => ToolPresentation.Compare(difference) with { Name = string.Empty },
                    _ => new ToolComparison(string.Empty, row.Before, row.After, "Details", "Contents")
                };
                this.Details.Show(row.Title, row.Path, row.Subtitle, (ToolComparison[])[comparison]);
            }
        }

        this.UpdateLayoutForWidth();
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateLayoutForWidth();
        if (this.Details is not null)
        {
            this.ShowSelection(this.ViewModel.Selected);
        }
    }

    private void UpdateLayoutForWidth()
    {
        if (this.Details is null)
        {
            return;
        }

        this.Layout.Padding = this._docked ? new Thickness(0) : new Thickness(this.Layout.ActualWidth < 760 ? 16 : 24, 24, 12, 16);
        var beside = this._docked || this.IsWide;
        var visible = this.Details.Visibility == Visibility.Visible;
        this.InspectorWidth.Width = visible && beside ? new GridLength(285) : new GridLength(0);
        this.InspectorHeight.Height = visible && !beside ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(this.Details, beside ? 1 : 0);
        Grid.SetRow(this.Details, beside ? 0 : 1);
        Grid.SetColumnSpan(this.Details, beside ? 1 : 2);
        this.Workspace.ColumnSpacing = visible && beside ? 16 : 0;
        this.Workspace.RowSpacing = visible && !beside ? 12 : 0;
        this.DetailsSplitter.Arrange(
            this.Details, this.InspectorWidth, this.InspectorHeight, beside, visible,
            () => this.Collection.ActualWidth + this.InspectorWidth.ActualWidth);
    }

    private void InspectDetails_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.ViewModel.Selected.Count > 0)
        {
            args.Handled = true;
            this._detailsExpanded = this.Details.Visibility != Visibility.Visible;
            this._detailsCollapsed = !this._detailsExpanded;
            this.ShowSelection(this.ViewModel.Selected);
            (this._detailsExpanded ? (Control)this.Details : this.Results).Focus(FocusState.Keyboard);
        }
    }

    private async void InspectValue(RegistryValue value)
    {
        var dialog = new AppDialog("Value inspector", new ValueInspector(new ValueInspectorViewModel(value)), "Close", string.Empty);
        await this._dialogs.ShowAsync(dialog);
    }
}
