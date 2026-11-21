// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.Services;

internal sealed class SearchHistoryStore(
    Func<AppSettings> load, string settingsPath, ILogger<SearchHistoryStore> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Lazy<IReadOnlyList<string>> _initialQueries = new(() => load().RecentSearches);
    private readonly Queue<string> _pending = new();
    private IReadOnlyList<string>? _savedQueries;
    private Task _lastWrite = Task.CompletedTask;
    private bool _disposed;

    internal IReadOnlyList<string> Queries
    {
        get
        {
            var initial = Volatile.Read(ref this._savedQueries) ?? this._initialQueries.Value;
            lock (this._gate)
            {
                var saved = this._savedQueries ?? initial;
                return this._pending.Count == 0
                    ? saved
                    : AppSettings.NormalizeRecentSearches(this._pending.Reverse().Concat(saved));
            }
        }
    }

    internal Task RecordAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.CompletedTask;
        }

        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            this._pending.Enqueue(query);
            return this._lastWrite = this._lastWrite.ContinueWith(
                _ => this.Persist(query), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Persist(string query)
    {
        IReadOnlyList<string>? saved = null;
        try
        {
            var settings = load();
            if (settings.RecordSearch(query, settingsPath))
            {
                saved = settings.RecentSearches;
            }
        }
        catch (Exception ex)
        {
            logger.LogFailure(LogLevel.Warning, "Save registry search history", ex);
        }
        finally
        {
            lock (this._gate)
            {
                if (saved is not null)
                {
                    Volatile.Write(ref this._savedQueries, saved);
                }

                this._pending.Dequeue();
            }
        }
    }

    public void Dispose()
    {
        Task pending;
        lock (this._gate)
        {
            this._disposed = true;
            pending = this._lastWrite;
        }

        pending.GetAwaiter().GetResult();
    }
}
