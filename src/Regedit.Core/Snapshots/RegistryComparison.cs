// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Snapshots;

public sealed class RegistryComparison
{
    public RegistryBranchSnapshot Before { get; }

    public RegistryBranchSnapshot After { get; }

    public IReadOnlyList<RegistryDifference> Differences { get; }

    public RegistryComparison(RegistryBranchSnapshot before, RegistryBranchSnapshot after, CancellationToken token = default)
    {
        before.Validate();
        after.Validate();
        ValidateRoots(before, after);

        this.Before = before;
        this.After = after;
        var oldKeys = before.Keys.ToDictionary(key => key.Path, StringComparer.OrdinalIgnoreCase);
        var newKeys = after.Keys.ToDictionary(key => key.Path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<RegistryDifference>();
        foreach (var path in oldKeys.Keys.Union(newKeys.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            oldKeys.TryGetValue(path, out var oldKey);
            newKeys.TryGetValue(path, out var newKey);
            var location = before.Root with
            {
                SubKey = string.Join('\\', new[] { before.Root.SubKey, path }.Where(part => part.Length > 0))
            };
            if (oldKey is null)
            {
                changes.Add(new RegistryDifference(location, RegistryChangeKind.KeyAdded));
            }

            if (newKey is null)
            {
                changes.Add(new RegistryDifference(location, RegistryChangeKind.KeyRemoved));
            }

            var oldValues = (oldKey?.Values ?? []).ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
            var newValues = (newKey?.Values ?? []).ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in oldValues.Keys.Union(newValues.Keys, StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                oldValues.TryGetValue(name, out var oldValue);
                newValues.TryGetValue(name, out var newValue);
                if (oldValue?.SameData(newValue) == true)
                {
                    continue;
                }

                changes.Add(
                    new RegistryDifference(
                        location,
                        oldValue is null ? RegistryChangeKind.ValueAdded :
                        newValue is null ? RegistryChangeKind.ValueRemoved : RegistryChangeKind.ValueChanged, oldValue, newValue));
            }
        }

        this.Differences = changes;
    }

    internal static void ValidateRoots(RegistryBranchSnapshot before, RegistryBranchSnapshot after)
    {
        if (before.Root.Hive != after.Root.Hive
            || before.Root.View != after.Root.View
            || !before.Computer.Equals(after.Computer, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.Root.Machine, after.Root.Machine, StringComparison.OrdinalIgnoreCase)
            || !before.Root.SubKey.Equals(after.Root.SubKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Compare snapshots of the same branch, computer, and registry view.");
        }
    }

    public HashSet<RegistryDifference> ValidateSelection(IEnumerable<RegistryDifference> selection)
    {
        var selected = selection.Distinct().ToHashSet();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Select differences to export.");
        }

        if (selected.Any(change => !this.Differences.Contains(change)))
        {
            throw new ArgumentException("The selection belongs to another comparison.");
        }

        var deletedKeys = selected.Where(change => change.Kind == RegistryChangeKind.KeyRemoved).ToArray();
        foreach (var deleted in deletedKeys)
        {
            if (this.Differences.Any(change => IsWithin(change.Location, deleted.Location) && !selected.Contains(change)))
            {
                throw new InvalidOperationException(
                    $"Select every change under {deleted.Location.Address} before exporting its deletion. A .reg key deletion removes the entire subtree.");
            }

            if (deleted.Location.Parent is null)
            {
                throw new InvalidOperationException("A root hive cannot be deleted.");
            }
        }

        return selected;
    }

    public async Task ExportAsync(IEnumerable<RegistryDifference> selection, string file, CancellationToken token = default)
    {
        var selected = this.ValidateSelection(selection);
        var deletedKeys = selected.Where(change => change.Kind == RegistryChangeKind.KeyRemoved).ToArray();
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var writer = new StreamWriter(temporary, false, Encoding.Unicode))
            {
                await writer.WriteLineAsync("Windows Registry Editor Version 5.00\r\n");
                await writer.WriteLineAsync($"; Source: {this.Before.Root.Address.Replace('\r', ' ').Replace('\n', ' ')}");
                await writer.WriteLineAsync(
                    $"; Registry view: {(this.Before.Root.View == RegistryView.Registry32 ? "32" : "64")}-bit. Import using reg.exe import FILE /reg:{(this.Before.Root.View == RegistryView.Registry32 ? "32" : "64")}");
                await writer.WriteLineAsync("; Applies the selected after-state. Review deletions before importing.\r\n");
                foreach (var change in this.Differences.Where(selected.Contains))
                {
                    token.ThrowIfCancellationRequested();
                    RegistryExport.ValidatePath(change.Location.Path);
                    if (deletedKeys.Any(deleted => deleted != change && IsWithin(change.Location, deleted.Location)))
                    {
                        continue;
                    }

                    if (change.Kind == RegistryChangeKind.KeyRemoved)
                    {
                        await writer.WriteLineAsync($"[-{change.Location.Path}]\r\n");
                    }
                    else
                    {
                        await writer.WriteLineAsync($"[{change.Location.Path}]");
                        if (change.Kind == RegistryChangeKind.ValueRemoved)
                        {
                            await writer.WriteLineAsync($"{RegistryExport.ValueName(change.Before!.Name)}=-");
                        }
                        else if (change.After is { } value)
                        {
                            await RegistryExport.WriteValueAsync(writer, value, token);
                        }

                        await writer.WriteLineAsync();
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            File.Move(temporary, file, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsWithin(RegistryLocation location, RegistryLocation root)
    {
        return location.SubKey.Equals(root.SubKey, StringComparison.OrdinalIgnoreCase)
               || location.SubKey.StartsWith(root.SubKey + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
