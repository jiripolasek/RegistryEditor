// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Watching;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Workspaces;

public sealed record RegistryWorkspace(string Name, RegistryLocation[] Tabs, SavedSearch[] Searches, WatchDefinition[] Watches)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(this.Name)
            || this.Tabs is null
            || this.Searches is null
            || this.Watches is null
            || this.Tabs.Length + this.Searches.Length + this.Watches.Length is < 1 or > 64)
        {
            throw new ArgumentException("A workspace needs a name and between 1 and 64 registry tabs, searches, or watches.");
        }

        foreach (var search in this.Searches)
        {
            RegistrySearch.Validate(search);
        }

        foreach (var location in this.Tabs)
        {
            ValidateLocation(location, true);
        }

        foreach (var watch in this.Watches)
        {
            if (watch is null)
            {
                throw new ArgumentException("A saved watch is missing.");
            }

            ValidateLocation(watch.Location);
            if (watch.Location.Machine is not null)
            {
                throw new ArgumentException("Workspaces support local watches only.");
            }

            if (watch.ValueName is not null)
            {
                RegistryService.ValidateValueName(watch.ValueName);
            }
        }
    }

    public static void ValidateLocation(RegistryLocation location, bool allowComputer = false)
    {
        if (location is null
            || location.SubKey is null
            || location.View is not (RegistryView.Registry32 or RegistryView.Registry64)
            || (location.IsComputer
                ? !allowComputer || location.SubKey.Length != 0
                : !RegistryLocation.Roots.Any(root => root.Hive == location.Hive)))
        {
            throw new ArgumentException("Invalid saved registry location.");
        }

        if (location.Machine is { } machine
            && (machine.Length == 0
                || machine.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '\0')
                || !location.IsComputer && location.Hive is not (RegistryHive.LocalMachine or RegistryHive.Users)))
        {
            throw new ArgumentException("Invalid saved registry location.");
        }

        if (location.SubKey.Length > 0)
        {
            foreach (var name in location.SubKey.Split('\\'))
            {
                RegistryLocation.ValidateKeyName(name);
            }
        }
    }
}
