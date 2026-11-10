// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Lists;

/// <summary>
///     The fixed, non-visual rules of one list column. A <see cref="DefaultWidth" /> of 0 makes the column
///     share the space left over by fixed-width columns in proportion to <see cref="FillWeight" />.
/// </summary>
public sealed record ColumnSpec(
    string Id,
    double DefaultWidth = 0,
    double MinWidth = 48,
    double MaxWidth = 1600,
    double FillWeight = 1,
    bool CanHide = true,
    bool CanResize = true,
    bool CanReorder = true,
    bool DefaultVisible = true)
{
    /// <summary>Clamps a user width; non-finite or non-positive values fall back to the default.</summary>
    public double ClampWidth(double width)
    {
        if (!double.IsFinite(width) || width < 0)
        {
            return this.DefaultWidth;
        }

        return width == 0 ? 0 : Math.Clamp(width, this.MinWidth, Math.Max(this.MinWidth, this.MaxWidth));
    }
}
