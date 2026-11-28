// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Omnibox;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.ViewModels.Browser;

/// <summary>Known destinations, local path completions and breadcrumb children for one browser tab.</summary>
internal sealed class RegistryAddressSuggestions(
    Func<RegistryLocation, string[]> subKeys, IReadOnlyList<Favorite> favorites, NavigationHistory history)
{
    internal const int SuggestionLimit = 30;
    private const int StartingItemsPerSource = 5;
    private const string FolderGlyph = "\uE8B7";
    private const string HistoryGlyph = "\uE81C";
    private const string FavoriteGlyph = "\uE734";
    private const string FavoritesGroup = "Favorites";
    private const string HistoryGroup = "History";
    private const string HivesGroup = "Hives";

    /// <summary>Children of <paramref name="address"/>, or of the current computer when it is null.</summary>
    internal async Task<IReadOnlyList<OmniboxItem>> ChildrenAsync(
        string? address,
        RegistryLocation current,
        CancellationToken cancellationToken)
    {
        var parent = address is null ? current.GetComputer() : RegistryLocation.Parse(address, current.View);
        if (parent.IsComputer)
        {
            return parent.GetHives().Select(hive => new OmniboxItem(hive.Name, hive.Address)).ToArray();
        }

        return await Task.Run(
            () => subKeys(parent).Select(name =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new OmniboxItem(name, parent.Child(name).Address);
            }).ToArray(), cancellationToken);
    }

    /// <summary>
    /// Matches bare text against known destinations and completes the last segment of a local path.
    /// </summary>
    internal async Task<IReadOnlyList<OmniboxItem>> SuggestAsync(string text, RegistryLocation current, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return this.StartingSuggestions(current, cancellationToken);
        }

        text = text.TrimStart();
        var computerPrefix = text.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase);
        if (computerPrefix)
        {
            text = text[9..];
        }

        var slash = text.LastIndexOf('\\');
        if (slash < 0)
        {
            var computer = computerPrefix ? new RegistryLocation(View: current.View) : current;
            var roots = RegistryLocation.Roots.Where(root => root.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                || root.Alias.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            var hives = roots.Join(computer.GetHives(), root => root.Hive, hive => hive.Hive,
                (root, hive) => (Location: hive, root.Alias)).ToArray();
            if (text.Length == 0 || computerPrefix)
            {
                return hives.Select(hive => AddressItem(hive.Location.Name, hive.Location.Address)).ToArray();
            }

            // Snapshot the tab's mutable collections before ranking them on a worker.
            var destinations = favorites.ToArray().Select(favorite => (favorite.GetLocation(), favorite.Name, (string?)favorite.Id))
                .Concat(history.GetNearbyLocations().ToArray().Select(visit => (visit, visit.Name, (string?)null)));
            return await Task.Run(() => MatchDestinations(text, current, hives, destinations, cancellationToken), cancellationToken);
        }

        var parent = RegistryLocation.Parse(text[..slash], current.View);
        if (parent.Machine is not null)
        {
            return [];
        }

        var segment = text[(slash + 1)..];
        return await Task.Run(() =>
        {
            var names = subKeys(parent);
            cancellationToken.ThrowIfCancellationRequested();
            return names.Where(name => name.Contains(segment, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(name => name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(name => name.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(SuggestionLimit)
                .Select(name => AddressItem(name, parent.Child(name).Address))
                .ToArray();
        }, cancellationToken);
    }

    private OmniboxItem[] StartingSuggestions(RegistryLocation current, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Address };
        bool Include(RegistryLocation location)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CanSuggest(location, current) && seen.Add(location.Address);
        }

        var rows = history.GetNearbyLocations().Where(Include).Take(StartingItemsPerSource)
            .Select(location => DestinationItem(location, HistoryGroup)).ToList();
        var visitCount = rows.Count;
        var unmatchedVisits = rows.Select((row, index) => (row.Address, Index: index))
            .ToDictionary(visit => visit.Address, visit => visit.Index, StringComparer.OrdinalIgnoreCase);
        var remainingFavorites = StartingItemsPerSource;
        foreach (var favorite in favorites)
        {
            if (remainingFavorites == 0 && unmatchedVisits.Count == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var location = favorite.GetLocation();
            if (!CanSuggest(location, current))
            {
                continue;
            }

            if (unmatchedVisits.Remove(location.Address, out var index))
            {
                rows[index] = rows[index] with
                {
                    Label = favorite.Name, IconGlyph = FavoriteGlyph, Group = FavoritesGroup, ActionsKey = favorite.Id
                };
            }
            else if (remainingFavorites > 0 && seen.Add(location.Address))
            {
                rows.Add(DestinationItem(location, FavoritesGroup, favorite.Name, favorite.Id));
                remainingFavorites--;
            }
        }

        var personal = rows.Take(visitCount).OrderByDescending(row => row.Group == FavoritesGroup)
            .Concat(rows.Skip(visitCount));
        var hives = current.GetHives().Where(Include).Select(hive => DestinationItem(hive, HivesGroup));
        return PromoteSuggestions(personal).Concat(hives).ToArray();
    }

    private static IEnumerable<OmniboxItem> PromoteSuggestions(IEnumerable<OmniboxItem> ranked) =>
        ranked.Select((item, index) => index < 2 ? item with { Group = "Suggestions" } : item);

    private static OmniboxItem DestinationItem(RegistryLocation location, string group, string? label = null, string? favoriteId = null)
    {
        return new OmniboxItem(label ?? location.Name, location.Address)
        {
            Group = group,
            ActionsKey = favoriteId,
            IconGlyph = group switch { FavoritesGroup => FavoriteGlyph, HistoryGroup => HistoryGlyph, _ => FolderGlyph },
            RightText = location.Hive is { } hive && location.SubKey.Length == 0
                ? RegistryLocation.Roots.First(root => root.Hive == hive).Alias
                : null
        };
    }

    private static bool CanSuggest(RegistryLocation location, RegistryLocation current)
    {
        return location.View == current.View && string.Equals(location.Machine, current.Machine, StringComparison.OrdinalIgnoreCase)
            && location.IsSameLocation(RegistryLocation.Parse(location.Address, current.View));
    }

    private static IReadOnlyList<OmniboxItem> MatchDestinations(
        string query, RegistryLocation current, (RegistryLocation Location, string Alias)[] hives,
        IEnumerable<(RegistryLocation Location, string Label, string? FavoriteId)> destinations, CancellationToken cancellationToken)
    {
        var matches = hives.Select((hive, index) => (
            Rank: MatchRank(query, hive.Alias, hive.Location.Name, hive.Location.Address),
            HiveOrder: index, hive.Location, Label: hive.Location.Name, Group: HivesGroup, FavoriteId: (string?)null))
            .ToDictionary(match => match.Location.Address, StringComparer.OrdinalIgnoreCase);
        foreach (var (location, label, favoriteId) in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = location.Address;
            var rank = MatchRank(query, label, location.Name, address);
            if (rank < 0 || !CanSuggest(location, current))
            {
                continue;
            }

            // At equal rank keep the hive's identity, then the first saved name, then history.
            if (!matches.TryGetValue(address, out var previous) || rank < previous.Rank)
            {
                matches[address] = (rank, hives.Length, location, label, favoriteId is null ? HistoryGroup : FavoritesGroup, favoriteId);
            }
        }

        var ranked = matches.OrderBy(match => match.Value.Rank)
            .ThenBy(match => match.Value.HiveOrder)
            .ThenBy(match => match.Value.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Key, StringComparer.OrdinalIgnoreCase)
            .Take(SuggestionLimit)
            .Select(match => DestinationItem(match.Value.Location, match.Value.Group, match.Value.Label, match.Value.FavoriteId));
        return PromoteSuggestions(ranked).ToArray();
    }

    private static int MatchRank(string query, string label, string leaf, string address)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (label.Equals(query, comparison) || leaf.Equals(query, comparison))
        {
            return 0;
        }

        if (label.StartsWith(query, comparison) || leaf.StartsWith(query, comparison))
        {
            return 1;
        }

        if (label.Contains(query, comparison) || leaf.Contains(query, comparison))
        {
            return 2;
        }

        return address.Contains(query, comparison) ? 3 : -1;
    }

    private static OmniboxItem AddressItem(string label, string address) => new(label, address) { IconGlyph = FolderGlyph };
}
