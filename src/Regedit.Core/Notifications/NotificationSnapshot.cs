// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Provides an immutable snapshot of an active notification.</summary>
/// <remarks>
/// Later session changes do not update an existing snapshot.
/// </remarks>
/// <param name="Id">The active entry's identity.</param>
/// <param name="Content">The notification content captured by the snapshot.</param>
/// <param name="IsActionRunning">Whether an action for this entry was running when the snapshot was captured.</param>
public sealed record NotificationSnapshot(Guid Id, Notification Content, bool IsActionRunning);
