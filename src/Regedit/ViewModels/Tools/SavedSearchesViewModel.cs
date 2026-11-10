// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class SavedSearchesViewModel : InvestigationViewModel
{
    private readonly InvestigationStore _store;
    private readonly Action<SavedSearch> _run;

    internal SavedSearchesViewModel(InvestigationStore store, Action<SavedSearch> run)
        : base(
            "Saved searches", "Select a search to run it in a new results tab or remove it.", "No saved searches",
            "Save a search from its results tab to run it again later.")
    {
        this._store = store;
        this._run = run;
        this.Refresh();
        store.SearchesChanged += this.Refresh;
    }

    internal override IRelayCommand ActivateCommand => this.RunCommand;

    private void Refresh()
    {
        this.ReplaceNamedRows(
            this._store.Searches.Select(search => new InvestigationRow(
                "Saved search", search.Name, string.Join(", ", search.Roots.Select(root => root.Address)),
                search.Options.Text, string.Empty, search.Options.Text, search)));
    }

    protected override void SelectionChanged()
    {
        base.SelectionChanged();
        this.RemoveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSingle))]
    private void Run()
    {
        if (this.Selected is [{ Source: SavedSearch search }])
        {
            this._run(search);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        try
        {
            if (this.HasSelection)
            {
                this._store.RemoveSearches(this.Selected.Select(row => (SavedSearch)row.Source).ToHashSet());
            }
        }
        catch (Exception ex)
        {
            this.Report("Could not remove saved searches", ex.Message, NoticeKind.Error);
        }
    }

    internal override void Close()
    {
        this._store.SearchesChanged -= this.Refresh;
        base.Close();
    }
}
