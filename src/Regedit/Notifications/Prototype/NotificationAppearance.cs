// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Notifications.WinUI;

namespace JPSoftworks.Regedit.Notifications.Prototype;

/// <summary>Chooses the layout and emphasis of a notification card.</summary>
internal enum NotificationVisualStyle
{
    Fluent2,
    AccentRail,
    Compact,
    Native,
    Minimal
}

/// <summary>Tracks the playground selection of bundled notification themes.</summary>
internal sealed record NotificationAppearance
{
    public NotificationVisualStyle VisualStyle { get; init; } = NotificationVisualStyle.Fluent2;

    public NotificationMaterial Material { get; init; }

    public NotificationActionPresentation Actions { get; init; }

    public NotificationDismissPresentation Dismiss { get; init; }
}
