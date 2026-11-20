// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Omnibox;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

/// <summary>Address bar completions and breadcrumb children, read from the registry off the UI thread.</summary>
internal sealed class RegistryAddressSuggestions(Func<RegistryLocation, string[]> subKeys)
{
    internal const int SuggestionLimit = 30;

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
    /// Completes the last segment of typed text. Roots match by name or alias; remote paths get no
    /// suggestions so typing never opens network connections.
    /// </summary>
    internal async Task<IReadOnlyList<OmniboxItem>> SuggestAsync(string text, RegistryView view, CancellationToken cancellationToken)
    {
        text = text.TrimStart();
        if (text.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase))
        {
            text = text[9..];
        }

        var slash = text.LastIndexOf('\\');
        if (slash < 0)
        {
            return RegistryLocation.Roots.Where(root =>
                    root.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                    || root.Alias.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .Select(root => new OmniboxItem(root.Name, root.Name))
                .ToArray();
        }

        var parent = RegistryLocation.Parse(text[..slash], view);
        if (parent.Machine is not null)
        {
            return [];
        }

        var names = await Task.Run(() => subKeys(parent), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = text[(slash + 1)..];
        return names.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(SuggestionLimit)
            .Select(name => new OmniboxItem(name, parent.Child(name).Address)).ToArray();
    }
}
