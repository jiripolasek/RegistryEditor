// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Views.Shell;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class CaptionLayoutTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2, 3 })
        {
            check(CaptionLayout.InsetWidth(150, scale) == 150 / scale, $"Caption insets respect DPI scale {scale}");
            check(CaptionLayout.InsetWidth(0, scale) == 0, $"An empty caption inset stays empty at DPI scale {scale}");
            check(CaptionLayout.InsetWidth(-150, scale) == 0, $"Negative caption insets are clamped at DPI scale {scale}");
        }

        foreach (var scale in new[] { 0d, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon })
        {
            foreach (var pixels in new[] { int.MinValue, 0, 150, int.MaxValue })
            {
                check(CaptionLayout.InsetWidth(pixels, scale) == 0, $"Invalid caption conversion stays empty: {pixels} / {scale}");
            }
        }
    }
}
