// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Models.Registry;

internal static class RegistryColumns
{
    internal static readonly RegistrySortColumn[] All = Enum.GetValues<RegistrySortColumn>();

    internal static RegistrySortColumn[] Defaults()
    {
        return [RegistrySortColumn.Name, RegistrySortColumn.Type, RegistrySortColumn.Data];
    }

    internal static int IndexOf(this IReadOnlyList<RegistrySortColumn> columns, RegistrySortColumn column)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index] == column)
            {
                return index;
            }
        }

        return -1;
    }

    internal static string Label(this RegistrySortColumn column)
    {
        return column switch
        {
            RegistrySortColumn.LastWrite => "Key last modified",
            RegistrySortColumn.LinkTarget => "Link target",
            RegistrySortColumn.Size => "Size (bytes)",
            _ => column.ToString()
        };
    }

    internal static bool NeedsKeyDetails(this RegistrySortColumn column)
    {
        return column is RegistrySortColumn.LastWrite or RegistrySortColumn.Children or RegistrySortColumn.LinkTarget;
    }

    internal static IEnumerable<RegistryEntry> Sort(
        IEnumerable<RegistryEntry> entries,
        RegistrySortColumn column,
        bool descending)
    {
        var grouped = entries.OrderByDescending(item => item.IsKey).ThenByDescending(item => item.IsDefaultValue);

        IOrderedEnumerable<RegistryEntry> Order<T>(Func<RegistryEntry, T> field, IComparer<T>? comparer = null)
        {
            return descending ? grouped.ThenByDescending(field, comparer) : grouped.ThenBy(field, comparer);
        }

        var sorted = column switch
        {
            RegistrySortColumn.Type => Order(item => item.Type, StringComparer.OrdinalIgnoreCase),
            RegistrySortColumn.Data => Order(item => item.Data, StringComparer.OrdinalIgnoreCase),
            RegistrySortColumn.LastWrite => Order(item => item.LastWriteTimeUtc),
            RegistrySortColumn.Children => Order(item => (item.Details?.SubKeyCount, item.Details?.ValueCount)),
            RegistrySortColumn.Notes => Order(item => item.Note, StringComparer.OrdinalIgnoreCase),
            RegistrySortColumn.LinkTarget => Order(
                item => item.Value?.Type == RegistryValueType.Link ? item.Data : item.Details?.LinkTarget,
                StringComparer.OrdinalIgnoreCase),
            RegistrySortColumn.Size => Order(item => item.Value?.Data.Length),
            _ => Order(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
        return sorted.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool TryParse(string? name, out RegistrySortColumn column)
    {
        return Enum.TryParse(name, out column) && Enum.GetName(column) == name;
    }

    internal static RegistrySortColumn[] FromNames(string[]? names)
    {
        if (names is null)
        {
            return Defaults();
        }

        var columns = new List<RegistrySortColumn>();
        foreach (var name in names)
        {
            if (TryParse(name, out var column) && !columns.Contains(column))
            {
                columns.Add(column);
            }
        }

        return columns.Count == 0 ? Defaults() : columns.ToArray();
    }
}
