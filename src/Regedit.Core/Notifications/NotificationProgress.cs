// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Notifications;

/// <summary>Describes determinate or indeterminate operation progress.</summary>
/// <remarks>
/// The session validates determinate values when the containing notification is published or updated.
/// </remarks>
/// <param name="Value">A finite fraction from zero to one, or <see langword="null"/> for indeterminate progress.</param>
public sealed record NotificationProgress(double? Value)
{
    /// <summary>Gets shared indeterminate progress without a completion fraction.</summary>
    public static NotificationProgress Indeterminate { get; } = new(Value: null);
}
