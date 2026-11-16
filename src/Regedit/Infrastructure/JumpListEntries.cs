// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.Infrastructure;

internal sealed record JumpListEntry(string Title, string Arguments, string? FavoriteId = null);

internal static class JumpListEntries
{
    internal const string AdministratorArgument = "--jump-list-admin";

    internal static bool IsAdministratorTask(IReadOnlyList<string> arguments)
    {
        return arguments.Count == 1 && arguments[0] == AdministratorArgument;
    }

    internal static JumpListEntry[] Create(IEnumerable<Favorite> favorites, IReadOnlySet<string> hiddenFavorites)
    {
        return new[]
        {
            new JumpListEntry("New window", "Computer"),
            new JumpListEntry("New administrator window", AdministratorArgument)
        }.Concat(
            favorites.Where(favorite => !hiddenFavorites.Contains(favorite.Id)).Select(favorite => new JumpListEntry(
                favorite.Name + (favorite.Is32Bit ? " (32-bit)" : string.Empty),
                RegistryLaunch.FormatArguments(favorite.Is32Bit ? (string[])[favorite.Address, "--32"] : (string[])[favorite.Address]),
                favorite.Id))).ToArray();
    }

    internal static HashSet<string> RemovedFavoriteIds(
        IEnumerable<JumpListEntry> entries,
        IReadOnlySet<string> arguments)
    {
        return entries.Where(entry => entry.FavoriteId is not null && arguments.Contains(entry.Arguments))
            .Select(entry => entry.FavoriteId!).ToHashSet(StringComparer.Ordinal);
    }
}
