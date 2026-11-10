// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Omnibox;

internal static class OmniboxLayout
{
    internal static int FirstVisible(IReadOnlyList<double> widths, double availableWidth, double overflowWidth)
    {
        var remaining = widths.Sum();
        if (remaining <= availableWidth)
        {
            return 0;
        }

        var first = 0;
        while (first < widths.Count - 1 && remaining + overflowWidth > availableWidth)
        {
            remaining -= widths[first++];
        }

        return first;
    }
}
