// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class RegistryUsageTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> canceled, bool useRegistry)
    {
        var registry = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.UsageTests." + Guid.NewGuid().ToString("N"), RegistryView.Registry32);
        canceled(() => registry.MeasureUsage(root, new CancellationToken(true)), "Usage cancels before opening its scope");
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
        }
        finally
        {
            registry.DeleteKey(root);
        }
    }
}
