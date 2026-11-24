// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Lists;

/// <summary>
///     One computed placement shared by the header and every realized row: grid column widths (leading column first,
///     when present) and the column shown in each slot. <see cref="ColumnsVersion" /> changes when the declared column
///     set changes, so rows know to discard cells created for removed columns.
/// </summary>
internal sealed record ColumnArrangement(
    int ColumnsVersion,
    IReadOnlyCollection<ListColumn> Declared,
    IReadOnlyList<ListColumn?> Slots,
    IReadOnlyList<double> Widths,
    double Spacing)
{
    internal static readonly ColumnArrangement Empty = new(-1, [], [], [], 0);

    internal int SlotOf(ListColumn column)
    {
        for (var index = 0; index < this.Slots.Count; index++)
        {
            if (ReferenceEquals(this.Slots[index], column))
            {
                return index;
            }
        }

        return -1;
    }
}
