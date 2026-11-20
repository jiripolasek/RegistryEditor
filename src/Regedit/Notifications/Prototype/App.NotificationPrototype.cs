// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Notifications.Prototype;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit;

public partial class App
{
    private void OpenNotificationPrototype()
    {
        var window = new NotificationPrototypeWindow(
            this.OpenNotificationPrototype,
            error => this._logger.LogFailure(LogLevel.Warning, "Notification action", error));
        this._windows.Add(window);
        window.Closed += (_, _) =>
        {
            this._windows.Remove(window);
            if (this._windows.Count == 0)
            {
                this.StopServices();
            }
        };
        window.Activate();
    }
}
