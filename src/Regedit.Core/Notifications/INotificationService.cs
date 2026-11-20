// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Publishes notifications into an explicitly owned session.</summary>
public interface INotificationService
{
    /// <summary>Publishes a notification or replaces an active notification with the same key.</summary>
    /// <remarks>
    /// The returned handle belongs to the owning session. See <see cref="NotificationSession"/> for its lifetime and threading rules.
    /// </remarks>
    /// <param name="notification">The notification to publish.</param>
    /// <returns>A handle identifying the new or replaced active entry.</returns>
    NotificationHandle Show(Notification notification);
}
