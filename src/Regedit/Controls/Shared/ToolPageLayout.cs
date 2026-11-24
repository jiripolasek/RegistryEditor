// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;

namespace JPSoftworks.Regedit.Controls.Shared;

internal static class ToolPageLayout
{
    internal static Thickness GetPadding(double width, bool embedded)
    {
        var horizontal = width < 900 ? 16 : 24;
        return embedded ? new Thickness(0) : new Thickness(horizontal, 24, horizontal, 16);
    }
}
