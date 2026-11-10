// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Controls.Shared;

internal static class StatusNotice
{
    internal static void Show(InfoBar bar, string title, string message, InfoBarSeverity severity)
    {
        var wasOpen = bar.IsOpen;
        var changed = bar.Title != title || bar.Message != message || bar.Severity != severity;
        if (wasOpen && !changed)
        {
            return;
        }

        bar.Title = title;
        bar.Message = message;
        bar.Severity = severity;
        var peer = FrameworkElementAutomationPeer.FromElement(bar) ??
                   FrameworkElementAutomationPeer.CreatePeerForElement(bar);
        bar.IsOpen = true;
        // InfoBar announces its native opening, but not changes while it remains open.
        if (wasOpen)
        {
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                severity is InfoBarSeverity.Error or InfoBarSeverity.Warning
                    ? AutomationNotificationProcessing.ImportantAll
                    : AutomationNotificationProcessing.CurrentThenMostRecent, title + Environment.NewLine + message,
                "RegistryStatus");
        }
    }
}
