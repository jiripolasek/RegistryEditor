// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Journal;

public sealed partial class JournalHistoryReader
{
    private const int FileBasicInfoClass = 0;
    private const int FileIdInfoClass = 18;
    private static readonly uint BasicInfoBytes = (uint)Marshal.SizeOf<FileBasicInfo>();
    private static readonly uint IdInfoBytes = (uint)Marshal.SizeOf<FileIdInfo>();
    private readonly RegistryJournal _journal;
    private Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CachedFile> _current = new(StringComparer.OrdinalIgnoreCase);
    private FileGeneration? _generation;

    private sealed record CachedFile(FileGeneration Generation, JournalEntry Entry);

    public JournalHistoryReader(RegistryJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        this._journal = journal;
    }

    internal int CachedFileCount => this._files.Count;

    public JournalHistory ReadHistory()
    {
        return this._journal.ReadHistory(this);
    }

    internal void BeginRead()
    {
        this._current = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
    }

    internal JournalEntry? Find(string file, FileStream stream)
    {
        this._generation = ReadGeneration(stream);
        return this._generation is { } generation && this._files.TryGetValue(file, out var cached) &&
               cached.Generation == generation
            ? cached.Entry
            : null;
    }

    internal void Keep(string file, JournalEntry entry)
    {
        if (this._generation is { } generation)
        {
            this._current.Add(file, new CachedFile(generation, entry));
        }
    }

    internal void EndRead()
    {
        // RegistryJournal calls the cache only under its reader/writer gate and keeps no open file between reads.
        this._files = this._current;
    }

    internal readonly record struct FileGeneration(
        long ChangeTime,
        ulong Volume,
        ulong IdLow,
        ulong IdHigh,
        long Length);

    internal static FileGeneration? ReadGeneration(FileStream stream)
    {
        if (GetBasicInfo(stream.SafeFileHandle, FileBasicInfoClass, out var basic, BasicInfoBytes) == 0 || GetIdInfo(
                stream.SafeFileHandle, FileIdInfoClass, out var id, IdInfoBytes) == 0)
        {
            return null;
        }

        return new FileGeneration(basic.ChangeTime, id.VolumeSerialNumber, id.IdLow, id.IdHigh, stream.Length);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal ulong IdLow;
        internal ulong IdHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    private static partial int GetBasicInfo(
        SafeFileHandle file,
        int informationClass,
        out FileBasicInfo info,
        uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    private static partial int GetIdInfo(SafeFileHandle file, int informationClass, out FileIdInfo info, uint size);
}
