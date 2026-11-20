// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Describes an in-app message independently of its presentation framework.</summary>
/// <remarks>
/// A session validates the request and copies its action collection when it is published or updated.
/// Use <see cref="Key"/> to replace related active messages without creating another entry.
/// </remarks>
public sealed record Notification
{
    /// <summary>Gets the required, nonblank notification title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the message body.</summary>
    /// <remarks>
    /// Defaults to an empty string. A published message cannot be <see langword="null"/>.
    /// </remarks>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the semantic severity.</summary>
    /// <remarks>
    /// Defaults to <see cref="NotificationSeverity.Information"/>. Severity does not determine the request's lifetime.
    /// </remarks>
    public NotificationSeverity Severity { get; init; }

    /// <summary>Gets an optional renderer-defined custom icon key.</summary>
    /// <remarks>
    /// A supplied key must be nonblank. It is independent of severity, priority, and lifetime; the consuming renderer resolves it.
    /// </remarks>
    public string? IconKey { get; init; }

    /// <summary>Gets an optional key for replacing an active notification.</summary>
    /// <remarks>
    /// Matching is case-sensitive and scoped to the session. A nonblank key reuses a matching active entry's identity and position.
    /// A <see langword="null"/> key always creates a new entry; closed entries are never reopened.
    /// </remarks>
    public string? Key { get; init; }

    /// <summary>Gets the requested visible lifetime.</summary>
    /// <remarks>
    /// Defaults to five seconds. A positive duration is extended to the session's minimum duration.
    /// A <see langword="null"/> value disables automatic expiry. Lifetimes advance only when the renderer calls
    /// <see cref="NotificationSession.Advance"/>; hidden, interacting, progress, and action-running messages can be paused.
    /// </remarks>
    public TimeSpan? Duration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets optional determinate or indeterminate progress.</summary>
    /// <remarks>
    /// A <see langword="null"/> value means no progress. Any non-null progress prevents automatic expiry until it is removed or
    /// replaced.
    /// </remarks>
    public NotificationProgress? Progress { get; init; }

    /// <summary>Gets the actions offered by the notification.</summary>
    /// <remarks>
    /// Defaults to an empty collection. The session copies the collection, and at most one action may have
    /// <see cref="NotificationAction.IsDefault"/> set. Dismissal, replacement, and eviction do not cancel an already running action.
    /// </remarks>
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];

    /// <summary>Gets an optional per-instance dismissal label.</summary>
    /// <remarks>
    /// A supplied label must be nonblank. Renderers may use it for a lightweight text dismissal in place of their normal close control.
    /// This changes presentation only; it does not add an application callback or cancel the operation.
    /// </remarks>
    public string? DismissLabel { get; init; }
}
