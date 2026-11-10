// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.Models.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class MiddleClickTests
{
    public static void Run(Action<bool, string> check)
    {
        var gesture = new MiddleClickGesture();
        var key = RegistryLocation.Parse("\\\\SERVER\\HKLM\\Software", RegistryView.Registry32);
        var first = new RegistryEntry(key, null);
        var second = new RegistryEntry(key.Child("Child"), null);
        check(!gesture.Release(1, first), "Middle release without a press does not open a tab");
        gesture.Press(1, first);
        check(gesture.Release(1, first), "Middle press and release on the same item open once");
        check(!gesture.Release(1, first), "Repeated middle release cannot reopen the item");
        gesture.Press(1, first);
        check(!gesture.Release(1, second), "Dragging to a different row does not open either row");
        gesture.Press(1, first);
        check(!gesture.Release(1, null), "Dragging outside a row cancels middle-click");
        gesture.Press(1, null);
        check(!gesture.Release(1, first), "Pressing empty space cannot open a row on release");
        gesture.Press(1, first);
        check(!gesture.Release(2, first), "A different pointer cannot complete a middle-click");
        gesture.Press(1, first);
        gesture.Cancel();
        check(!gesture.Release(1, first), "Cancellation or unloading prevents a stale middle-click");
        gesture.Press(1, first);
        check(!gesture.Release(1, first with { }), "A refreshed equal row cannot complete the old gesture");
        gesture.Press(1, first);
        gesture.Press(1, second);
        check(gesture.Release(1, second), "A new press replaces an incomplete middle-click");
        gesture.Press(1, first);
        gesture.Press(1, null);
        check(!gesture.Release(1, first), "Pressing empty space clears an incomplete gesture");
    }
}
