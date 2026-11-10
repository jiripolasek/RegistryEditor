// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Lists;

public static class ColumnSizing
{
    /// <summary>
    ///     Returns pixel widths for the visible columns of <paramref name="layout" />, in display order. Fixed columns
    ///     keep their width; fill columns (width 0) split what remains of <paramref name="available" /> by fill weight
    ///     and never shrink below their minimum, so the total can exceed the available width and scroll.
    /// </summary>
    public static double[] Compute(ColumnLayout layout, IReadOnlyList<ColumnSpec> specs, double available)
    {
        var known = specs.ToDictionary(spec => spec.Id, StringComparer.Ordinal);
        var visible = layout.Visible.Where(column => known.ContainsKey(column.Id)).ToArray();
        var widths = new double[visible.Length];
        var remaining = double.IsFinite(available) ? Math.Max(0, available) : 0;
        var weights = 0d;
        for (var index = 0; index < visible.Length; index++)
        {
            var spec = known[visible[index].Id];
            if (visible[index].Width > 0)
            {
                widths[index] = spec.ClampWidth(visible[index].Width);
                remaining -= widths[index];
            }
            else
            {
                weights += Math.Max(0.01, spec.FillWeight);
            }
        }

        for (var index = 0; index < visible.Length; index++)
        {
            if (widths[index] > 0)
            {
                continue;
            }

            var spec = known[visible[index].Id];
            var share = weights > 0 ? Math.Max(0, remaining) * Math.Max(0.01, spec.FillWeight) / weights : 0;
            widths[index] = Math.Max(spec.MinWidth, Math.Floor(share));
        }

        return widths;
    }
}
