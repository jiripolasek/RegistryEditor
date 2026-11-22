// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JPSoftworks.Regedit.Infrastructure.Interop;

internal static partial class DialogWindowNative
{
    private const int WindowOwnerIndex = -8;
    private const uint CloseCommand = 0xF060;
    private const uint GrayedByCommand = 0x0001;

    internal static void SetOwner(nint window, nint owner)
    {
        if (SetWindowLongPtr(window, WindowOwnerIndex, owner) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 0)
            {
                throw new Win32Exception(error);
            }
        }
    }

    internal static void DisableClose(nint window)
    {
        var menu = GetSystemMenu(window, 0);
        if (menu == 0 || EnableMenuItem(menu, CloseCommand, GrayedByCommand) == -1)
        {
            throw new InvalidOperationException("Could not disable the dialog window's Close command.");
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetSystemMenu(nint window, int revert);

    [LibraryImport("user32.dll")]
    private static partial int EnableMenuItem(nint menu, uint item, uint flags);
}
