// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Manages active notifications and bounded history for an owner.</summary>
/// <remarks>
/// Session state is thread-safe and contains no UI dependencies. A renderer supplies visible elapsed time through
/// <see cref="Advance"/>; the session has no expiry timer of its own. Active entries and closed history are each bounded
/// by <see cref="Capacity"/>. Change events run on the mutating thread outside the state lock.
/// </remarks>
public sealed class NotificationSession : INotificationService, IDisposable
{
    /// <summary>Occurs when active notifications or retained history change.</summary>
    /// <remarks>
    /// Raised synchronously on the thread making the change, outside the session lock. Asynchronous action completion
    /// may raise it on a background thread. No UI context is captured, and concurrent callers may raise concurrently.
    /// Subscribers that access UI must marshal to their dispatcher.
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>Specifies the separate limits for active entries and retained history entries.</summary>
    /// <remarks>
    /// Each limit is 50. When active capacity is reached, eviction prefers the oldest entry with a finite duration
    /// and no running action, then the oldest active entry. Eviction archives content without cancelling its action.
    /// </remarks>
    public const int Capacity = 50;

    private readonly object _gate = new();
    private readonly List<Entry> _active = [];
    private readonly Queue<NotificationHistoryItem> _history = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _minimumDuration;
    private bool _disposed;
    private int _omittedCount;

    /// <summary>Gets the number of closed history entries discarded because the history limit was exceeded.</summary>
    public int OmittedCount
    {
        get
        {
            lock (this._gate)
            {
                return this._omittedCount;
            }
        }
    }

    /// <summary>Initializes an empty notification session.</summary>
    /// <param name="minimumDuration">A nonnegative minimum visible lifetime for notifications with a finite duration.</param>
    /// <exception cref="ArgumentOutOfRangeException">The minimum duration is negative.</exception>
    public NotificationSession(TimeSpan minimumDuration = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumDuration, TimeSpan.Zero);
        this._minimumDuration = minimumDuration;
    }

    /// <summary>Clears session content and cancels the session's action token.</summary>
    /// <remarks>
    /// Disposal is idempotent. It clears active entries, retained history, and event subscriptions, but does not dispose UI hosts.
    /// Subsequent publication throws; valid updates and dismissals through existing handles are harmless.
    /// Cancellation callbacks run on the disposing thread.
    /// </remarks>
    /// <exception cref="AggregateException">One or more registered cancellation callbacks throw during cancellation.</exception>
    public void Dispose()
    {
        lock (this._gate)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._active.Clear();
            this._history.Clear();
            this.Changed = null;
        }

        try
        {
            this._lifetime.Cancel();
        }
        finally
        {
            this._lifetime.Dispose();
        }
    }

    /// <summary>Publishes a notification or replaces an active entry with the same key.</summary>
    /// <remarks>
    /// A matching non-null key preserves the entry's identity and position while resetting visible elapsed time.
    /// The action collection is copied. Replacement and capacity eviction do not cancel running actions;
    /// completion of an older revision cannot dismiss its replacement.
    /// </remarks>
    /// <param name="notification">The notification to validate and publish.</param>
    /// <returns>A handle to the new or replaced active entry.</returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="ArgumentNullException">The request or a required reference in it is null.</exception>
    /// <exception cref="ArgumentException">A required label is blank, a key or severity is invalid, duration is not positive, or
    /// multiple actions are default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Progress is not a finite fraction between zero and one.</exception>
    public NotificationHandle Show(Notification notification)
    {
        notification = Validate(notification);
        Guid id;
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            var existing = notification.Key is null ? null : this._active.Find(entry => entry.Content.Key == notification.Key);
            if (existing is not null)
            {
                existing.Replace(notification);
                id = existing.Id;
            }
            else
            {
                if (this._active.Count == Capacity)
                {
                    var oldest = this._active.Find(entry => entry.Content.Duration is not null && !entry.IsActionRunning)
                                 ?? this._active[0];
                    this.Archive(oldest);
                }

                id = Guid.NewGuid();
                this._active.Add(new Entry(id, notification));
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
        return new NotificationHandle(this, id);
    }

    /// <summary>Captures the current active entries.</summary>
    /// <returns>An independent array in entry-creation order, oldest first; keyed replacement preserves that order.</returns>
    public NotificationSnapshot[] GetActive()
    {
        lock (this._gate)
        {
            return this._active.Select(entry => new NotificationSnapshot(entry.Id, entry.Content, entry.IsActionRunning)).ToArray();
        }
    }

    /// <summary>Captures the retained closed history.</summary>
    /// <returns>An independent array in closure order, oldest first, with action callbacks removed.</returns>
    public NotificationHistoryItem[] GetHistory()
    {
        lock (this._gate)
        {
            return this._history.ToArray();
        }
    }

    internal void Update(Guid id, Notification notification)
    {
        notification = Validate(notification);
        lock (this._gate)
        {
            var entry = this._active.Find(item => item.Id == id);
            if (entry is null)
            {
                return;
            }

            if (notification.Key is not null && this._active.Any(item => item.Id != id && item.Content.Key == notification.Key))
            {
                throw new ArgumentException("The notification key already belongs to another active message.", nameof(notification));
            }

            entry.Replace(notification);
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes and archives an active entry.</summary>
    /// <remarks>
    /// An unknown or already closed identity is ignored, including after disposal. Dismissal does not cancel a running action.
    /// </remarks>
    /// <param name="id">The entry to dismiss.</param>
    public void Dismiss(Guid id)
    {
        lock (this._gate)
        {
            var entry = this._active.Find(item => item.Id == id);
            if (entry is null)
            {
                return;
            }

            this.Archive(entry);
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes and archives every active entry.</summary>
    /// <remarks>
    /// Running actions are not cancelled. Calling this on an empty or disposed session is harmless.
    /// </remarks>
    public void DismissAll()
    {
        lock (this._gate)
        {
            foreach (var entry in this._active.ToArray())
            {
                this.Archive(entry);
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Advances the visible lifetimes of the specified presented entries.</summary>
    /// <remarks>
    /// The renderer excludes hidden or interacting entries. Progress entries, entries with running actions, and entries
    /// without a finite duration do not advance. Reaching the effective duration archives an entry. Unknown identities are ignored.
    /// </remarks>
    /// <param name="elapsed">The nonnegative visible interval since the renderer's previous advance.</param>
    /// <param name="presented">The entry identities actually presented and eligible to consume visible lifetime.</param>
    /// <exception cref="ArgumentOutOfRangeException">The elapsed interval is negative.</exception>
    /// <exception cref="ArgumentNullException">The presented collection is null.</exception>
    public void Advance(TimeSpan elapsed, IReadOnlyCollection<Guid> presented)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(presented);
        var changed = false;
        lock (this._gate)
        {
            foreach (var entry in this._active.ToArray())
            {
                if (entry.IsActionRunning
                    || entry.Content.Progress is not null
                    || entry.Content.Duration is not { } duration
                    || !presented.Contains(entry.Id))
                {
                    continue;
                }

                var effectiveDuration = duration < this._minimumDuration ? this._minimumDuration : duration;
                var remaining = effectiveDuration - entry.VisibleFor;
                if (elapsed >= remaining)
                {
                    this.Archive(entry);
                    changed = true;
                }
                else
                {
                    entry.VisibleFor += elapsed;
                }
            }
        }

        if (changed)
        {
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Executes a current action and dismisses its original revision on success.</summary>
    /// <remarks>
    /// Only one action runs per entry at a time, including across keyed replacement. The callback begins on the invoking
    /// thread and receives the session token; only session disposal cancels that token. A replacement survives older action completion.
    /// Callback exceptions propagate through the returned task. Unless the session token is cancelled, a matching active revision
    /// is retained with the error message, error severity, no expiry, and its actions available for retry.
    /// </remarks>
    /// <param name="id">The active entry whose action should run.</param>
    /// <param name="expected">The exact action instance currently offered by the entry. A <see langword="null"/> value selects the
    /// default action, or the first action if none is default.</param>
    /// <returns>A task representing the callback, or a completed task when the entry is inactive, already running an action, or no
    /// matching action is current.</returns>
    public async Task InvokeActionAsync(Guid id, NotificationAction? expected = null)
    {
        Entry entry;
        NotificationAction action;
        long revision;
        CancellationToken token;
        lock (this._gate)
        {
            var current = this._active.Find(item => item.Id == id);
            if (current is null || current.IsActionRunning)
            {
                return;
            }

            var currentAction = expected is null
                ? current.Content.Actions.FirstOrDefault(item => item.IsDefault) ?? current.Content.Actions.FirstOrDefault()
                : current.Content.Actions.FirstOrDefault(item => ReferenceEquals(item, expected));
            if (currentAction is null)
            {
                return;
            }

            entry = current;
            action = currentAction;
            revision = entry.Revision;
            token = this._lifetime.Token;
            entry.IsActionRunning = true;
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await action.ExecuteAsync(token).ConfigureAwait(false);
            lock (this._gate)
            {
                if (this._active.Contains(entry) && entry.Revision == revision)
                {
                    this.Archive(entry);
                }
            }
        }
        catch (Exception error)
        {
            lock (this._gate)
            {
                if (this._active.Contains(entry) && entry.Revision == revision && !token.IsCancellationRequested)
                {
                    entry.Replace(entry.Content with { Message = error.Message, Severity = NotificationSeverity.Error, Duration = null });
                }
            }

            throw;
        }
        finally
        {
            lock (this._gate)
            {
                entry.IsActionRunning = false;
            }

            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Archive(Entry entry)
    {
        this._active.Remove(entry);
        this._history.Enqueue(new NotificationHistoryItem(entry.Id, entry.Content with { Actions = [] }));
        if (this._history.Count > Capacity)
        {
            this._history.Dequeue();
            this._omittedCount++;
        }
    }

    private static Notification Validate(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentNullException.ThrowIfNull(notification.Message);
        if (notification.IconKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notification.IconKey);
        }

        if (notification.DismissLabel is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notification.DismissLabel);
        }

        if (!Enum.IsDefined(notification.Severity)
            || notification.Duration <= TimeSpan.Zero
            || notification.Key is not null && string.IsNullOrWhiteSpace(notification.Key))
        {
            throw new ArgumentException("The notification has an invalid severity, duration, or key.", nameof(notification));
        }

        if (notification.Progress?.Value is { } value && (!double.IsFinite(value) || value < 0 || value > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(notification), "Progress must be a finite fraction between zero and one.");
        }

        ArgumentNullException.ThrowIfNull(notification.Actions);
        var actions = notification.Actions.ToArray();
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentException.ThrowIfNullOrWhiteSpace(action.Label);
            ArgumentNullException.ThrowIfNull(action.ExecuteAsync);
        }

        if (actions.Count(action => action.IsDefault) > 1)
        {
            throw new ArgumentException("A notification can have only one default action.", nameof(notification));
        }

        return notification with { Actions = Array.AsReadOnly(actions) };
    }

    private sealed class Entry
    {
        internal Guid Id { get; }

        internal Notification Content { get; private set; }

        internal TimeSpan VisibleFor { get; set; }

        internal long Revision { get; private set; }

        internal bool IsActionRunning { get; set; }

        internal Entry(Guid id, Notification content)
        {
            this.Id = id;
            this.Content = content;
        }

        internal void Replace(Notification content)
        {
            this.Content = content;
            this.VisibleFor = TimeSpan.Zero;
            this.Revision++;
        }
    }
}
