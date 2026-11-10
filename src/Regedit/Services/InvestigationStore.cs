// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Persistence;


namespace JPSoftworks.Regedit.Services;

internal sealed class InvestigationStore(AppSettings settings, Action save)
{
    internal IReadOnlyList<SavedSearch> Searches => settings.Searches;

    internal IReadOnlyList<RegistryWorkspace> Workspaces => settings.Workspaces;

    internal event Action? SearchesChanged;

    internal event Action? WorkspacesChanged;

    internal void SaveSearch(SavedSearch search)
    {
        RegistrySearch.Validate(search);
        this.Update(
            settings.Searches, item => item.Name.Equals(search.Name, StringComparison.OrdinalIgnoreCase), search,
            this.SearchesChanged);
    }

    internal void SaveWorkspace(RegistryWorkspace workspace)
    {
        workspace.Validate();
        this.Update(
            settings.Workspaces, item => item.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase),
            workspace, this.WorkspacesChanged);
    }

    internal void RemoveSearches(IReadOnlySet<SavedSearch> selected)
    {
        this.Update(settings.Searches, selected.Contains, null, this.SearchesChanged);
    }

    internal void RemoveWorkspaces(IReadOnlySet<RegistryWorkspace> selected)
    {
        this.Update(settings.Workspaces, selected.Contains, null, this.WorkspacesChanged);
    }

    private void Update<T>(List<T> items, Predicate<T> remove, T? add, Action? changed)
        where T : class
    {
        var previous = items.ToArray();
        items.RemoveAll(remove);
        if (add is not null)
        {
            items.Add(add);
        }

        try
        {
            save();
        }
        catch
        {
            items.Clear();
            items.AddRange(previous);
            throw;
        }

        changed?.Invoke();
    }
}
