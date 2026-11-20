// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Identifies a notification's meaning independently of its appearance or lifetime.</summary>
public enum NotificationSeverity
{
    /// <summary>Provides ordinary informational feedback.</summary>
    Information,

    /// <summary>Reports a successful outcome.</summary>
    Success,

    /// <summary>Identifies a condition that needs attention.</summary>
    Warning,

    /// <summary>Reports a failure or an unavailable operation.</summary>
    Error,

    /// <summary>Carries no severity classification.</summary>
    Neutral
}
