using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Microsoft.Terminal.WinUI3;

internal sealed partial class WindowSubclass : IDisposable
{
    private readonly HWND _window;
    private readonly Func<HWND, uint, WPARAM, LPARAM, nint?> _handler;
    private GCHandle _handle;

    private WindowSubclass(HWND window, Func<HWND, uint, WPARAM, LPARAM, nint?> handler)
    {
        this._window = window;
        this._handler = handler;
    }

    internal static unsafe WindowSubclass Attach(HWND window, Func<HWND, uint, WPARAM, LPARAM, nint?> handler)
    {
        if (window.IsNull)
        {
            return null;
        }

        var subclass = new WindowSubclass(window, handler);
        subclass._handle = GCHandle.Alloc(subclass);
        // Each terminal can observe the same WinUI input window. The handle gives each hook a unique ID.
        var id = (nuint)GCHandle.ToIntPtr(subclass._handle);
        if (SetWindowSubclass((nint)window, &Dispatch, id, 0) != 0)
        {
            return subclass;
        }

        subclass._handle.Free();
        Trace.TraceWarning("Could not subclass terminal window {0}.", (nint)window);
        return null;
    }

    public void Dispose() => this.Detach(windowDestroyed: false);

    private unsafe void Detach(bool windowDestroyed)
    {
        if (this._handle.IsAllocated)
        {
            var id = (nuint)GCHandle.ToIntPtr(this._handle);
            if (RemoveWindowSubclass((nint)this._window, &Dispatch, id) != 0 || windowDestroyed)
            {
                this._handle.Free();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Dispatch(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        var subclass = (WindowSubclass)GCHandle.FromIntPtr((nint)id).Target;
        nint? result = null;
        try
        {
            result = subclass._handler(new HWND(window), message, new WPARAM(wParam), new LPARAM(lParam));
        }
        catch (Exception ex)
        {
            Trace.TraceError("Terminal window message 0x{0:X} failed: {1}", message, ex);
        }
        finally
        {
            if (message == PInvoke.WM_NCDESTROY)
            {
                subclass.Detach(windowDestroyed: true);
            }
        }

        return result ?? DefSubclassProc(window, message, wParam, lParam);
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
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
