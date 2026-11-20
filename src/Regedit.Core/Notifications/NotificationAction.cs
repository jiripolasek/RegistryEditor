// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Defines a labelled asynchronous action owned by the consuming application.</summary>
/// <remarks>
/// The callback begins on the invoking thread. Its token is cancelled only when the session is disposed;
/// dismissal, replacement, and eviction do not cancel it. Successful execution dismisses the original revision
/// if it remains active; failures propagate to the caller and retain the matching revision for retry.
/// </remarks>
/// <param name="Label">The nonblank, application-provided action label.</param>
/// <param name="ExecuteAsync">The non-null callback receiving the owning session's cancellation token.</param>
public sealed record NotificationAction(string Label, Func<CancellationToken, Task> ExecuteAsync)
{
    /// <summary>Gets whether this is the notification's default action.</summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. At most one action per notification may be the default.
    /// The session prefers it when <see cref="NotificationSession.InvokeActionAsync"/> is called without a specific action.
    /// Presentation and keyboard activation belong to the consuming renderer.
    /// </remarks>
    public bool IsDefault { get; init; }
}
