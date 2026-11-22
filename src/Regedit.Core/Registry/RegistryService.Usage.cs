// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Registry;

public sealed record RegistryUsageReport(RegistryUsageEntry Total, RegistryUsageEntry[] Subkeys);

public sealed record RegistryUsageEntry(
    RegistryLocation Location, long KeyCount, long ValueCount, long ValueDataBytes, long SkippedBranches);

public sealed partial class RegistryService
{
    /// <summary>Measures live value data and counts in a branch without reading value contents or following registry links.</summary>
    public RegistryUsageReport MeasureUsage(RegistryLocation root, CancellationToken token = default)
    {
        if (root.IsComputer || root.SubKey.Contains('\0'))
        {
            throw new ArgumentException("Select a registry hive or key first.", nameof(root));
        }

        return RemoteRegistryCredentials.Run(root.Machine, () =>
        {
            token.ThrowIfCancellationRequested();
            const uint openLink = 8;
            const RegistryRights read = RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys;
            var access = (uint)read | (uint)root.View;
            using var hive = OpenRoot(root);
            using var localRoot = root.Machine is null
                ? hive.OpenSubKey(string.Empty, read) ?? throw new UnauthorizedAccessException("Could not read the registry hive.")
                : null;
            var parent = (localRoot ?? hive).Handle;
            using var opened = root.SubKey.Length == 0 ? null : Open(parent, root.SubKey);
            var keyNames = new char[256];
            var valueNames = new char[256];
            var rows = new List<RegistryUsageEntry>();
            var total = Measure(opened ?? parent, 0);
            return new RegistryUsageReport(
                ToEntry(root, total),
                rows.OrderByDescending(row => row.ValueDataBytes).ThenBy(row => row.Location.Name, StringComparer.OrdinalIgnoreCase).ToArray());

            SafeRegistryHandle Open(SafeRegistryHandle parentKey, string name)
            {
                var error = RegistryNative.OpenKey(parentKey, name, openLink, access, out var handle);
                if (error != 0)
                {
                    handle.Dispose();
                    throw new Win32Exception(error);
                }

                return handle;
            }

            UsageCounts Measure(SafeRegistryHandle key, int depth)
            {
                token.ThrowIfCancellationRequested();
                const int noMoreItems = 259;
                const int moreData = 234;
                const int maxValueNameCapacity = 16384;
                var counts = new UsageCounts(1, 0, 0, 0);
                var incomplete = false;

                for (uint index = 0; ;)
                {
                    token.ThrowIfCancellationRequested();
                    var length = (uint)valueNames.Length;
                    var error = RegistryNative.EnumValueSize(key, index, valueNames, ref length, 0, 0, 0, out var bytes);
                    if (error == noMoreItems)
                    {
                        break;
                    }

                    if (error == moreData && valueNames.Length < maxValueNameCapacity)
                    {
                        Array.Resize(ref valueNames, valueNames.Length * 2);
                        continue;
                    }

                    if (error != 0)
                    {
                        incomplete = true;
                        break;
                    }

                    counts = counts with { Values = counts.Values + 1, Bytes = counts.Bytes + bytes };
                    index++;
                }

                for (uint index = 0; ; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var length = (uint)keyNames.Length;
                    var error = RegistryNative.EnumKey(key, index, keyNames, ref length, 0, 0, 0, 0);
                    if (error == noMoreItems)
                    {
                        break;
                    }

                    if (error != 0 || depth >= 511)
                    {
                        incomplete = true;
                        break;
                    }

                    var name = new string(keyNames, 0, (int)length);
                    // Win32 would open a different key if the name were truncated at this NUL.
                    if (name.Contains('\0'))
                    {
                        counts = counts with { Skipped = counts.Skipped + 1 };
                        continue;
                    }

                    var openError = RegistryNative.OpenKey(key, name, openLink, access, out var childKey);
                    using (childKey)
                    {
                        // The enumerated child may be inaccessible or may have disappeared during the scan.
                        var childCounts = openError == 0 ? Measure(childKey, depth + 1) : new UsageCounts(0, 0, 0, 1);
                        counts += childCounts;
                        if (depth == 0)
                        {
                            rows.Add(ToEntry(root.Child(name), childCounts));
                        }
                    }
                }

                return incomplete ? counts with { Skipped = counts.Skipped + 1 } : counts;
            }
        });
    }

    private static RegistryUsageEntry ToEntry(RegistryLocation location, UsageCounts counts)
    {
        return new RegistryUsageEntry(location, counts.Keys, counts.Values, counts.Bytes, counts.Skipped);
    }

    private readonly record struct UsageCounts(long Keys, long Values, long Bytes, long Skipped)
    {
        public static UsageCounts operator +(UsageCounts left, UsageCounts right)
        {
            return new UsageCounts(
                left.Keys + right.Keys, left.Values + right.Values, left.Bytes + right.Bytes, left.Skipped + right.Skipped);
        }
    }
}
