// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Snapshots;

public sealed record RegistryBranchSnapshot(
    int Version,
    RegistryLocation Root,
    DateTimeOffset Started,
    DateTimeOffset Completed,
    SnapshotKey[] Keys,
    string Computer)
{
    public const int CurrentVersion = 1;
    private const long MaximumFileBytes = 256 * 1024 * 1024;

    public static RegistryBranchSnapshot Capture(
        RegistryService registry,
        RegistryLocation root,
        CancellationToken token = default,
        bool allowMissingRoot = false)
    {
        return Capture(registry.Read, root, token, allowMissingRoot);
    }

    internal static RegistryBranchSnapshot Capture(
        Func<RegistryLocation, RegistrySnapshot> read,
        RegistryLocation root,
        CancellationToken token = default,
        bool allowMissingRoot = false,
        bool skipMissingDescendants = false,
        JournalCaptureBudget? budget = null)
    {
        var started = DateTimeOffset.UtcNow;
        var keys = new List<SnapshotKey>();
        var stack = new Stack<RegistryLocation>();
        stack.Push(root);
        while (stack.TryPop(out var location))
        {
            token.ThrowIfCancellationRequested();
            var path = location.SubKey.Length == root.SubKey.Length
                ? string.Empty
                : location.SubKey[(root.SubKey.Length == 0 ? 0 : root.SubKey.Length + 1)..];
            budget?.Charge(JournalCaptureBudget.KeyBytes + JournalCaptureBudget.TextBytes(path));
            RegistrySnapshot key;
            try
            {
                key = read(location);
            }
            catch (FileNotFoundException) when (path.Length == 0 ? allowMissingRoot : skipMissingDescendants)
            {
                continue;
            }
            catch (Exception ex) when (skipMissingDescendants
                                       && (path.Length != 0 || allowMissingRoot)
                                       && ex is Win32Exception { NativeErrorCode: RegistryNative.ErrorKeyDeleted } or IOException
                                       {
                                           HResult: RegistryNative.ErrorKeyDeleted or unchecked((int)0x800703FA)
                                       })
            {
                continue;
            }

            keys.Add(new SnapshotKey(path, key.Values));
            key.PushChildren(stack, location);
        }

        token.ThrowIfCancellationRequested();
        return new RegistryBranchSnapshot(
            CurrentVersion, root, started, DateTimeOffset.UtcNow, keys.ToArray(), root.Machine ?? Environment.MachineName);
    }

    public async Task SaveAsync(string file, CancellationToken token = default)
    {
        this.Validate();
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await JsonSerializer.SerializeAsync(stream, this, SnapshotJsonContext.Default.RegistryBranchSnapshot, token);
                if (stream.Length > MaximumFileBytes)
                {
                    throw new IOException("Snapshots are limited to 256 MiB. Capture a smaller branch.");
                }
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, file, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<RegistryBranchSnapshot> LoadAsync(string file, CancellationToken token = default)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length > MaximumFileBytes)
        {
            throw new IOException("This snapshot exceeds the 256 MiB file limit.");
        }

        var snapshot = await JsonSerializer.DeserializeAsync(stream, SnapshotJsonContext.Default.RegistryBranchSnapshot, token)
                       ?? throw new InvalidDataException("The snapshot is empty.");
        snapshot.Validate();
        return snapshot;
    }

    public void Validate()
    {
        if (this.Version != CurrentVersion)
        {
            throw new InvalidDataException("This snapshot format version is not supported.");
        }

        if (this.Root is null
            || this.Root.SubKey is null
            || this.Keys is null
            || string.IsNullOrWhiteSpace(this.Computer)
            || this.Started == default
            || this.Completed < this.Started
            || this.Root.View is not (RegistryView.Registry32 or RegistryView.Registry64)
            || !RegistryLocation.Roots.Any(root => root.Hive == this.Root.Hive))
        {
            throw new InvalidDataException("The snapshot metadata is invalid.");
        }

        RegistryWorkspace.ValidateLocation(this.Root);
        ValidatePath(this.Root.SubKey);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in this.Keys)
        {
            if (key is null || key.Path is null || key.Values is null || !paths.Add(key.Path))
            {
                throw new InvalidDataException("The snapshot contains a missing or duplicate key.");
            }

            ValidatePath(key.Path);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in key.Values)
            {
                if (value is null || value.Name is null || value.Data is null || !names.Add(value.Name))
                {
                    throw new InvalidDataException("The snapshot contains a missing or duplicate value.");
                }

                RegistryService.ValidateValueName(value.Name);
            }
        }

        if (this.Keys.Length > 0 && !paths.Contains(string.Empty))
        {
            throw new InvalidDataException("The snapshot has no root key.");
        }

        foreach (var path in paths.Where(path => path.Length > 0))
        {
            var parent = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : string.Empty;
            if (!paths.Contains(parent))
            {
                throw new InvalidDataException("The snapshot has a key without its parent.");
            }
        }
    }

    private static void ValidatePath(string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        var segments = path.Split('\\');
        if (segments.Length > 512)
        {
            throw new InvalidDataException("The snapshot exceeds the maximum registry depth.");
        }

        foreach (var segment in segments)
        {
            RegistryLocation.ValidateKeyName(segment);
        }
    }
}

[JsonSourceGenerationOptions(MaxDepth = 32, IgnoreReadOnlyProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RegistryBranchSnapshot))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
