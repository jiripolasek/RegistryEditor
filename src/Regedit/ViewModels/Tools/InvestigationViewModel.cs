// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Shell;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal abstract partial class InvestigationViewModel : WindowTab
{
    internal event Action<string, string, NoticeKind>? Notice;

    internal event Func<string, Task<string?>>? NameRequested;

    internal event Action? RowsReplacing;

    internal event Action<IReadOnlyList<InvestigationRow>>? SelectionRestored;

    /// <summary>Writes text to the clipboard; the view supplies the platform clipboard.</summary>
    internal event Action<string>? CopyTextRequested;

    public BulkObservableCollection<InvestigationRow> Rows { get; } = [];

    private BulkObservableCollection<InvestigationRow> FilteredRows { get; } = [];

    public virtual BulkObservableCollection<InvestigationRow> DisplayRows =>
        this.Query.Length == 0 ? this.Rows : this.FilteredRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayEmptyTitle), nameof(DisplayEmptyDescription))]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; protected set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayEmptyTitle))]
    public partial string EmptyTitle { get; protected set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayEmptyDescription))]
    public partial string EmptyDescription { get; protected set; }

    public virtual string DisplayEmptyTitle => this.Query.Length == 0 ? this.EmptyTitle : "No matching items";

    public virtual string DisplayEmptyDescription =>
        this.Query.Length == 0 ? this.EmptyDescription : "Try another name, registry path or text.";

    public bool IsEmpty => this.DisplayRows.Count == 0;

    public string CountText => $"{this.DisplayRows.Count:N0} {(this.DisplayRows.Count == 1 ? "item" : "items")}"
                               + (this.Selected.Count > 0 ? $" · {this.Selected.Count:N0} selected" : string.Empty);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial IReadOnlyList<InvestigationRow> Selected { get; set; } = [];

    internal abstract IRelayCommand ActivateCommand { get; }

    protected bool HasSingle => this.Selected.Count == 1;

    protected bool HasSelection => this.Selected.Count > 0;

    protected bool IsClosed { get; private set; }

    protected InvestigationViewModel(string title, string description, string emptyTitle, string emptyDescription)
        : base(title, "\uE721")
    {
        this.Description = description;
        this.EmptyTitle = emptyTitle;
        this.EmptyDescription = emptyDescription;
        ((INotifyPropertyChanged)this.Rows).PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != "Item[]")
            {
                return;
            }

            if (this.Query.Length > 0)
            {
                this.RefreshFilter();
            }

            this.OnPropertyChanged(nameof(this.IsEmpty));
            this.OnPropertyChanged(nameof(this.CountText));
        };
    }

    partial void OnQueryChanged(string value)
    {
        this.UpdateDisplayedRows(() =>
        {
            this.RefreshFilter();
            this.OnPropertyChanged(nameof(this.DisplayRows));
        });
    }

    private void RefreshFilter()
    {
        this.FilteredRows.ReplaceAll(
            this.Rows.Where(row =>
                (row.Title + " " + row.Path + " " + row.Subtitle).Contains(this.Query, StringComparison.OrdinalIgnoreCase)));
    }

    partial void OnSelectedChanged(IReadOnlyList<InvestigationRow> value)
    {
        this.SelectionChanged();
    }

    protected virtual void SelectionChanged()
    {
        this.ActivateCommand.NotifyCanExecuteChanged();
        this.CopyPathsCommand.NotifyCanExecuteChanged();
        this.CopyResultsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPaths()
    {
        this.CopyText(ResultText.Paths(this.Selected), null, null, "Could not copy results");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyResults()
    {
        this.CopyText(ResultText.Table(this.Selected, row => row.Title), null, null, "Could not copy results");
    }

    [RelayCommand]
    private void CopyExactBytes(RegistryValue value)
    {
        this.CopyText(ResultText.Hex(value), "Exact bytes copied", ResultText.BytesCopied(value), "Could not copy exact bytes");
    }

    protected void CopyText(string text, string? successTitle, string? successMessage, string failureTitle)
    {
        try
        {
            this.CopyTextRequested?.Invoke(text);
            if (successTitle is not null)
            {
                this.Report(successTitle, successMessage ?? string.Empty, NoticeKind.Success);
            }
        }
        catch (Exception error)
        {
            this.Report(failureTitle, error.Message, NoticeKind.Error);
        }
    }

    protected void ReplaceNamedRows(IEnumerable<InvestigationRow> rows)
    {
        var replacement = rows.ToArray();
        var selectedNames = this.Selected.Select(row => row.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        this.RowsReplacing?.Invoke();
        this.Rows.ReplaceAll(replacement);
        this.Selected = selectedNames.Count == 0 ? [] : this.DisplayRows.Where(row => selectedNames.Contains(row.Title)).ToArray();
        this.SelectionRestored?.Invoke(this.Selected);
    }

    protected void UpdateDisplayedRows(Action update)
    {
        var selected = this.Selected;
        this.RowsReplacing?.Invoke();
        update();
        if (selected.Count > 0)
        {
            var visible = this.DisplayRows.ToHashSet();
            this.Selected = selected.Where(visible.Contains).ToArray();
        }

        this.OnPropertyChanged(nameof(this.IsEmpty));
        this.OnPropertyChanged(nameof(this.CountText));
        this.SelectionRestored?.Invoke(this.Selected);
    }

    protected Task<string?> RequestNameAsync(string initial)
    {
        return this.NameRequested?.Invoke(initial) ?? Task.FromResult<string?>(null);
    }

    protected void Report(string title, string message, NoticeKind kind = NoticeKind.Information)
    {
        if (!this.IsClosed)
        {
            this.Notice?.Invoke(title, message, kind);
        }
    }

    internal virtual void Close()
    {
        this.IsClosed = true;
    }
}
