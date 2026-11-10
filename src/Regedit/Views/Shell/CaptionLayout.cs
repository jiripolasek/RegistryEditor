// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Views.Shell;

internal static class CaptionLayout
{
    internal static double InsetWidth(int pixels, double scale)
    {
        var width = pixels / scale;
        return scale > 0 && double.IsFinite(scale) && double.IsFinite(width) ? Math.Max(0, width) : 0;
    }
}
