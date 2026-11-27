// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class RegistryUsageTests
{
    internal static async Task RunAsync(Action<bool, string> check, Action<Action, string> canceled, bool useRegistry)
    {
        var registry = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.UsageTests." + Guid.NewGuid().ToString("N"), RegistryView.Registry32);
        canceled(() => registry.MeasureUsage(root, new CancellationToken(true)), "Usage cancels before opening its scope");
        var tab = new RegistryUsageTab(registry, root);
        check(tab.BackTarget is null && tab.ForwardTarget is null && tab.UpTarget is null, "Usage starts at its scope without prior visits");
        if (!useRegistry)
        {
            return;
        }

        registry.CreateKey(root.Parent!, root.Name);
        try
        {
            var small = root.Child("A small branch");
            var large = root.Child("Z large branch");
            var nested = large.Child(new string('k', 255));
            registry.CreateKey(root, small.Name);
            registry.CreateKey(root, large.Name);
            registry.CreateKey(large, nested.Name);
            registry.Write(root, new RegistryValue(string.Empty, RegistryValueType.Binary, []), null);
            registry.Write(root, new RegistryValue("Text", RegistryValueType.String, [65, 0, 0, 0]), null);
            registry.Write(small, new RegistryValue("One byte", RegistryValueType.Binary, [1]), null);
            registry.Write(large, new RegistryValue(new string('v', 16383), RegistryValueType.Binary, new byte[65536]), null);
            registry.Write(nested, new RegistryValue("Two bytes", RegistryValueType.Binary, [2, 3]), null);

            var report = registry.MeasureUsage(root);
            check(
                report.Total == new RegistryUsageEntry(root, 4, 5, 65543, 0),
                "Usage counts the selected key, descendants, default and empty values, and exact data bytes");
            check(
                report.Subkeys.SequenceEqual([new RegistryUsageEntry(large, 2, 2, 65538, 0), new RegistryUsageEntry(small, 1, 1, 1, 0)]),
                "Usage groups descendant counts under immediate subkeys and sorts by data size");
            var drilledDown = registry.MeasureUsage(large);
            check(
                drilledDown.Total == report.Subkeys[0] && drilledDown.Subkeys.Single() == new RegistryUsageEntry(nested, 1, 1, 2, 0),
                "Usage drill-down retains the selected registry view and reports the nested key");

            var link = RegistryLocation.Parse(@"HKLM\SYSTEM\CurrentControlSet");
            var linkUsage = registry.MeasureUsage(link);
            check(
                linkUsage.Total is { KeyCount: 1, ValueCount: 1, ValueDataBytes: > 0, SkippedBranches: 0 } && linkUsage.Subkeys.Length == 0,
                "Usage counts a local registry link without following its target");

            RegistryLocation? selection = null;
            tab.SelectionRequested += location => selection = location;
            await tab.ScanCommand.ExecuteAsync(tab.CurrentTarget);
            tab.SelectedSubkey = small;
            await tab.ScanCommand.ExecuteAsync(new RegistryUsageTab.ScanTarget(small));
            check(tab.Location == small && tab.Rows.Length == 0 && tab.UpTarget?.Location == root,
                "A leaf has an Up target and respects the hidden parent-row preference");

            tab.ShowParentKeyInList = true;
            check(tab.Rows is [var parent] && tab.IsParentRow(parent) && tab.Summary == "1 key · 1 value · 1 byte of value data",
                "Showing the parent row makes a leaf navigable without changing its scan totals");

            await tab.ScanCommand.ExecuteAsync(tab.BackTarget);
            check(tab.Location == root && selection == small && tab.UpTarget is null && tab.Rows.SequenceEqual(report.Subkeys),
                "Back restores the selected subkey and shows no parent row at the original scope");
            var forward = tab.ForwardTarget;
            await tab.ScanCommand.ExecuteAsync(forward);
            check(tab.Location == small && tab.Rows is [var up] && tab.IsParentRow(up),
                "Forward returns to the leaf with its parent row");

            await tab.ScanCommand.ExecuteAsync(tab.UpTarget);
            check(tab.Location == root && selection == small && tab.BackTarget?.Location == small,
                "Up opens the parent as a new visit and reselects the originating child");
            await tab.ScanCommand.ExecuteAsync(tab.BackTarget);
            await tab.ScanCommand.ExecuteAsync(tab.BackTarget);
            await tab.ScanCommand.ExecuteAsync(new RegistryUsageTab.ScanTarget(large));
            check(tab.Location == large && tab.ForwardTarget is null && tab.Rows[0].Location == root,
                "Opening a new subkey after Back discards the forward history");

            tab.SelectedSubkey = nested;
            await tab.ScanCommand.ExecuteAsync(tab.CurrentTarget);
            check(selection == nested && tab.BackTarget?.Location == root,
                "Rescan preserves selection and adds no history visit");
            tab.ShowParentKeyInList = false;
            check(tab.Rows.SequenceEqual(drilledDown.Subkeys), "Hiding the parent row restores the scanned subkeys");
        }
        finally
        {
            registry.DeleteKey(root);
        }
    }
}
