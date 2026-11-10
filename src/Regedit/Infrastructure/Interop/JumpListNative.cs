// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Infrastructure.Interop;

internal static unsafe partial class JumpListNative
{
    internal const string AppId = "JPSoftworks.RegistryEditor";

    private static readonly Guid AppUserModelFormat = new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
    private static readonly Guid DestinationListClass = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    private static readonly Guid DestinationListId = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    private static readonly Guid ObjectArrayId = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    private static readonly Guid CollectionClass = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    private static readonly Guid CollectionId = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid ShellLinkId = new("000214f9-0000-0000-c000-000000000046");
    private static readonly Guid PropertyStoreId = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    internal static bool IsPackaged
    {
        get
        {
            uint length = 0;
            var error = GetCurrentPackageFullName(ref length, null);
            return error switch
            {
                122 => true, // ERROR_INSUFFICIENT_BUFFER: a package name is available.
                15700 => false, // APPMODEL_ERROR_NO_PACKAGE
                _ => throw new System.ComponentModel.Win32Exception(error)
            };
        }
    }

    internal static void SetWindowIdentity(nint window, string? executable)
    {
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, in PropertyStoreId, out var pointer));
        using var store = new ComPointer(pointer);
        // Set relaunch information before the ID; null clears the properties before window destruction.
        SetString(
            store, new PropertyKey { FormatId = AppUserModelFormat, Id = 2 },
            executable is null ? null : $"\"{executable}\"");
        SetString(
            store, new PropertyKey { FormatId = AppUserModelFormat, Id = 4 },
            executable is null ? null : "Modern Registry Editor");
        SetString(store, new PropertyKey { FormatId = AppUserModelFormat, Id = 5 }, executable is null ? null : AppId);
    }

    internal static void Update(
        string executable,
        JumpListEntry[] entries,
        Action<IReadOnlySet<string>> saveRemovedArguments,
        string? appId = null)
    {
        var initialized = CoInitializeEx(0, 0);
        // RPC_E_CHANGED_MODE means the caller already owns an initialized STA apartment.
        if (initialized != unchecked((int)0x80010106))
        {
            Marshal.ThrowExceptionForHR(initialized);
        }

        try
        {
            UpdateCore(executable, entries, saveRemovedArguments, appId);
        }
        finally
        {
            if (initialized >= 0)
            {
                CoUninitialize();
            }
        }
    }

    private static void UpdateCore(
        string executable,
        JumpListEntry[] entries,
        Action<IReadOnlySet<string>> saveRemovedArguments,
        string? appId)
    {
        using var list = ComPointer.Create(DestinationListClass, DestinationListId);
        if (appId is not null)
        {
            fixed (char* id = appId)
            {
                Marshal.ThrowExceptionForHR(
                    ((delegate* unmanaged[Stdcall]<nint, char*, int>)list.Method(3))(list.Pointer, id));
            }
        }

        var arrayId = ObjectArrayId;
        uint slots = 0;
        nint removedPointer = 0;
        // ICustomDestinationList.BeginList returns an owned IObjectArray reference.
        Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)list.Method(4))(
                list.Pointer, &slots, &arrayId, &removedPointer));
        using var removed = new ComPointer(removedPointer);
        var committed = false;
        try
        {
            var removedArguments = ReadRemovedArguments(removed);
            saveRemovedArguments(removedArguments);
            using var tasks = CreateCollection(executable, entries.Where(entry => entry.FavoriteId is null));
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[Stdcall]<nint, nint, int>)list.Method(7))(list.Pointer, tasks.Pointer));

            var favorites = entries
                .Where(entry => entry.FavoriteId is not null && !removedArguments.Contains(entry.Arguments))
                .DistinctBy(entry => entry.Arguments, StringComparer.Ordinal).Take((int)Math.Min(slots, int.MaxValue))
                .ToArray();
            if (favorites.Length > 0)
            {
                using var destinations = CreateCollection(executable, favorites);
                fixed (char* name = "Favorites")
                {
                    var result = ((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)list.Method(5))(
                        list.Pointer, name, destinations.Pointer);
                    // Windows privacy settings can disable custom categories while still allowing tasks.
                    if (result != unchecked((int)0x80070005))
                    {
                        Marshal.ThrowExceptionForHR(result);
                    }
                }
            }

            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)list.Method(8))(list.Pointer));
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)list.Method(11))(list.Pointer);
            }
        }
    }

    internal static void Delete(string appId)
    {
        using var list = ComPointer.Create(DestinationListClass, DestinationListId);
        fixed (char* id = appId)
        {
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[Stdcall]<nint, char*, int>)list.Method(10))(list.Pointer, id));
        }
    }

    private static HashSet<string> ReadRemovedArguments(ComPointer array)
    {
        uint count = 0;
        Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<nint, uint*, int>)array.Method(3))(array.Pointer, &count));
        var result = new HashSet<string>(StringComparer.Ordinal);
        var linkId = ShellLinkId;
        var buffer = new char[32768];
        for (uint index = 0; index < count; index++)
        {
            nint pointer = 0;
            var error = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)array.Method(4))(
                array.Pointer, index, &linkId, &pointer);
            if (error == unchecked((int)0x80004002)) // E_NOINTERFACE: a removed IShellItem, not one of our links.
            {
                continue;
            }

            Marshal.ThrowExceptionForHR(error);
            using var link = new ComPointer(pointer);
            fixed (char* arguments = buffer)
            {
                Marshal.ThrowExceptionForHR(
                    ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)link.Method(10))(
                        link.Pointer, arguments, buffer.Length));
                result.Add(new string(arguments));
            }
        }

        return result;
    }

    private static ComPointer CreateCollection(string executable, IEnumerable<JumpListEntry> entries)
    {
        var collection = ComPointer.Create(CollectionClass, CollectionId);
        try
        {
            foreach (var entry in entries)
            {
                using var link = ComPointer.Create(ShellLinkClass, ShellLinkId);
                fixed (char* path = executable)
                fixed (char* arguments = entry.Arguments)
                {
                    // IShellLinkW.SetPath, SetArguments, and SetIconLocation.
                    Marshal.ThrowExceptionForHR(
                        ((delegate* unmanaged[Stdcall]<nint, char*, int>)link.Method(20))(link.Pointer, path));
                    Marshal.ThrowExceptionForHR(
                        ((delegate* unmanaged[Stdcall]<nint, char*, int>)link.Method(11))(link.Pointer, arguments));
                    Marshal.ThrowExceptionForHR(
                        ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)link.Method(17))(link.Pointer, path, 0));
                }

                var storeId = PropertyStoreId;
                nint storePointer = 0;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(link.Pointer, in storeId, out storePointer));
                using var store = new ComPointer(storePointer);
                var title = new PropertyKey { FormatId = new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), Id = 2 };
                SetString(store, title, entry.Title);
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)store.Method(7))(store.Pointer));

                // IObjectCollection.AddObject retains its own reference to the shell link.
                Marshal.ThrowExceptionForHR(
                    ((delegate* unmanaged[Stdcall]<nint, nint, int>)collection.Method(5))(
                        collection.Pointer, link.Pointer));
            }

            return collection;
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    private static void SetString(ComPointer store, PropertyKey key, string? text)
    {
        var pointer = Marshal.StringToCoTaskMemUni(text);
        try
        {
            var value = new PropVariant
            {
                Type = text is null ? (ushort)0 : (ushort)31, Text = pointer
            }; // VT_EMPTY or VT_LPWSTR
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)store.Method(6))(
                    store.Pointer, &key, &value));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        internal Guid FormatId;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;

        [FieldOffset(8)] internal nint Text;

        // The PROPVARIANT union also contains a counted array: ULONG followed by a pointer.
        [FieldOffset(8)] private CountedArray _array;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CountedArray
    {
        internal uint Count;
        internal nint Pointer;
    }

    private sealed class ComPointer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ComPointer(nint pointer)
            : base(true)
        {
            this.SetHandle(pointer);
        }

        internal nint Pointer => this.handle;

        internal nint Method(int slot)
        {
            return (*(nint**)this.handle)[slot];
        }

        internal static ComPointer Create(Guid classId, Guid interfaceId)
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, 0, 1, in interfaceId, out var pointer));
            return new ComPointer(pointer);
        }

        protected override bool ReleaseHandle()
        {
            Marshal.Release(this.handle);
            return true;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId, out nint store);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint length, char* name);
}
