// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Models.Lists;
using Microsoft.UI.Dispatching;

namespace JPSoftworks.Regedit.Controls.Shared;

/// <summary>Shares column layouts within a window and debounces settings writes until dragging settles.</summary>
internal sealed class ColumnLayoutPersistence
{
    private readonly Func<string, ColumnLayout?> _load;
    private readonly Action<string, ColumnLayout> _save;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, ColumnLayout> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WeakReference<ColumnListView>>> _lists = new(StringComparer.Ordinal);

    internal ColumnLayoutPersistence(DispatcherQueue dispatcher, Func<string, ColumnLayout?> load, Action<string, ColumnLayout> save)
    {
        this._load = load;
        this._save = save;
        this._timer = dispatcher.CreateTimer();
        this._timer.Interval = TimeSpan.FromMilliseconds(400);
        this._timer.IsRepeating = false;
        this._timer.Tick += (_, _) => this.Flush();
    }

    internal void Attach(ColumnListView list, string listId)
    {
        list.Layout = this._pending.GetValueOrDefault(listId) ?? this._load(listId);
        if (!this._lists.TryGetValue(listId, out var peers))
        {
            peers = [];
            this._lists.Add(listId, peers);
        }

        peers.Add(new WeakReference<ColumnListView>(list));
        list.LayoutChanged += (_, layout) =>
        {
            this._pending[listId] = layout;
            peers.RemoveAll(reference => !reference.TryGetTarget(out var target));
            foreach (var reference in peers)
            {
                if (reference.TryGetTarget(out var peer) && !ReferenceEquals(peer, list))
                {
                    peer.Layout = layout;
                }
            }

            this._timer.Stop();
            this._timer.Start();
        };
        list.Unloaded += (_, _) => this.Flush();
    }

    internal void Flush()
    {
        this._timer.Stop();
        var pending = this._pending.ToArray();
        this._pending.Clear();
        foreach (var (listId, layout) in pending)
        {
            this._save(listId, layout);
        }
    }
}
