// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Windows.ApplicationModel.DataTransfer;

namespace JPSoftworks.Regedit.Infrastructure;

/// <summary>The single clipboard writer; view models receive <see cref="Set"/> as their copy callback.</summary>
internal static class ClipboardText
{
    internal static void Set(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
