// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json.Serialization;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;

namespace JPSoftworks.Regedit.Core.Journal;

public sealed record JournalEntry(
    Guid Id,
    DateTimeOffset Time,
    string Operation,
    JournalState[] Before,
    JournalState[]? After,
    string? Error = null,
    Guid? Reverts = null,
    RegistryLocation? ActivityLocation = null,
    string? Activity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JournalOperation? Kind = null)
{
    public JournalOperation OperationKind => this.Kind
                                             ?? (this.Activity is not null ? JournalOperation.Activity :
                                                 this.Reverts.HasValue ? JournalOperation.Revert :
                                                 JournalOperations.FromLegacy(this.Operation));

    public string Summary => $"{this.Time.ToLocalTime():G} · {this.Operation}";

    public string Path => this.Before.FirstOrDefault()?.Scope.Location.Address ?? this.ActivityLocation?.Address ?? string.Empty;

    public string Status => this.After is null ? "Incomplete; inspect the registry before continuing" :
        this.Error is not null ? "Failed or partially applied: " + this.Error :
        this.Activity is not null ? "Recorded activity; automatic revert unavailable" : "Completed";

    [JsonIgnore]
    public bool HasChanges => this.After is { } after
                              && (this.OperationKind == JournalOperation.RenameKey
                                  || this.Before.Zip(after).Any(pair => !RegistryJournal.Same(pair.First, pair.Second)));

    [JsonIgnore]
    public IReadOnlyList<RegistryDifference> Differences => this.After is null
        ? []
        : this.Before.Zip(this.After).SelectMany(pair => new RegistryComparison(pair.First.Snapshot, pair.Second.Snapshot).Differences)
            .ToArray();

    public string? RevertUnavailableReason(IReadOnlySet<Guid> reverted)
    {
        if (this.Activity is not null || this.OperationKind == JournalOperation.Activity)
        {
            return "This external Windows operation is recorded as activity. Its prior data is not available for automatic revert.";
        }

        if (this.After is null || this.Error is not null)
        {
            return "Incomplete or partially failed operations require manual review.";
        }

        if (this.OperationKind == JournalOperation.Unknown || !Enum.IsDefined(this.OperationKind))
        {
            return "This journal operation is unknown. Inspect the recorded change before continuing.";
        }

        return reverted.Contains(this.Id) ? "This edit was already reverted." : null;
    }

    public static HashSet<Guid> GetRevertedIds(IReadOnlyList<JournalEntry> history)
    {
        var reverted = new HashSet<Guid>();
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var item = history[i];
            if (item.Reverts is { } original
                && item.OperationKind == JournalOperation.Revert
                && item.After is not null
                && item.Error is null
                && !reverted.Contains(item.Id))
            {
                reverted.Add(original);
            }
        }

        return reverted;
    }
}
