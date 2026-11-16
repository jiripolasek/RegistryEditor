// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Services;

internal static class RegistryWebSearch
{
    internal const string Bing = "https://www.bing.com/search?q={query}";
    internal const string Google = "https://www.google.com/search?q={query}";
    internal const string DuckDuckGo = "https://duckduckgo.com/?q={query}";

    internal static bool IsValidTemplate(string? template)
    {
        return template is not null && template.Contains("{query}", StringComparison.Ordinal) &&
               Uri.TryCreate(template.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
               uri.UserInfo.Length == 0 && uri.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped)
                   .Contains("{query}", StringComparison.Ordinal);
    }

    internal static string ValidateTemplate(string? template)
    {
        if (!IsValidTemplate(template))
        {
            throw new ArgumentException(
                "Enter an HTTP or HTTPS search URL containing {query} in its path or query string.");
        }

        return template!.Trim();
    }

    internal static Uri CreateUri(string template, RegistryLocation location, string? valueName = null)
    {
        template = ValidateTemplate(template);
        if (location.IsComputer)
        {
            throw new ArgumentException("Select a registry hive, key, or value to search the web.");
        }

        var searchLocation = location with { Machine = null };
        if (location.Hive == RegistryHive.Users &&
            location.SubKey.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
        {
            var separator = location.SubKey.IndexOf('\\');
            searchLocation = searchLocation with
            {
                SubKey = "<SID>" + (separator < 0 ? string.Empty : location.SubKey[separator..])
            };
        }

        var path = searchLocation.Address;
        if (valueName is not null)
        {
            path += "\\" + (valueName.Length == 0 ? "(Default)" : valueName);
        }

        // Canonicalization can remove escaped trailing spaces that belong to the registry name.
        return new Uri(
            template.Replace("{query}", Uri.EscapeDataString(path), StringComparison.Ordinal),
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }
}
