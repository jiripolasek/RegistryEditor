// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Text;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Registry;

public sealed partial class RegistryService
{
    public RegistryJournal? Journal { get; set; }

    private void Mutate(
        JournalOperation operation,
        JournalScope[] scopes,
        Action action,
        CancellationToken cancellationToken,
        long minimumAfterBytes = 0)
    {
        if (this.Journal is { } journal)
        {
            journal.Execute(operation, scopes, action, cancellationToken, minimumAfterBytes);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }
    }

    public void Write(
        RegistryLocation location,
        RegistryValue value,
        RegistryValue? expected,
        CancellationToken cancellationToken = default)
    {
        var minimumAfterBytes = this.Journal?.CheckIncomingValue(value) ?? 0;
        this.Mutate(
            expected is null ? JournalOperation.CreateValue : JournalOperation.EditValue, [new JournalScope(location, [value.Name])],
            () => this.WriteCore(location, value, expected), cancellationToken, minimumAfterBytes);
    }

    public void CreateKey(RegistryLocation parent, string name, CancellationToken cancellationToken = default)
    {
        this.Mutate(
            JournalOperation.CreateKey, [new JournalScope(parent.Child(name))], () => this.CreateKeyCore(parent, name), cancellationToken);
    }

    public void RenameKey(RegistryLocation location, string name, CancellationToken cancellationToken = default)
    {
        this.Mutate(
            JournalOperation.RenameKey, [
                new JournalScope(location),
                new JournalScope((location.Parent ?? throw new InvalidOperationException("Cannot rename a root hive.")).Child(name))
            ], () => this.RenameKeyCore(location, name), cancellationToken);
    }

    public void RenameValue(RegistryLocation location, RegistryValue expected, string name, CancellationToken cancellationToken = default)
    {
        this.Mutate(
            JournalOperation.RenameValue, [new JournalScope(location, [expected.Name, name])],
            () => this.RenameValueCore(location, expected, name), cancellationToken);
    }

    public void DeleteKey(RegistryLocation location, CancellationToken cancellationToken = default)
    {
        if (location.Parent is null)
        {
            throw new InvalidOperationException("Root hives cannot be deleted.");
        }

        this.Mutate(JournalOperation.DeleteKey, [new JournalScope(location)], () => this.DeleteKeyCore(location), cancellationToken);
    }

    public void DeleteValue(RegistryLocation location, RegistryValue expected, CancellationToken cancellationToken = default)
    {
        this.Mutate(
            JournalOperation.DeleteValue, [new JournalScope(location, [expected.Name])], () => this.DeleteValueCore(location, expected),
            cancellationToken);
    }

    public void CopyKey(RegistryLocation source, RegistryLocation destination, string name, CancellationToken cancellationToken = default)
    {
        this.Mutate(
            JournalOperation.CopyKey, [new JournalScope(destination.Child(name))], () => this.CopyKeyCore(source, destination, name),
            cancellationToken);
    }

    public RegistryKey Open(RegistryLocation location, bool writable = false)
    {
        return RemoteRegistryCredentials.Run(
            location.Machine, () =>
            {
                var root = OpenRoot(location);
                // Remote hive handles already identify the root; reopening an empty path can be denied by the server.
                if (location.Machine is not null && location.SubKey.Length == 0)
                {
                    return root;
                }

                using (root)
                {
                    return root.OpenSubKey(location.SubKey, writable)
                           ?? throw new FileNotFoundException($"The key no longer exists: {location.Address}");
                }
            });
    }

    private static RegistryKey OpenRoot(RegistryLocation location)
    {
        var hive = location.Hive ?? throw new InvalidOperationException("Select a registry hive or key first.");
        if (location.SubKey.Count(c => c == '\\') >= 512)
        {
            throw new IOException("The maximum registry depth was exceeded. A registry link may form a cycle.");
        }

        return location.Machine is null
            ? RegistryKey.OpenBaseKey(hive, location.View)
            : RemoteRegistryCredentials.OpenBaseKey(hive, location.Machine, location.View);
    }

    /// <summary>Reads the key's modification time and immediate subkey and value counts.</summary>
    /// <remarks>For local symbolic links, the metadata describes the link itself. Its target is available on request.</remarks>
    public RegistryKeyDetails ReadKeyDetails(RegistryLocation location, bool includeLink)
    {
        if (location.Machine is not null)
        {
            using var remote = this.Open(location);
            Check(QueryKeyInfo(remote.Handle, out var modified, out var children, out var entries));
            return new RegistryKeyDetails(modified, checked((int)children), checked((int)entries), null);
        }

        const uint openLink = 8;
        using var hive = OpenRoot(location);
        // Handle on a predefined RegistryKey reopens the hive for writing, even for a read-only query.
        using var root = hive.OpenSubKey(string.Empty, RegistryRights.QueryValues)
                         ?? throw new IOException("Could not open the registry hive for reading.");
        var localChild = location.Machine is null && location.SubKey.Length != 0;
        var access = (uint)RegistryRights.QueryValues | (uint)location.View;
        // Opening the local link itself keeps its counts and timestamp independent of the target.
        var error = RegistryNative.OpenKey(root.Handle, location.SubKey, localChild ? openLink : 0, access, out var key);
        using (key)
        {
            Check(error);
            Check(QueryKeyInfo(key, out var lastWrite, out var subKeys, out var values));
            return new RegistryKeyDetails(
                lastWrite, checked((int)subKeys), checked((int)values), localChild && includeLink ? ReadLinkTarget(key) : null);
        }
    }

    private static int QueryKeyInfo(SafeRegistryHandle key, out DateTimeOffset? lastWriteUtc, out uint subKeys, out uint values)
    {
        var error = RegistryNative.QueryInfoKey(key, 0, 0, 0, out subKeys, 0, 0, out values, 0, 0, 0, out var fileTime);
        lastWriteUtc = error == 0 && fileTime > 0 && fileTime <= DateTimeOffset.MaxValue.ToFileTime()
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime))
            : null;
        return error;
    }

    private static string? ReadLinkTarget(SafeRegistryHandle key)
    {
        const string name = "SymbolicLinkValue";
        const uint linkType = (uint)RegistryValueType.Link;
        uint size = 0;
        var error = RegistryNative.QueryValue(key, name, 0, out var type, null, ref size);
        if (error == 2)
        {
            return null;
        }

        Check(error);
        // NT object names have a 16-bit byte length. Ordinary keys may also have a value with this name.
        if (type != linkType || size == 0 || size > ushort.MaxValue || size % sizeof(char) != 0)
        {
            return null;
        }

        var data = new byte[size];
        error = RegistryNative.QueryValue(key, name, 0, out type, data, ref size);
        if (error == 2)
        {
            return null;
        }

        Check(error);
        return type == linkType && size != 0 && size <= data.Length && size % sizeof(char) == 0
            ? Encoding.Unicode.GetString(data, 0, checked((int)size)).TrimEnd('\0')
            : null;
    }

    public string[] SubKeys(RegistryLocation location)
    {
        using var key = this.Open(location);
        var names = key.GetSubKeyNames();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public RegistrySnapshot Read(RegistryLocation location)
    {
        return this.Read(location, false);
    }

    public RegistrySnapshot Read(RegistryLocation location, bool includeLastWriteTime)
    {
        using var key = this.Open(location);
        var names = key.GetSubKeyNames();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        var values = key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).Select(name => ReadValue(key, name))
            .OfType<RegistryValue>().ToArray();
        DateTimeOffset? lastWrite = null;
        if (includeLastWriteTime)
        {
            // An unavailable timestamp must not hide readable values.
            QueryKeyInfo(key.Handle, out lastWrite, out _, out _);
        }

        return new RegistrySnapshot(names, values) { LastWriteTimeUtc = lastWrite };
    }

    internal RegistrySnapshot Read(RegistryLocation location, JournalCaptureBudget budget)
    {
        using var key = this.Open(location);
        budget.Check((long)key.SubKeyCount * JournalCaptureBudget.KeyBytes + (long)key.ValueCount * JournalCaptureBudget.ValueBytes);
        var names = ReadNames(key, location, true, budget);
        var valueNames = ReadNames(key, location, false, budget);
        var values = new List<RegistryValue>();
        foreach (var name in valueNames)
        {
            if (ReadValue(key, name, budget) is { } value)
            {
                values.Add(value);
            }
        }

        return new RegistrySnapshot(names, values.ToArray());
    }

    private static string[] ReadNames(RegistryKey key, RegistryLocation location, bool subKeys, JournalCaptureBudget budget)
    {
        var fixedBytes = subKeys
            ? JournalCaptureBudget.KeyBytes + JournalCaptureBudget.TextBytes(location.SubKey) + sizeof(char)
            : JournalCaptureBudget.ValueBytes;
        // A child name also occurs in its queued full path, before PushChildren constructs it.
        var characterBytes = subKeys ? 2 * sizeof(char) : sizeof(char);
        var buffer = new char[(int)Math.Min(256, Math.Max(0, budget.Remaining - fixedBytes) / characterBytes + 1)];
        var names = new List<string>();
        for (uint index = 0;;)
        {
            var length = (uint)buffer.Length;
            var error = subKeys
                ? RegistryNative.EnumKey(key.Handle, index, buffer, ref length, 0, 0, 0, 0)
                : RegistryNative.EnumValue(key.Handle, index, buffer, ref length, 0, 0, 0, 0);
            if (error == 259)
            {
                break;
            }

            if (error == 234 || error == 0 && length >= buffer.Length)
            {
                budget.Check(fixedBytes + (long)buffer.Length * characterBytes);
                var capacity = Math.Min((long)buffer.Length * 2, (budget.Remaining - fixedBytes) / characterBytes + 1);
                buffer = new char[checked((int)capacity)];
                continue;
            }

            Check(error);
            budget.Charge(fixedBytes + (long)length * characterBytes);
            names.Add(new string(buffer, 0, checked((int)length)));
            index++;
        }

        var result = names.ToArray();
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public RegistryValue? ReadValue(RegistryLocation location, string name)
    {
        using var key = this.Open(location);
        return ReadValue(key, name);
    }

    public RegistryValue? ReadValue(RegistryLocation location, string name, out DateTimeOffset? lastWriteTimeUtc)
    {
        using var key = this.Open(location);
        var value = ReadValue(key, name);
        lastWriteTimeUtc = null;
        if (location.Machine is null)
        {
            // An unavailable timestamp must not hide a readable value.
            QueryKeyInfo(key.Handle, out lastWriteTimeUtc, out _, out _);
        }

        return value;
    }

    internal static RegistryValue? ReadValue(
        RegistryKey key,
        string name,
        JournalCaptureBudget? budget = null,
        QueryRegistryValue? query = null)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            uint size = 0;
            uint type;
            var error = query is null
                ? RegistryNative.QueryValue(key.Handle, name, 0, out type, null, ref size)
                : query(null, out type, ref size);
            if (error == 2)
            {
                return null;
            }

            Check(error);
            budget?.Check(size);
            var bytes = new byte[checked((int)size)];
            error = query is null
                ? RegistryNative.QueryValue(key.Handle, name, 0, out type, bytes, ref size)
                : query(bytes, out type, ref size);
            if (error == 234 || error == 0 && size > bytes.Length)
            {
                continue;
            }

            if (error == 2)
            {
                return null;
            }

            Check(error);
            budget?.Check(size);
            Array.Resize(ref bytes, checked((int)size));
            budget?.Charge(size);
            return new RegistryValue(name, (RegistryValueType)type, bytes);
        }

        throw new IOException("The value keeps changing. Refresh and try again.");
    }

    private void WriteCore(RegistryLocation location, RegistryValue value, RegistryValue? expected)
    {
        ValidateValueName(value.Name);
        using var key = this.Open(location, true);
        var current = ReadValue(key, value.Name);
        if (expected is null ? current is not null : !expected.SameData(current))
        {
            throw new IOException("This value was changed by another program or already exists. Refresh before editing it.");
        }

        Check(RegistryNative.SetValue(key.Handle, value.Name, 0, (uint)value.Type, value.Data, checked((uint)value.Data.Length)));
    }

    private void CreateKeyCore(RegistryLocation parent, string name)
    {
        RegistryLocation.ValidateKeyName(name);
        using var key = this.Open(parent, true);
        Check(RegistryNative.CreateKey(key.Handle, name, 0, 0, 0, 0x20019, 0, out var created, out var disposition));
        using (created)
        {
            if (disposition == 2)
            {
                throw new IOException("A key with this name already exists.");
            }
        }
    }

    private void RenameKeyCore(RegistryLocation location, string name)
    {
        RegistryLocation.ValidateKeyName(name);
        var parent = location.Parent ?? throw new InvalidOperationException("Root hives cannot be renamed.");
        using var key = this.Open(parent, true);
        var error = RegistryNative.RenameKey(key.Handle, location.Name, name);
        if (RenameCollides(error, location.Name, name, key.GetSubKeyNames))
        {
            throw new IOException("A key with this name already exists.", new Win32Exception(error));
        }

        Check(error);
    }

    internal static bool RenameCollides(int error, string source, string destination, Func<string[]> siblings)
    {
        if (error == 183)
        {
            return true;
        }

        if (error != 5 || source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return siblings().Any(name => name.Equals(destination, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or Win32Exception)
        {
            return false;
        }
    }

    private void RenameValueCore(RegistryLocation location, RegistryValue expected, string name)
    {
        ValidateValueName(name);
        if (expected.Name.Length == 0 || name.Length == 0)
        {
            throw new InvalidOperationException("The default value cannot be renamed.");
        }

        if (name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a different value name.");
        }

        using var key = this.Open(location, true);
        if (!expected.SameData(ReadValue(key, expected.Name)))
        {
            throw new IOException("The value changed. Refresh and try again.");
        }

        if (ReadValue(key, name) is not null)
        {
            throw new IOException("A value with this name already exists.");
        }

        Check(RegistryNative.SetValue(key.Handle, name, 0, (uint)expected.Type, expected.Data, checked((uint)expected.Data.Length)));
        // Keep both copies if removing the original fails, so a failed rename never loses the data.
        key.DeleteValue(expected.Name, true);
    }

    private void DeleteKeyCore(RegistryLocation location)
    {
        var parent = location.Parent ?? throw new InvalidOperationException("Root hives cannot be deleted.");
        using var key = this.Open(parent, true);
        key.DeleteSubKeyTree(location.Name, true);
    }

    private void DeleteValueCore(RegistryLocation location, RegistryValue expected)
    {
        using var key = this.Open(location, true);
        if (!expected.SameData(ReadValue(key, expected.Name)))
        {
            throw new IOException("The value changed. Refresh before deleting it.");
        }

        key.DeleteValue(expected.Name, true);
    }

    private void CopyKeyCore(RegistryLocation source, RegistryLocation destination, string name)
    {
        var target = destination.Child(name);
        if (!string.Equals(source.Machine, target.Machine, StringComparison.OrdinalIgnoreCase) || source.View != target.View)
        {
            throw new InvalidOperationException(
                "Copy keys within the same computer connection and registry view. Use export and import to transfer data between them.");
        }

        var sourcePaths = RegistryKeyIdentity.Paths(source);
        if (target.IsAtOrBelow(source)
            || RegistryKeyIdentity.Paths(target).Any(child => sourcePaths.Any(parent => RegistryKeyIdentity.AtOrBelow(child, parent))))
        {
            throw new InvalidOperationException("A key cannot be copied into itself.");
        }

        using var sourceKey = this.Open(source);
        this.CreateKeyCore(destination, name);
        using var targetParent = this.Open(destination, true);
        using var targetKey = targetParent.OpenSubKey(name, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)
                              ?? throw new IOException("The destination key could not be opened.");
        // A partial copy is kept on failure; removing it could also remove concurrent writes.
        Check(RegistryNative.CopyTree(sourceKey.Handle, null, targetKey.Handle));
    }

    public SearchResult FindNext(RegistryLocation root, SearchOptions options, SearchHit? after, CancellationToken token)
    {
        if (string.IsNullOrEmpty(options.Text) || !(options.Keys || options.Values || options.Data))
        {
            throw new ArgumentException("Enter search text and choose keys, values, or data.");
        }

        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        bool Matches(string text)
        {
            return options.Whole ? text.Equals(options.Text, comparison) : text.Contains(options.Text, comparison);
        }

        token.ThrowIfCancellationRequested();
        if (after is not null && !after.Location.IsAtOrBelow(root))
        {
            return new SearchResult(null, 0);
        }

        // Only ancestors of the cursor need their earlier children skipped during the sorted depth-first walk.
        var cursorPath = after?.Location.SubKey[root.SubKey.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<(RegistryLocation Location, int CursorDepth)>();
        stack.Push((root, cursorPath is null ? -1 : 0));
        var skippedKeys = 0;
        while (stack.TryPop(out var frame))
        {
            token.ThrowIfCancellationRequested();
            var (location, depth) = frame;
            RegistrySnapshot snapshot;
            try
            {
                snapshot = this.Read(location);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or Win32Exception)
            {
                skippedKeys++;
                continue;
            }

            if (depth < 0 && options.Keys && Matches(location.Name))
            {
                return new SearchResult(new SearchHit(location, null), skippedKeys);
            }

            var atCursor = cursorPath is not null && depth == cursorPath.Length;
            if (depth < 0 || atCursor)
            {
                foreach (var value in snapshot.Values)
                {
                    token.ThrowIfCancellationRequested();
                    if ((atCursor && StringComparer.OrdinalIgnoreCase.Compare(value.Name, after?.ValueName) <= 0)
                        || (options.Types is { Length: > 0 } && !options.Types.Contains(value.Type)))
                    {
                        continue;
                    }

                    if ((options.Values && Matches(value.Name)) || (options.Data && Matches(RegistrySearch.ValueSearchText(value))))
                    {
                        return new SearchResult(new SearchHit(location, value.Name), skippedKeys);
                    }
                }
            }

            for (var i = snapshot.SubKeys.Length - 1; i >= 0; i--)
            {
                var child = snapshot.SubKeys[i];
                var order = depth < 0 || atCursor ? 1 : StringComparer.OrdinalIgnoreCase.Compare(child, cursorPath![depth]);
                if (order < 0)
                {
                    break;
                }

                stack.Push((location.Child(child), order == 0 ? depth + 1 : -1));
            }
        }

        return new SearchResult(null, skippedKeys);
    }

    public static async Task RunRegAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start the Windows registry utility.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new IOException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }
    }

    public static async Task SaveHiveAsync(RegistryLocation location, string file)
    {
        RegistryWorkspace.ValidateLocation(location);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await RunRegAsync("save", location.Address, temporary, location.View == RegistryView.Registry32 ? "/reg:32" : "/reg:64");
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

    public static void ValidateValueName(string name)
    {
        if (name.Length > 16383 || name.Contains('\0'))
        {
            throw new ArgumentException("The value name is too long or contains a null character.");
        }
    }

    private static void Check(int error)
    {
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    internal delegate int QueryRegistryValue(byte[]? bytes, out uint type, ref uint size);
}
