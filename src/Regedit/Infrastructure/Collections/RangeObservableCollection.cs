// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JPSoftworks.Regedit.Infrastructure.Collections;

/// <summary>
///     An <see cref="ObservableCollection{T}" /> with range operations and deferred notifications.
///     <para>
///         WinUI item controls accept only single-item change notifications, so every multi-item change is reported as
///         one Reset; single-item changes keep precise Add/Remove notifications. Operations that change nothing raise
///         nothing. Within <see cref="DeferNotifications" /> all changes coalesce into one Reset when the outermost scope
///         ends. Source-compatible with the app's <c>BulkObservableCollection.AddRange/ReplaceAll</c>.
///     </para>
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>, IRangeList
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetChanged = new(NotifyCollectionChangedAction.Reset);

    private int _deferDepth;
    private bool _deferredChange;
    private int _deferredCount;

    public bool IsDeferring => this._deferDepth > 0;

    public RangeObservableCollection()
    {
    }

    public RangeObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    void IRangeList.ReplaceAll(IEnumerable items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var typed = new List<T>();
        foreach (var item in items)
        {
            typed.Add(
                item is T value || (item is null && default(T) is null)
                    ? (T)item!
                    : throw new ArgumentException($"Items must be of type {typeof(T).Name}.", nameof(items)));
        }

        this.ReplaceAll(typed);
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var additions = Snapshot(items);
        if (additions.Count == 0)
        {
            return;
        }

        if (additions.Count == 1 && !this.IsDeferring)
        {
            this.Add(additions[0]);
            return;
        }

        this.CheckReentrancy();
        var before = this.Count;
        foreach (var item in additions)
        {
            this.Items.Add(item);
        }

        this.NotifyBatch(before);
    }

    /// <summary>Removes each listed item once (first occurrence); returns how many were removed.</summary>
    public int RemoveRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var removals = Snapshot(items);
        if (removals.Count == 0)
        {
            return 0;
        }

        if (removals.Count == 1 && !this.IsDeferring)
        {
            return this.Remove(removals[0]) ? 1 : 0;
        }

        // Count occurrences so large removals stay linear instead of one IndexOf per item.
        var pending = new Dictionary<Wrapper, int>();
        foreach (var item in removals)
        {
            pending[new Wrapper(item)] = pending.GetValueOrDefault(new Wrapper(item)) + 1;
        }

        return this.RemoveWhere(item =>
        {
            var key = new Wrapper(item);
            if (!pending.TryGetValue(key, out var left) || left == 0)
            {
                return false;
            }

            pending[key] = left - 1;
            return true;
        });
    }

    /// <summary>Removes every item matching <paramref name="match" />; returns how many were removed.</summary>
    public int RemoveAll(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return this.RemoveWhere(item => match(item));
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var replacement = Snapshot(items);
        if (replacement.Count == this.Count && replacement.SequenceEqual(this.Items, EqualityComparer<T>.Default))
        {
            return;
        }

        this.CheckReentrancy();
        var before = this.Count;
        this.Items.Clear();
        foreach (var item in replacement)
        {
            this.Items.Add(item);
        }

        this.NotifyBatch(before);
    }

    /// <summary>
    ///     Suspends notifications until the returned scope is disposed; nested scopes are allowed. Any change made
    ///     inside raises a single Reset (and Count when it differs) when the outermost scope ends.
    /// </summary>
    public IDisposable DeferNotifications()
    {
        this.CheckReentrancy();
        if (this._deferDepth++ == 0)
        {
            this._deferredChange = false;
            this._deferredCount = this.Count;
        }

        return new DeferScope(this);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (this.IsDeferring)
        {
            this._deferredChange = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!this.IsDeferring)
        {
            base.OnPropertyChanged(e);
        }
    }

    private int RemoveWhere(Func<T, bool> match)
    {
        this.CheckReentrancy();
        var before = this.Count;
        var kept = new List<T>(before);
        foreach (var item in this.Items)
        {
            if (!match(item))
            {
                kept.Add(item);
            }
        }

        var removed = before - kept.Count;
        if (removed == 0)
        {
            return 0;
        }

        this.Items.Clear();
        foreach (var item in kept)
        {
            this.Items.Add(item);
        }

        this.NotifyBatch(before);
        return removed;
    }

    private void NotifyBatch(int countBefore)
    {
        if (this.IsDeferring)
        {
            this._deferredChange = true;
            return;
        }

        if (this.Count != countBefore)
        {
            this.OnPropertyChanged(CountChanged);
        }

        this.OnPropertyChanged(IndexerChanged);
        this.OnCollectionChanged(ResetChanged);
    }

    private void EndDefer()
    {
        if (--this._deferDepth > 0 || !this._deferredChange)
        {
            return;
        }

        this._deferredChange = false;
        if (this.Count != this._deferredCount)
        {
            base.OnPropertyChanged(CountChanged);
        }

        base.OnPropertyChanged(IndexerChanged);
        base.OnCollectionChanged(ResetChanged);
    }

    private static IReadOnlyList<T> Snapshot(IEnumerable<T> items)
    {
        // Copy first so passing this collection (or a view over it) cannot observe its own mutation.
        return items as T[] ?? items.ToArray();
    }

    // Dictionary keys cannot be null; wrap so null items can be counted too.
    private readonly record struct Wrapper(T Value);

    private sealed class DeferScope(RangeObservableCollection<T> owner) : IDisposable
    {
        private RangeObservableCollection<T>? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref this._owner, null)?.EndDefer();
        }
    }
}
