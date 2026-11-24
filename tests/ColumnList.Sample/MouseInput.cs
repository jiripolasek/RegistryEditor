// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace JPSoftworks.ColumnList.Sample;

/// <summary>
///     Real OS mouse and keyboard input for <c>--input-test</c>. It moves the user's cursor, so it only runs when asked.
///     Unlike automation peers, it exercises pointer capture, routing and Click exactly as a person would.
/// </summary>
internal static partial class MouseInput
{
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const uint AbsoluteVirtualDesktopMove = 0xC001;
    private static nint _targetWindow;

    internal static async Task FocusAsync(Window window)
    {
        _targetWindow = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetForegroundWindow(_targetWindow);
        if (GetForegroundWindow() != _targetWindow)
        {
            var caption = ToScreen(window, window.Content, new Point(window.Content.RenderSize.Width / 2, -16));
            await MoveAsync(caption);
            mouse_event(LeftDown, 0, 0, 0, 0);
            mouse_event(LeftUp, 0, 0, 0, 0);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (GetForegroundWindow() != _targetWindow && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            if (GetForegroundWindow() != _targetWindow)
            {
                throw new InvalidOperationException("The component test window could not receive foreground input.");
            }
        }
    }

    /// <summary>Converts a point in <paramref name="element" /> coordinates (DIPs) to physical screen pixels.</summary>
    internal static (int X, int Y) ToScreen(Window window, UIElement element, Point point)
    {
        var inContent = element.TransformToVisual(window.Content).TransformPoint(point);
        var scale = element.XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin))
        {
            throw new InvalidOperationException("Could not locate the component test window's client area.");
        }

        return (origin.X + (int)Math.Round(inContent.X * scale), origin.Y + (int)Math.Round(inContent.Y * scale));
    }

    internal static async Task MoveAsync((int X, int Y) to)
    {
        if (!SetCursorPos(to.X, to.Y))
        {
            throw new InvalidOperationException("Could not move the test cursor.");
        }

        await Task.Delay(30);
        if (!GetCursorPos(out var actual)
            || actual.X != to.X
            || actual.Y != to.Y
            || GetAncestor(WindowFromPoint(new NativePoint { X = to.X, Y = to.Y }), 2) != _targetWindow)
        {
            throw new InvalidOperationException($"The test window is not under the cursor at {to}; actual {actual.X},{actual.Y}.");
        }
    }

    /// <summary>Moves in small steps so the app sees a continuous drag, as a hand would produce.</summary>
    internal static async Task GlideAsync((int X, int Y) from, (int X, int Y) to, int steps = 12)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1)
        {
            throw new InvalidOperationException("The virtual desktop has no usable pointer bounds.");
        }

        for (var step = 1; step <= steps; step++)
        {
            var x = from.X + (to.X - from.X) * step / steps;
            var y = from.Y + (to.Y - from.Y) * step / steps;
            mouse_event(
                AbsoluteVirtualDesktopMove, (int)Math.Round((x - left) * 65535d / (width - 1)),
                (int)Math.Round((y - top) * 65535d / (height - 1)), 0, 0);
            await Task.Delay(25);
        }
    }

    internal static async Task DownAsync()
    {
        mouse_event(LeftDown, 0, 0, 0, 0);
        await Task.Delay(60);
    }

    internal static async Task UpAsync()
    {
        mouse_event(LeftUp, 0, 0, 0, 0);
        await Task.Delay(120);
    }

    internal static async Task PressKeyAsync(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, 0);
        keybd_event(virtualKey, 0, KeyUp, 0);
        await Task.Delay(60);
    }

    internal static void SetKey(byte virtualKey, bool down)
    {
        keybd_event(virtualKey, 0, down ? 0 : KeyUp, 0);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, int dx, int dy, uint data, nuint extraInfo);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte virtualKey, byte scan, uint flags, nuint extraInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
