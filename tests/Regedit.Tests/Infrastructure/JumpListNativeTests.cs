// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Interop;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static partial class JumpListNativeTests
{
    internal static void Run(Action<bool, string> check)
    {
        RunApartment(ApartmentState.STA, check);
        RunApartment(ApartmentState.MTA, check);
    }

    private static void RunApartment(ApartmentState apartment, Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var appId = "JPSoftworks.Regedit.Tests." + Guid.NewGuid().ToString("N");
            var initialized = false;
            try
            {
                if (apartment == ApartmentState.STA)
                {
                    Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
                    initialized = true;
                }

                check(
                    Marshal.SizeOf<JumpListNative.PropVariant>() == (nint.Size == 8 ? 24 : 16),
                    "PROPVARIANT layout matches the native ABI");
                check(!JumpListNative.IsPackaged, "Native probe detects its unpackaged host");
                if (apartment == ApartmentState.STA)
                {
                    CheckWindowIdentity(check);
                }

                var entries = JumpListEntries.Create([], new HashSet<string>()).Concat(
                [
                    new JumpListEntry("český 🐈 favorite", "\"HKCU\\Quoted \\\"key\\\"\\Tail \" --32", "test")
                ]).ToArray();
                try
                {
                    JumpListNative.Update(
                        Environment.ProcessPath!, entries, _ => throw new IOException("Abort probe"), appId);
                    check(false, "Native Jump List reports removal persistence failures");
                }
                catch (IOException)
                {
                    check(true, "Native Jump List transaction aborts when removal persistence fails");
                }

                JumpListNative.Update(
                    Environment.ProcessPath!, entries,
                    removed => check(removed.Count == 0, "A fresh isolated Jump List has no removed favorites"), appId);
                check(
                    true,
                    "Native shell accepts tasks, Unicode titles, and quoted favorite links after an aborted update");
                if (apartment == ApartmentState.MTA)
                {
                    check(true, "The native updater initializes COM on a worker without caller setup");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    if (!initialized)
                    {
                        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
                        initialized = true;
                    }

                    JumpListNative.Delete(appId);
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                if (initialized)
                {
                    CoUninitialize();
                }
            }
        });
        thread.SetApartmentState(apartment);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Native Jump List probe failed.", failure);
        }
    }

    private static unsafe void CheckWindowIdentity(Action<bool, string> check)
    {
        var window = CreateWindowEx(0, "STATIC", "Jump List identity probe", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            const string executable = @"C:\Program Files\Český editor\regedit2.exe";
            JumpListNative.SetWindowIdentity(window, executable);
            var storeId = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, in storeId, out var store));
            try
            {
                string? Read(uint id)
                {
                    var key = new PropertyKey { FormatId = new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), Id = id };
                    JumpListNative.PropVariant value = default;
                    try
                    {
                        Marshal.ThrowExceptionForHR(
                            ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, JumpListNative.PropVariant*, int>)
                                (*(nint**)store)[5])(store, &key, &value));
                        return value.Type == 0 ? null : Marshal.PtrToStringUni(value.Text);
                    }
                    finally
                    {
                        Marshal.ThrowExceptionForHR(PropVariantClear(ref value));
                    }
                }

                check(Read(4) == "Modern Registry Editor", "Taskbar uses the friendly app name");
                check(
                    Read(2) == $"\"{executable}\"",
                    "Taskbar relaunch preserves Unicode and quotes executable paths with spaces");
                check(Read(5) == JumpListNative.AppId, "Window and Jump List share the explicit app identity");
                JumpListNative.SetWindowIdentity(window, null);
                check(
                    Read(2) is null && Read(4) is null && Read(5) is null,
                    "Taskbar properties are cleared before window destruction");
            }
            finally
            {
                Marshal.Release(store);
            }
        }
        finally
        {
            JumpListNative.SetWindowIdentity(window, null);
            DestroyWindow(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        internal Guid FormatId;
        internal uint Id;
    }

    [LibraryImport(
        "user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string title,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId, out nint store);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref JumpListNative.PropVariant value);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
