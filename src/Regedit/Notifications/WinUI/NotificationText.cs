// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;
using Microsoft.UI.Xaml;

namespace JPSoftworks.Notifications.WinUI;

internal static class NotificationText
{
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Dismiss"] = "Dismiss",
        ["DismissNotification"] = "Dismiss notification",
        ["DefaultAction"] = "Default action",
        ["Progress"] = "Operation progress",
        ["Busy"] = "Operation in progress",
        ["ExpandStack"] = "Expand notification stack",
        ["ExpandStackFormat"] = "Show all {0} notifications",
        ["CollapseStackFormat"] = "Collapse {0} notifications",
        ["ExpandedStackHelp"] = "Notifications won't close automatically while you review them.",
        ["CollapsedStackHelp"] = "Expand the stack to review all active notifications.",
        ["DismissAll"] = "Dismiss all active notifications",
        ["HistoryTitleFormat"] = "Notification history ({0})",
        ["HistoryOmittedFormat"] = "Older notifications removed: {0}.",
        ["HistoryActive"] = "Active",
        ["HistoryClosed"] = "Closed",
        ["HistoryItemFormat"] = "{0} · {1}",
        ["NotificationNameFormat"] = "{0}: {1}",
        ["ProgressFormat"] = "{0:P0} complete",
        ["ProgressWithMessageFormat"] = "{0} · {1:P0} complete",
        ["SeverityNeutral"] = "",
        ["SeverityInformation"] = "Information",
        ["SeveritySuccess"] = "Success",
        ["SeverityWarning"] = "Warning",
        ["SeverityError"] = "Error",
    };

    internal static string Get(ResourceDictionary? strings, string key)
    {
        return strings is not null && strings.TryGetValue(key, out var value) && value is string text ? text : Defaults[key];
    }

    internal static string Format(ResourceDictionary? strings, string key, params object[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, Get(strings, key), arguments);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, Defaults[key], arguments);
        }
    }

    internal static string Name(ResourceDictionary? strings, Notification content)
    {
        var key = content.Severity switch
        {
            NotificationSeverity.Neutral => "SeverityNeutral",
            NotificationSeverity.Information => "SeverityInformation",
            NotificationSeverity.Success => "SeveritySuccess",
            NotificationSeverity.Warning => "SeverityWarning",
            NotificationSeverity.Error => "SeverityError",
            _ => throw new ArgumentOutOfRangeException(nameof(content))
        };
        var severity = Get(strings, key);
        return severity.Length == 0 ? content.Title : Format(strings, "NotificationNameFormat", severity, content.Title);
    }
}
