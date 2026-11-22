// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Services;

internal sealed record InspectionVisit(RegistryLocation Location, string ValueName, DateTimeOffset LastInspectedUtc)
{
    public string DisplayName => this.ValueName.Length == 0 ? "(Default)" : this.ValueName;

    public string Address => this.Location.Address;

    public string Details =>
        $"{(this.Location.View == RegistryView.Registry32 ? "32-bit" : "64-bit")} · {this.LastInspectedUtc.LocalDateTime:g}";
}

internal sealed record InspectionHistory(bool IsRecording, InspectionVisit[] Visits);

internal sealed class InspectionHistoryStore(string path, ILogger<InspectionHistoryStore> logger, TimeProvider? timeProvider = null)
    : IDisposable
{
    private const int VisitLimit = 100;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly InspectionHistory Empty = new(false, []);
    private readonly Lock _gate = new();
    private Task _lastUpdate = Task.CompletedTask;
    private bool _disposed;

    internal Task<InspectionHistory> ReadAsync() => this.UpdateAsync((history, _) => history);

    internal Task<InspectionHistory> SetRecordingAsync(bool enabled) => this.UpdateAsync((history, _) =>
        history.IsRecording == enabled ? history : history with { IsRecording = enabled }, create: enabled);

    internal Task<InspectionHistory> ClearAsync() => this.UpdateAsync((history, _) =>
        history.Visits.Length == 0 ? history : history with { Visits = [] });

    internal async Task RecordAsync(RegistryLocation location, string valueName)
    {
        try
        {
            await this.UpdateAsync((history, now) =>
            {
                if (!history.IsRecording)
                {
                    return history;
                }

                RegistryWorkspace.ValidateLocation(location);
                var visits = history.Visits.Where(visit => !visit.Location.IsSameLocation(location)
                                                           || !visit.ValueName.Equals(valueName, StringComparison.OrdinalIgnoreCase));
                return history with { Visits = visits.Prepend(new InspectionVisit(location, valueName, now)).Take(VisitLimit).ToArray() };
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.LogFailure(LogLevel.Warning, "Save inspection history", ex);
        }
    }

    private Task<InspectionHistory> UpdateAsync(Func<InspectionHistory, DateTimeOffset, InspectionHistory> update, bool create = false)
    {
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            var pending = this._lastUpdate.ContinueWith(
                _ => this.Update(update, create), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            this._lastUpdate = pending;
            return pending;
        }
    }

    private InspectionHistory Update(Func<InspectionHistory, DateTimeOffset, InspectionHistory> update, bool create)
    {
        if (!create && !File.Exists(path))
        {
            return Empty;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = ProfileFileLock.Acquire(path + ".lock", "Another window is updating inspection history. Try again shortly.");
        var previous = this.Load();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var retained = previous.Visits.Where(visit => visit.LastInspectedUtc >= now - Retention).Take(VisitLimit).ToArray();
        var current = update(retained.Length == previous.Visits.Length ? previous : previous with { Visits = retained }, now);
        if (!ReferenceEquals(current, previous))
        {
            this.Save(current);
        }

        return current;
    }

    private InspectionHistory Load()
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        var history = JsonSerializer.Deserialize(File.ReadAllText(path), InspectionHistoryJsonContext.Default.InspectionHistory);
        if (history?.Visits is null || history.Visits.Any(visit => visit?.ValueName is null))
        {
            throw new JsonException("Inspection history is incomplete.");
        }

        foreach (var visit in history.Visits)
        {
            RegistryWorkspace.ValidateLocation(visit.Location);
        }

        return history;
    }

    private void Save(InspectionHistory history)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(history, InspectionHistoryJsonContext.Default.InspectionHistory));
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        Task pending;
        lock (this._gate)
        {
            this._disposed = true;
            pending = this._lastUpdate;
        }

        pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
    }
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(InspectionHistory))]
internal sealed partial class InspectionHistoryJsonContext : JsonSerializerContext;
