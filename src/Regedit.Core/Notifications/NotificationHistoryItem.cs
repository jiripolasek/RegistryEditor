// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Stores a closed notification for session history.</summary>
/// <remarks>
/// The session strips action callbacks when archiving content. A history item cannot be invoked or reopened.
/// </remarks>
/// <param name="Id">The original entry's identity.</param>
/// <param name="Content">The archived content with an empty action collection.</param>
public sealed record NotificationHistoryItem(Guid Id, Notification Content);
