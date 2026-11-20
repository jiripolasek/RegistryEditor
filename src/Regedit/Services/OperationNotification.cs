// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.Services;

/// <summary>Owns one operation's progress and terminal notification independently of page feedback.</summary>
internal sealed class OperationNotification : IDisposable
{
    private readonly INotificationService _notifications;
    private readonly string _key;
    private readonly Action _cancel;
    private NotificationHandle? _handle;
    private Notification? _content;
    private bool _progress, _disposed;

    internal OperationNotification(INotificationService notifications, string key, Action cancel)
    {
        this._notifications = notifications;
        this._key = key;
        this._cancel = cancel;
    }

    public void Dispose()
    {
        this._disposed = true;
        this._handle?.Dismiss();
        this._handle = null;
        this._content = null;
        this._progress = false;
    }

    internal void Start(string title, string message)
    {
        this.Show(
            StatusNotifications.Running(StatusNotifications.Create(title, message, NoticeKind.Information, key: this._key), this._cancel),
            true);
    }

    internal void ShowResult(string title, string message, NoticeKind kind)
    {
        this.Show(StatusNotifications.Create(title, message, kind, key: this._key), false);
    }

    internal void AddOpenFolderAction(string file)
    {
        if (!this._disposed && this._content is not null)
        {
            this._content = StatusNotifications.WithOpenFolderAction(this._content, file);
            this._handle?.Update(this._content);
        }
    }

    internal void Complete()
    {
        if (this._progress)
        {
            this._handle?.Dismiss();
            this._handle = null;
            this._content = null;
            this._progress = false;
        }
    }

    private void Show(Notification content, bool progress)
    {
        if (this._disposed)
        {
            return;
        }

        this._progress = progress;
        this._content = content;
        this._handle = this._notifications.Show(content);
    }
}
