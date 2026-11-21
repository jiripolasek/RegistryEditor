// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Tools;

internal static class FavoritesBarLayout
{
    internal static int VisibleCount(IReadOnlyList<double> widths, double available, double gap, double overflowWidth)
    {
        if (widths.Sum() + Math.Max(0, widths.Count - 1) * gap <= available)
        {
            return widths.Count;
        }

        var remaining = available - overflowWidth - gap;
        var visible = 0;
        while (visible < widths.Count && widths[visible] <= remaining)
        {
            remaining -= widths[visible++] + gap;
        }

        return visible;
    }
}
