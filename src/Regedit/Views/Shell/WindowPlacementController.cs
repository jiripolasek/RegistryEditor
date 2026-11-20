// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.Regedit.Infrastructure.Interop;
using JPSoftworks.Regedit.Models.Settings;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace JPSoftworks.Regedit.Views.Shell;

internal sealed class WindowPlacementController
{
    private readonly AppWindow _window;
    private readonly nint _handle;
    private WindowPlacement _normal;

    private double Scale
    {
        get
        {
            var dpi = WindowPlacementNative.GetDpiForWindow(this._handle);
            return dpi > 0 ? dpi / 96d : 1;
        }
    }

    internal WindowPlacementController(AppWindow window, nint handle, WindowPlacement? saved)
    {
        this._window = window;
        this._handle = handle;
        var area = saved is { IsValid: true }
            ? DisplayArea.GetFromRect(Rect(saved.Bounds), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        var work = Bounds(area.WorkArea);
        // Move the hidden window first so its DPI belongs to the actual destination display.
        window.Move(new PointInt32(work.X, work.Y));
        var scale = this.Scale;
        saved = saved is { IsValid: true }
            ? saved
            : new WindowPlacement(new WindowBounds(work.X + 40, work.Y + 40, (int)(1380 * scale), (int)(900 * scale)), work, scale);
        var restored = saved.Restore(work, scale);
        window.MoveAndResize(Rect(restored));
        this._normal = new WindowPlacement(restored, work, scale, saved.Maximized);
        if (saved.Maximized)
        {
            var presenter = OverlappedPresenter.Create();
            window.SetPresenter(presenter);
            presenter.Maximize();
        }
    }

    internal WindowPlacement Capture()
    {
        var placement = new WindowPlacementNative.Placement { Length = (uint)Marshal.SizeOf<WindowPlacementNative.Placement>() };
        if (WindowPlacementNative.GetWindowPlacement(this._handle, ref placement) == 0)
        {
            return this._normal;
        }

        var area = DisplayArea.GetFromWindowId(this._window.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var screen = area.OuterBounds;
        // A top-level window's restore rectangle uses workspace coordinates, including while maximized.
        var bounds = new WindowBounds(
            placement.Left + work.X - screen.X, placement.Top + work.Y - screen.Y, placement.Right - placement.Left,
            placement.Bottom - placement.Top);
        if (bounds.IsValid)
        {
            var maximized = placement.ShowCommand == WindowPlacementNative.ShowMaximized
                            || placement.ShowCommand == WindowPlacementNative.ShowMinimized
                            && (placement.Flags & WindowPlacementNative.RestoreToMaximized) != 0;
            this._normal = new WindowPlacement(bounds, Bounds(work), this.Scale, maximized);
        }

        return this._normal;
    }

    private static WindowBounds Bounds(RectInt32 rect)
    {
        return new WindowBounds(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static RectInt32 Rect(WindowBounds bounds)
    {
        return new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
