// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Notifications.WinUI.Themes;

/// <summary>Provides native InfoBar presentation while the notification host owns announcements.</summary>
/// <remarks>
/// Used by the default native card template. Its automation peer avoids duplicate native announcements
/// for entries outside the stack's presented viewport.
/// </remarks>
public sealed partial class NotificationInfoBar : InfoBar
{
    // A native InfoBar peer would also announce entries outside the expanded stack's viewport.
    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new NotificationInfoBarAutomationPeer(this);
    }

    private sealed partial class NotificationInfoBarAutomationPeer : FrameworkElementAutomationPeer
    {
        internal NotificationInfoBarAutomationPeer(NotificationInfoBar owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore()
        {
            return nameof(InfoBar);
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.StatusBar;
        }
    }
}
