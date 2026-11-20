// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.Regedit.Infrastructure.Interop;

internal static partial class WindowPlacementNative
{
    internal const uint ShowMinimized = 2;
    internal const uint ShowMaximized = 3;
    internal const uint RestoreToMaximized = 2;

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowPlacement(nint window, ref Placement placement);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Placement
    {
        internal uint Length;
        internal uint Flags;
        internal uint ShowCommand;
        internal int MinX;
        internal int MinY;
        internal int MaxX;
        internal int MaxY;
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
