// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications.WinUI;

/// <summary>Selects an anchor within a notification host's own bounds.</summary>
public enum NotificationPosition
{
    /// <summary>Anchors the stack at the bottom-right corner.</summary>
    BottomRight,

    /// <summary>Centers the stack along the bottom edge.</summary>
    BottomCenter,

    /// <summary>Anchors the stack at the bottom-left corner.</summary>
    BottomLeft,

    /// <summary>Anchors the stack at the top-right corner.</summary>
    TopRight,

    /// <summary>Centers the stack along the top edge.</summary>
    TopCenter,

    /// <summary>Anchors the stack at the top-left corner.</summary>
    TopLeft
}

/// <summary>Selects a material visual state supported by the control template.</summary>
public enum NotificationMaterial
{
    /// <summary>Requests an opaque notification surface.</summary>
    Solid,

    /// <summary>Requests in-app acrylic with the default theme's system-dependent fallback.</summary>
    Acrylic
}

/// <summary>Selects the generated action control type.</summary>
public enum NotificationActionPresentation
{
    /// <summary>Presents actions as lightweight links.</summary>
    Link,

    /// <summary>Presents actions as buttons.</summary>
    Button
}

/// <summary>Selects the default dismissal presentation for a card.</summary>
public enum NotificationDismissPresentation
{
    /// <summary>Uses an icon close button unless the notification supplies a dismissal label.</summary>
    Icon,

    /// <summary>Uses a text dismissal, taking its label from the notification or localized defaults.</summary>
    Link
}
