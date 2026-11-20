// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Identifies an entry that can be updated or dismissed in its owning session.</summary>
/// <remarks>
/// Handles remain valid across keyed replacements of the same active entry. Once an entry is dismissed, expired, or evicted,
/// its handle cannot reopen it, even if the same key is later published again.
/// </remarks>
public sealed class NotificationHandle
{
    private readonly NotificationSession _session;

    internal NotificationHandle(NotificationSession session, Guid id)
    {
        this._session = session;
        this.Id = id;
    }

    /// <summary>Gets the identity of the notification entry.</summary>
    public Guid Id { get; }

    /// <summary>Replaces the active entry's content and resets its visible lifetime.</summary>
    /// <remarks>
    /// Validation still runs for inactive entries. A valid update is ignored after dismissal, expiry, eviction, or session disposal.
    /// Updating preserves the entry's identity and does not cancel a running action.
    /// </remarks>
    /// <param name="notification">The replacement notification.</param>
    /// <exception cref="ArgumentNullException">The request or a required reference in it is null.</exception>
    /// <exception cref="ArgumentException">The request is invalid, or its key belongs to another active entry.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Progress is not a finite fraction between zero and one.</exception>
    public void Update(Notification notification) => this._session.Update(this.Id, notification);

    /// <summary>Closes the active entry and archives its content.</summary>
    /// <remarks>
    /// This is harmless after the entry closes or the session is disposed. It does not cancel a running action.
    /// </remarks>
    public void Dismiss() => this._session.Dismiss(this.Id);
}
