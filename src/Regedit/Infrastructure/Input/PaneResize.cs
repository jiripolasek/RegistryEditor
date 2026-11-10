// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Infrastructure.Input;

internal static class PaneResize
{
    internal static double Apply(double size, double delta, double minimum, double maximum)
    {
        return Math.Clamp(Math.Clamp(size, minimum, maximum) + delta, minimum, maximum);
    }
}
