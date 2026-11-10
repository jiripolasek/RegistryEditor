// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private const uint MenuCharMessage = 0x0120;
    private const uint NonClientDestroyMessage = 0x0082;
    private const uint NonClientLeftDoubleClickMessage = 0x00A3;
    private const uint NonClientLeftButtonDownMessage = 0x00A1;
    private const uint NonClientLeftButtonUpMessage = 0x00A2;
    private const uint NonClientRightButtonUpMessage = 0x00A5;
    private const nuint SystemMenuHit = 3;

    private unsafe void SuppressMenuChime()
    {
        if (SetWindowSubclass(this.Handle, &WindowSubclass, 0, 0) == 0)
        {
            this.Logger.LogWarning("Could not suppress the unmatched-menu chime");
        }
    }

    private unsafe void EnableWindowIconGestures()
    {
        const uint childWindow = 5;
        var inputWindow = GetWindow(this.Handle, childWindow);
        if (inputWindow == 0 || SetWindowSubclass(inputWindow, &WindowSubclass, 0, (nuint)this.Handle) == 0)
        {
            this.Logger.LogWarning("Could not enable the native window icon gestures");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint WindowSubclass(
        nint window,
        uint message,
        nuint key,
        nint data,
        nuint id,
        nuint reference)
    {
        if (key == SystemMenuHit && message is NonClientLeftDoubleClickMessage or NonClientRightButtonUpMessage)
        {
            // Restore the native icon gestures that the custom title bar consumes.
            const uint systemCommandMessage = 0x0112;
            const nuint keyMenu = 0xF100;
            const nuint close = 0xF060;
            var target = reference == 0 ? window : (nint)reference;
            if (PostMessage(
                    target, systemCommandMessage, message == NonClientRightButtonUpMessage ? keyMenu : close,
                    message == NonClientRightButtonUpMessage ? 0x20 : 0) != 0)
            {
                return 0;
            }
        }

        if (key == SystemMenuHit && message == NonClientLeftButtonDownMessage)
        {
            return 0;
        }

        if (key == SystemMenuHit && message == NonClientLeftButtonUpMessage)
        {
            // Use the native menu loop so a second icon click closes the window.
            const uint systemCommandMessage = 0x0112;
            const nuint keyMenu = 0xF100;
            return DefWindowProc(reference == 0 ? window : (nint)reference, systemCommandMessage, keyMenu, 0x20);
        }

        if (message == MenuCharMessage)
        {
            // MNC_CLOSE prevents the default unmatched-menu chime.
            return 1 << 16;
        }

        if (message == NonClientDestroyMessage)
        {
            RemoveWindowSubclass(window, &WindowSubclass, id);
        }

        return DefSubclassProc(window, message, key, data);
    }

    [LibraryImport("comctl32.dll")]
    private static unsafe partial int SetWindowSubclass(
        nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback,
        nuint id,
        nuint reference);

    [LibraryImport("comctl32.dll")]
    private static unsafe partial int RemoveWindowSubclass(
        nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback,
        nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nuint key, nint data);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint window, uint message, nuint key, nint data);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nuint key, nint data);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanW")]
    private static partial short VkKeyScan(ushort character);
}
