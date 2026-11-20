// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Serialization;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Core.Journal;

public sealed class RegistryJournal
{
    private const long MaximumFileBytes = 256 * 1024 * 1024;

    private static readonly IEqualityComparer<JournalSecurity> SecurityComparer = EqualityComparer<JournalSecurity>.Create(
        static (left, right) => left is not null
                                && right is not null
                                && left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase)
                                && left.Descriptor == right.Descriptor,
        static item => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(item.Path), item.Descriptor));

    private readonly string _folder;
    private readonly object _gate = new();
    private readonly AsyncLocal<List<JournalEntry>?> _trackedEdits = new();
    private readonly RegistryService _registry = new();
    private readonly long _captureLimit;
    private readonly long _fileLimit;

    public bool AdministratorOnly { get; }

    public RegistryJournal(string folder, bool administratorOnly = false)
        : this(folder, administratorOnly, JournalCaptureBudget.DefaultLimit)
    {
    }

    internal RegistryJournal(string folder, bool administratorOnly, long captureLimit, long fileLimit = MaximumFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileLimit);
        this._folder = folder;
        this.AdministratorOnly = administratorOnly;
        this._captureLimit = captureLimit;
        this._fileLimit = fileLimit;
    }

    internal long CheckIncomingValue(RegistryValue value)
    {
        var budget = new JournalCaptureBudget(this._captureLimit);
        budget.ValueName(value.Name);
        budget.Check(value.Data.LongLength);
        return JournalCaptureBudget.ValueBytes + 2 * JournalCaptureBudget.TextBytes(value.Name) + value.Data.LongLength;
    }

    private static IOException StorageDenied(UnauthorizedAccessException failure)
    {
        return new IOException("The journal storage cannot be accessed. Check the journal folder's permissions and try again.", failure);
    }

    private AdministratorJournalDirectory? PrepareStorage()
    {
        try
        {
            if (this.AdministratorOnly)
            {
                return AdministratorJournalDirectory.Open(this._folder);
            }

            Directory.CreateDirectory(this._folder);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw StorageDenied(ex);
        }
    }

    private static IOException WriterBusy(Exception? inner = null)
    {
        return new IOException(
            "Another Registry Editor operation is using the journal. Wait for it to finish, then try again. "
            + "If it remains busy, close the other Registry Editor windows and retry.", inner);
    }

    private void EnterGate(long started, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Monitor.TryEnter(this._gate))
            {
                return;
            }

            WaitForWriter(started, cancellationToken);
        }
    }

    private static void WaitForWriter(long started, CancellationToken cancellationToken, IOException? contention = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = TimeSpan.FromSeconds(3) - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            throw WriterBusy(contention);
        }

        cancellationToken.WaitHandle.WaitOne(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25));
    }

    private FileStream OpenWriterLock(long started, CancellationToken cancellationToken)
    {
        var file = Path.Combine(this._folder, "write.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return this.AdministratorOnly
                    ? AdministratorJournalDirectory.OpenFile(file, FileMode.OpenOrCreate)
                    : new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                WaitForWriter(started, cancellationToken, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw StorageDenied(ex);
            }
        }
    }

    public void RecordActivity(
        string operation,
        RegistryLocation location,
        string details,
        Action action,
        CancellationToken cancellationToken = default)
    {
        RegistryWorkspace.ValidateLocation(location, true);
        var started = Stopwatch.GetTimestamp();
        this.EnterGate(started, cancellationToken);
        try
        {
            using var storage = this.PrepareStorage();
            using var lease = this.OpenWriterLock(started, cancellationToken);
            var entry = new JournalEntry(
                Guid.NewGuid(), DateTimeOffset.UtcNow, operation, [], null, ActivityLocation: location, Activity: details,
                Kind: JournalOperation.Activity);
            cancellationToken.ThrowIfCancellationRequested();
            this.Save(entry);
            try
            {
                action();
                this.Save(entry with { After = [] });
            }
            catch (Exception ex)
            {
                this.Save(entry with { After = [], Error = ex.Message });
                throw;
            }
        }
        finally
        {
            Monitor.Exit(this._gate);
        }
    }

    public JournalEntry[] ReadEntries()
    {
        return this.ReadEntries(null);
    }

    public JournalHistory ReadHistory()
    {
        var damaged = new List<string>();
        return new JournalHistory(this.ReadEntries(damaged), damaged.ToArray());
    }

    internal JournalHistory ReadHistory(JournalHistoryReader reader)
    {
        var damaged = new List<string>();
        return new JournalHistory(this.ReadEntries(damaged, reader), damaged.ToArray());
    }

    private JournalEntry[] ReadEntries(List<string>? damaged, JournalHistoryReader? reader = null)
    {
        lock (this._gate)
        {
            reader?.BeginRead();
            try
            {
                using var storage = this.AdministratorOnly ? this.PrepareStorage() : null;
                if (!Directory.Exists(this._folder))
                {
                    return [];
                }

                var entries = new List<JournalEntry>();
                var ids = new HashSet<Guid>();
                foreach (var file in Directory.EnumerateFiles(this._folder, "*.json"))
                {
                    if (this.AdministratorOnly)
                    {
                        AdministratorJournalDirectory.ValidateFile(file);
                    }

                    try
                    {
                        using var stream = new FileStream(
                            file, FileMode.Open, FileAccess.Read, reader is null ? FileShare.Read : FileShare.Read | FileShare.Delete);
                        if (reader is not null && this.AdministratorOnly)
                        {
                            AdministratorJournalDirectory.ValidateFile(stream);
                        }

                        if (stream.Length > MaximumFileBytes)
                        {
                            throw new InvalidDataException("A journal entry exceeds the size limit.");
                        }

                        var entry = reader?.Find(file, stream);
                        if (entry is null)
                        {
                            entry = JsonSerializer.Deserialize(stream, JournalJsonContext.Default.JournalEntry)
                                    ?? throw new InvalidDataException("A journal entry is empty.");
                            Validate(entry);
                        }

                        if (!ids.Add(entry.Id))
                        {
                            throw new InvalidDataException("A journal entry ID occurs more than once.");
                        }

                        reader?.Keep(file, entry);
                        entries.Add(entry);
                    }
                    catch (Exception ex) when (damaged is not null
                                               && ex is JsonException or InvalidDataException or ArgumentException or FormatException
                                                   or IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        damaged.Add(Path.GetFileName(file));
                    }
                }

                return entries.OrderBy(entry => entry.Time).ThenBy(entry => entry.Id).ToArray();
            }
            finally
            {
                reader?.EndRead();
            }
        }
    }

    public JournalArchiveState ReadArchiveState()
    {
        lock (this._gate)
        {
            using var storage = this.AdministratorOnly ? this.PrepareStorage() : null;
            var archived = new HashSet<Guid>();
            var damaged = new List<string>();
            if (!Directory.Exists(this._folder))
            {
                return new JournalArchiveState(archived, []);
            }

            foreach (var file in Directory.EnumerateFiles(this._folder, "*.archive"))
            {
                if (this.AdministratorOnly)
                {
                    AdministratorJournalDirectory.ValidateFile(file);
                }

                try
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) || id == Guid.Empty)
                    {
                        throw new InvalidDataException("Invalid journal archive marker name.");
                    }

                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (this.AdministratorOnly)
                    {
                        AdministratorJournalDirectory.ValidateFile(stream);
                    }

                    if (stream.Length != 1)
                    {
                        throw new InvalidDataException("Invalid journal archive marker.");
                    }

                    var value = stream.ReadByte();
                    if (value is not (0 or 1))
                    {
                        throw new InvalidDataException("Invalid journal archive marker.");
                    }

                    if (value == 1)
                    {
                        archived.Add(id);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                               or System.Security.SecurityException)
                {
                    damaged.Add(Path.GetFileName(file));
                }
            }

            return new JournalArchiveState(archived, damaged.ToArray());
        }
    }

    public void SetArchived(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Select a journal entry to archive.", nameof(id));
        }

        var started = Stopwatch.GetTimestamp();
        this.EnterGate(started, cancellationToken);
        try
        {
            using var storage = this.PrepareStorage();
            using var lease = this.OpenWriterLock(started, cancellationToken);
            var file = Path.Combine(this._folder, id.ToString("N") + ".archive");
            if (this.AdministratorOnly && File.Exists(file))
            {
                AdministratorJournalDirectory.ValidateFile(file);
            }

            cancellationToken.ThrowIfCancellationRequested();
            this.WriteFile(file, stream => stream.WriteByte(archived ? (byte)1 : (byte)0));
        }
        finally
        {
            Monitor.Exit(this._gate);
        }
    }

    internal void Execute(
        JournalOperation kind,
        JournalScope[] scopes,
        Action action,
        CancellationToken cancellationToken = default,
        long minimumAfterBytes = 0)
    {
        this.Execute(kind, kind.Label(), scopes, action, null, cancellationToken, minimumAfterBytes);
    }

    public void Execute(
        string operation,
        JournalScope[] scopes,
        Action action,
        Guid? reverts = null,
        CancellationToken cancellationToken = default)
    {
        this.Execute(
            reverts.HasValue ? JournalOperation.Revert : JournalOperations.FromLegacy(operation), operation, scopes, action, reverts,
            cancellationToken);
    }

    private void Execute(
        JournalOperation kind,
        string operation,
        JournalScope[] scopes,
        Action action,
        Guid? reverts,
        CancellationToken cancellationToken,
        long minimumAfterBytes = 0)
    {
        var started = Stopwatch.GetTimestamp();
        this.EnterGate(started, cancellationToken);
        try
        {
            using var storage = this.PrepareStorage();
            // The file lock also serializes edits from another editor process using this journal.
            using var lease = this.OpenWriterLock(started, cancellationToken);
            this.ExecuteLocked(
                kind, operation, scopes, action, reverts, cancellationToken: cancellationToken, minimumAfterBytes: minimumAfterBytes);
        }
        finally
        {
            Monitor.Exit(this._gate);
        }
    }

    private void ExecuteLocked(
        JournalOperation kind,
        string operation,
        JournalScope[] scopes,
        Action action,
        Guid? reverts,
        JournalState[]? before = null,
        CancellationToken cancellationToken = default,
        JournalCaptureBudget? budget = null,
        long minimumAfterBytes = 0)
    {
        budget ??= new JournalCaptureBudget(this._captureLimit);
        var namesBudget = new JournalCaptureBudget(this._captureLimit);
        namesBudget.Charge(scopes.LongLength * 512);
        scopes = scopes.Select(scope =>
        {
            if (scope.ValueNames is not { } names)
            {
                return scope;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!unique.Contains(name))
                {
                    namesBudget.ValueName(name);
                    unique.Add(name);
                }
            }

            return scope with { ValueNames = unique.ToArray() };
        }).ToArray();
        var entry = new JournalEntry(
            Guid.NewGuid(), DateTimeOffset.UtcNow, operation, before ?? scopes.Select(scope => this.Capture(scope, budget)).ToArray(), null,
            Reverts: reverts, Kind: kind);
        budget.Check(minimumAfterBytes);
        // Once the pending entry is saved, complete the mutation and its final record despite cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        this.Save(entry);
        Exception? failure = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            entry = entry with { After = scopes.Select(scope => this.Capture(scope, budget)).ToArray(), Error = failure?.Message };
            this.Save(entry);
        }
        catch (Exception ex)
        {
            throw new IOException(
                "The operation may have changed the registry, but its final journal state could not be saved. The before-state is retained. "
                + (failure is null ? string.Empty : "The mutation failed: " + failure.Message + " ")
                + ex.Message, failure is null ? ex : new AggregateException(failure, ex));
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void TrackEdits(Action action, List<JournalEntry> entries)
    {
        var previous = this._trackedEdits.Value;
        this._trackedEdits.Value = entries;
        try
        {
            action();
        }
        finally
        {
            this._trackedEdits.Value = previous;
        }
    }

    public void Revert(Guid id, CancellationToken cancellationToken = default)
    {
        this.Revert([id], cancellationToken);
    }

    public void Revert(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new ArgumentException("Select one or more distinct journal edits.", nameof(ids));
        }

        var started = Stopwatch.GetTimestamp();
        this.EnterGate(started, cancellationToken);
        try
        {
            using var storage = this.PrepareStorage();
            using var lease = this.OpenWriterLock(started, cancellationToken);
            var entries = this.ReadEntries();
            var byId = entries.ToDictionary(entry => entry.Id);
            var selected = ids.Reverse()
                .Select(id => byId.TryGetValue(id, out var entry)
                    ? entry
                    : throw new InvalidOperationException("This edit is missing from the journal. Inspect the history before continuing."))
                .Select(entry => (Entry: entry, UndoRename: RenameDirection(entry, byId))).ToArray();
            var reverted = JournalEntry.GetRevertedIds(entries);
            var ignored = reverted.Concat(ids).ToHashSet();
            var newerScopes = new List<JournalScope>();
            // Check the entire gesture before writing any of its inverse operations.
            foreach (var (entry, _) in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.RevertUnavailableReason(reverted) is { } reason)
                {
                    throw new InvalidOperationException(reason);
                }

                if (FindLaterConflict(entries, entry, ignored) is { } conflict)
                {
                    throw new IOException(conflict.Reason);
                }

                foreach (var state in entry.After!)
                {
                    if (!newerScopes.Any(scope => Overlaps(scope, state.Scope)))
                    {
                        CheckCurrent(state, this.Capture(state.Scope, new JournalCaptureBudget(this._captureLimit)));
                    }
                }

                newerScopes.AddRange(entry.After!.Select(state => state.Scope));
            }

            foreach (var (entry, undoRename) in selected)
            {
                var after = entry.After!;
                var budget = new JournalCaptureBudget(this._captureLimit);
                var before = after.Select(state => this.Capture(state.Scope, budget)).ToArray();
                for (var i = 0; i < after.Length; i++)
                {
                    CheckCurrent(after[i], before[i]);
                }

                this.ExecuteLocked(
                    JournalOperation.Revert, "Revert: " + entry.Operation, entry.Before.Select(state => state.Scope).ToArray(), () =>
                    {
                        // External writers can change the key while the pending journal record is flushed.
                        foreach (var state in after)
                        {
                            CheckCurrent(state, this.Capture(state.Scope, new JournalCaptureBudget(this._captureLimit)));
                        }

                        if (undoRename is { } reverse)
                        {
                            var source = after[reverse ? 1 : 0].Scope.Location;
                            var destination = entry.Before[reverse ? 0 : 1].Scope.Location;
                            this._registry.RenameKey(source, destination.Name);
                            return;
                        }

                        foreach (var pair in entry.Before.Zip(after))
                        {
                            this.Restore(pair.First, pair.Second);
                        }
                    }, entry.Id, before, cancellationToken, budget, JournalCaptureBudget.MinimumStateBytes(entry.Before));
            }
        }
        finally
        {
            Monitor.Exit(this._gate);
        }
    }

    private static bool? RenameDirection(JournalEntry entry, IReadOnlyDictionary<Guid, JournalEntry> entries)
    {
        var original = entry;
        var visited = new HashSet<Guid>();
        var reverse = true;
        while (original.OperationKind == JournalOperation.Revert && original.Reverts is { } parent)
        {
            if (!visited.Add(original.Id) || !entries.TryGetValue(parent, out var previous))
            {
                throw new InvalidDataException("This revert's original edit is missing or cyclic. Inspect the Journal before continuing.");
            }

            reverse = !reverse;
            original = previous;
        }

        if (original.OperationKind != JournalOperation.RenameKey)
        {
            return null;
        }

        if (entry.Before.Length != 2
            || entry.Before.Any(state => state.Scope.ValueNames is not null)
            || !entry.Before.Select(state => state.Scope.Location).SequenceEqual(original.Before.Select(state => state.Scope.Location)))
        {
            throw new InvalidDataException("This revert does not cover its original key rename. Nothing was changed.");
        }

        return reverse;
    }

    private static void CheckCurrent(JournalState expected, JournalState current)
    {
        if (!Same(expected, current)
            || expected.Scope.KeyPaths is { } paths && !paths.SequenceEqual(current.Scope.KeyPaths!, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException("Conflict: the registry no longer matches this edit's after-state. Nothing was reverted.");
        }
    }

    public static JournalConflict? FindLaterConflict(IReadOnlyList<JournalEntry> entries, JournalEntry entry, IReadOnlySet<Guid> reverted)
    {
        var protectedStates = entry.Before.Concat(entry.After ?? []).ToArray();
        foreach (var later in entries.SkipWhile(item => item.Id != entry.Id).Skip(1))
        {
            if (reverted.Contains(later.Id)
                || later.Reverts is { } original
                && later.OperationKind == JournalOperation.Revert
                && later.After is not null
                && later.Error is null
                && reverted.Contains(original))
            {
                continue;
            }

            var activity = later.ActivityLocation is { } location
                           && protectedStates.Any(state =>
                               RegistryKeyIdentity.SameComputer(location.Machine, state.Scope.Location.Machine));
            var touchesScope = later.Before.Concat(later.After ?? []).Any(state =>
                protectedStates.Any(earlier => Overlaps(state.Scope, earlier.Scope)));
            if (activity
                || touchesScope
                && (later.After is null
                    || !Enum.IsDefined(later.OperationKind)
                    || later.OperationKind == JournalOperation.Unknown
                    || later.Before.Zip(later.After).Any(pair => !Same(pair.First, pair.Second))))
            {
                if (protectedStates.Any(state => !RegistryKeyIdentity.IsLocal(state.Scope.Location.Machine)))
                {
                    return Conflict("A later journal entry may touch the same remote key or value. Its backing key cannot be verified.");
                }

                return Conflict(
                    activity
                        ? "A later journal entry records an external operation on this computer."
                        : "A later journal entry touches this key or value.");

                JournalConflict Conflict(string reason)
                {
                    var names = later.Before.SelectMany(state => state.Scope.ValueNames ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(4).ToArray();
                    var target = later.Path
                                 + (names.Length == 0
                                     ? string.Empty
                                     : " · "
                                       + string.Join(", ", names.Take(3).Select(name => name.Length == 0 ? "(Default)" : name))
                                       + (names.Length > 3 ? ", …" : string.Empty));
                    return new JournalConflict(
                        later,
                        $"{reason} {later.Operation} at {later.Time.ToLocalTime():G}: {target}. "
                        + "Revert or review this later edit first.");
                }
            }
        }

        return null;
    }

    public static bool Overlaps(JournalScope left, JournalScope right)
    {
        if (!RegistryKeyIdentity.SameComputer(left.Location.Machine, right.Location.Machine)
            || left.ValueNames is not null
            && right.ValueNames is not null
            && !left.ValueNames.Intersect(right.ValueNames, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        // Remote handles cannot prove that two aliases with overlapping value names identify distinct keys.
        if (!RegistryKeyIdentity.IsLocal(left.Location.Machine))
        {
            return true;
        }

        if (left.Location.View == right.Location.View
            && left.Location.Hive == right.Location.Hive
            && Intersects(left.Location.Path, right.Location.Path))
        {
            return true;
        }

        var leftPaths = left.KeyPaths ?? RegistryKeyIdentity.Paths(left.Location);
        var rightPaths = right.KeyPaths ?? RegistryKeyIdentity.Paths(right.Location);
        return leftPaths.Any(first => rightPaths.Any(second => Intersects(first, second)));

        bool Intersects(string first, string second)
        {
            return first.Equals(second, StringComparison.OrdinalIgnoreCase)
                   || left.ValueNames is null && RegistryKeyIdentity.AtOrBelow(second, first)
                   || right.ValueNames is null && RegistryKeyIdentity.AtOrBelow(first, second);
        }
    }

    private JournalState Capture(JournalScope scope, JournalCaptureBudget budget)
    {
        budget.Charge(
            512
            + 2 * JournalCaptureBudget.TextBytes(scope.Location.SubKey)
            + 2 * JournalCaptureBudget.TextBytes(scope.Location.Machine)
            + JournalCaptureBudget.TextBytes(Environment.MachineName));
        var paths = RegistryKeyIdentity.Paths(scope.Location);
        scope = scope with { KeyPaths = paths };
        foreach (var path in paths)
        {
            budget.Charge(JournalCaptureBudget.TextBytes(path));
        }

        if (scope.ValueNames is null)
        {
            var snapshot = RegistryBranchSnapshot.Capture(
                location => this._registry.Read(location, budget), scope.Location, allowMissingRoot: true, budget: budget);
            var security = snapshot.Keys.Select(key =>
            {
                budget.Charge(128 + JournalCaptureBudget.TextBytes(key.Path));
                using var handle = this._registry.Open(At(scope.Location, key.Path));
                var descriptor = handle
                    .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
                    .GetSecurityDescriptorSddlForm(
                        AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
                budget.Charge(JournalCaptureBudget.TextBytes(descriptor));
                return new JournalSecurity(key.Path, descriptor);
            }).ToArray();
            return new JournalState(scope, snapshot, security);
        }

        var values = new List<RegistryValue>();
        using (var key = this._registry.Open(scope.Location))
        {
            foreach (var name in scope.ValueNames)
            {
                budget.Charge(JournalCaptureBudget.TextBytes(name));
                budget.ValueName(name);
                if (RegistryService.ReadValue(key, name, budget) is { } value)
                {
                    values.Add(value);
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        return new JournalState(
            scope,
            new RegistryBranchSnapshot(
                1, scope.Location, now, now, [new SnapshotKey(string.Empty, values.ToArray())],
                scope.Location.Machine ?? Environment.MachineName), []);
    }

    private static RegistryLocation At(RegistryLocation root, string path)
    {
        return root with { SubKey = string.Join('\\', new[] { root.SubKey, path }.Where(part => part.Length > 0)) };
    }

    internal static bool Same(JournalState left, JournalState right)
    {
        if (left.Security.Length != right.Security.Length)
        {
            return false;
        }

        var security = new HashSet<JournalSecurity>(right.Security, SecurityComparer);
        return left.Security.All(security.Remove)
               && security.Count == 0
               && new RegistryComparison(left.Snapshot, right.Snapshot).Differences.Count == 0;
    }

    private void Restore(JournalState before, JournalState after)
    {
        var location = before.Scope.Location;
        if (before.Scope.ValueNames is { } names)
        {
            foreach (var name in names)
            {
                var previous = before.Snapshot.Keys[0].Values
                    .FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var expected = after.Snapshot.Keys[0].Values
                    .FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (previous is not null)
                {
                    this._registry.Write(location, previous, expected);
                }
                else if (expected is not null)
                {
                    this._registry.DeleteValue(location, expected);
                }
            }

            return;
        }

        if (before.Snapshot.Keys.Length == 0)
        {
            this._registry.DeleteKey(location);
            return;
        }

        if (after.Snapshot.Keys.Length != 0)
        {
            throw new InvalidOperationException("This branch operation cannot be safely reversed automatically.");
        }

        foreach (var key in before.Snapshot.Keys.OrderBy(key => key.Path.Count(c => c == '\\')).ThenBy(key => key.Path.Length))
        {
            var target = At(location, key.Path);
            this._registry.CreateKey(target.Parent ?? throw new InvalidOperationException("Cannot restore a root hive."), target.Name);
            foreach (var value in key.Values)
            {
                this._registry.Write(target, value, null);
            }
        }

        foreach (var security in before.Security.Reverse())
        {
            var target = At(location, security.Path);
            using var root = target.Machine is null
                ? RegistryKey.OpenBaseKey(target.Hive!.Value, target.View)
                : RemoteRegistryCredentials.OpenBaseKey(target.Hive!.Value, target.Machine, target.View);
            using var key = root.OpenSubKey(target.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)
                            ?? throw new IOException("The restored key disappeared before its permissions could be restored.");
            var descriptor = new RegistrySecurity();
            descriptor.SetSecurityDescriptorSddlForm(
                security.Descriptor, AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
            key.SetAccessControl(descriptor);
        }
    }

    private void Save(JournalEntry entry)
    {
        var file = Path.Combine(this._folder, entry.Id.ToString("N") + ".json");
        this.WriteFile(
            file, stream =>
            {
                using var bounded = new JournalWriteStream(stream, this._fileLimit);
                JsonSerializer.Serialize(bounded, entry, JournalJsonContext.Default.JournalEntry);
            });
        if (this._trackedEdits.Value is { } tracked)
        {
            var index = tracked.FindIndex(previous => previous.Id == entry.Id);
            if (index < 0)
            {
                tracked.Add(entry);
            }
            else
            {
                tracked[index] = entry;
            }
        }
    }

    private void WriteFile(string file, Action<FileStream> write)
    {
        try
        {
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = this.AdministratorOnly
                           ? AdministratorJournalDirectory.OpenFile(temporary, FileMode.CreateNew)
                           : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }

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
        catch (UnauthorizedAccessException ex)
        {
            throw StorageDenied(ex);
        }
    }

    private static void Validate(JournalEntry entry)
    {
        if (entry.Id == Guid.Empty
            || entry.Time == default
            || string.IsNullOrEmpty(entry.Operation)
            || entry.Before is null
            || entry.Before.Length == 0 && entry.Activity is null
            || entry.After is not null && entry.After.Length != entry.Before.Length)
        {
            throw new InvalidDataException("Invalid journal entry.");
        }

        if (entry.Activity is not null)
        {
            RegistryWorkspace.ValidateLocation(entry.ActivityLocation!, true);
        }

        foreach (var state in entry.Before.Concat(entry.After ?? []))
        {
            if (state?.Snapshot is null
                || state.Scope?.Location is null
                || state.Scope.Location != state.Snapshot.Root
                || state.Security is null
                || state.Scope.KeyPaths is { } paths
                && (paths.Length is < 1 or > 2
                    || paths.Any(path =>
                        string.IsNullOrEmpty(path) || !path.StartsWith(@"\REGISTRY\", StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidDataException("Invalid journal scope.");
            }

            state.Snapshot.Validate();
            if (state.Scope.ValueNames is { } names)
            {
                foreach (var name in names)
                {
                    RegistryService.ValidateValueName(name);
                }
            }

            var securityPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var security in state.Security)
            {
                if (security?.Path is null || string.IsNullOrEmpty(security.Descriptor) || !securityPaths.Add(security.Path))
                {
                    throw new InvalidDataException("Invalid journal security descriptor.");
                }

                var descriptor = new RegistrySecurity();
                descriptor.SetSecurityDescriptorSddlForm(security.Descriptor);
            }
        }

        if (entry.OperationKind == JournalOperation.RenameKey
            && (entry.Before.Length != 2
                || entry.Before.Any(state => state.Scope.ValueNames is not null)
                || entry.Before[0].Scope.Location.Parent is not { } parent
                || parent != entry.Before[1].Scope.Location.Parent))
        {
            throw new InvalidDataException("Invalid journal key rename.");
        }

        if (entry.After is not null)
        {
            foreach (var pair in entry.Before.Zip(entry.After))
            {
                var beforeNames = pair.First.Scope.ValueNames;
                var afterNames = pair.Second.Scope.ValueNames;
                if (beforeNames is null
                        ? afterNames is not null
                        : afterNames is null || !beforeNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(afterNames))
                {
                    throw new InvalidDataException("The before and after states cover different journal targets.");
                }

                RegistryComparison.ValidateRoots(pair.First.Snapshot, pair.Second.Snapshot);
            }
        }
    }
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true, MaxDepth = 32)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
