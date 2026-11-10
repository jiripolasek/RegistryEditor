// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Registry;

public sealed class NavigationHistory
{
    private readonly List<RegistryLocation> _items;
    private int _index;

    public RegistryLocation Current => this._items[this._index];

    public bool CanGoBack => this._index > 0;

    public bool CanGoForward => this._index < this._items.Count - 1;

    public RegistryLocation BackTarget => this.CanGoBack ? this._items[this._index - 1] : this.Current;

    public RegistryLocation ForwardTarget => this.CanGoForward ? this._items[this._index + 1] : this.Current;

    public NavigationHistory(RegistryLocation initial)
    {
        this._items = [initial];
    }

    public IReadOnlyList<(int Index, RegistryLocation Location)> GetEntries(bool forward)
    {
        var entries = this._items.Select((location, index) => (Index: index, Location: location));
        return (forward ? entries.Skip(this._index + 1) : entries.Take(this._index).Reverse()).ToArray();
    }

    public RegistryLocation? GetTarget(int index)
    {
        return index >= 0 && index < this._items.Count ? this._items[index] : null;
    }

    public RegistryLocation MoveTo(int index)
    {
        var target = this._items[index];
        this._index = index;
        return target;
    }

    public void Navigate(RegistryLocation location)
    {
        if (this.Current == location)
        {
            return;
        }

        this._items.RemoveRange(this._index + 1, this._items.Count - this._index - 1);
        this._items.Add(location);
        this._index++;
    }

    public RegistryLocation Back()
    {
        return this.CanGoBack ? this._items[--this._index] : this.Current;
    }

    public RegistryLocation Forward()
    {
        return this.CanGoForward ? this._items[++this._index] : this.Current;
    }
}
