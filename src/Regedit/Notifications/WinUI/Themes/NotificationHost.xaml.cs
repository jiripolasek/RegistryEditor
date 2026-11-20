// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;

namespace JPSoftworks.Notifications.WinUI.Themes;

/// <summary>Provides default notification host resources with compiled history bindings.</summary>
public sealed partial class NotificationHostResources : ResourceDictionary
{
    /// <summary>Loads the default host resources and history templates.</summary>
    public NotificationHostResources()
    {
        this.InitializeComponent();
    }
}
