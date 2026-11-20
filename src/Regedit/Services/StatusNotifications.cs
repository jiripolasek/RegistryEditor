// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.Services;

/// <summary>Applies the app's lifetime policy to routine operation feedback.</summary>
internal static class StatusNotifications
{
    internal static Notification Create(
        string title,
        string message,
        NoticeKind kind,
        IReadOnlyList<NotificationAction>? actions = null,
        string? key = null)
    {
        var severity = kind switch
        {
            NoticeKind.Information => NotificationSeverity.Information,
            NoticeKind.Success => NotificationSeverity.Success,
            NoticeKind.Warning => NotificationSeverity.Warning,
            NoticeKind.Error => NotificationSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new Notification
        {
            Title = title,
            Message = message,
            Severity = severity,
            Key = key,
            Duration = kind is NoticeKind.Warning or NoticeKind.Error || actions is { Count: > 0 } ? null : TimeSpan.FromSeconds(6),
            Actions = actions ?? []
        };
    }

    internal static NotificationHandle? UpdateError(
        INotificationService notifications,
        NotificationHandle? current,
        string title,
        string? error,
        string key)
    {
        if (error is null)
        {
            current?.Dismiss();
            return null;
        }

        return Show(notifications, title, error, NoticeKind.Error, key: key);
    }

    internal static Notification Running(Notification content, Action cancel)
    {
        return content with
        {
            Duration = null,
            Progress = NotificationProgress.Indeterminate,
            Actions = (NotificationAction[])
            [
                new NotificationAction(
                    "Cancel", _ =>
                    {
                        cancel();
                        return Task.CompletedTask;
                    })
            ]
        };
    }

    internal static Notification WithOpenFolderAction(Notification notification, string file)
    {
        var exportedFile = Path.GetFullPath(file);
        return notification with
        {
            Duration = null,
            Actions = (NotificationAction[])
            [
                new NotificationAction(
                    "Open folder", token => Task.Run(
                        () =>
                        {
                            var start = RegistryFileReveal.CreateStartInfo(exportedFile);
                            token.ThrowIfCancellationRequested();
                            using var process = Process.Start(start);
                        }, token))
            ]
        };
    }

    internal static NotificationHandle Show(
        INotificationService notifications,
        string title,
        string message,
        NoticeKind kind,
        IReadOnlyList<NotificationAction>? actions = null,
        string? key = null)
    {
        return notifications.Show(Create(title, message, kind, actions, key));
    }
}
