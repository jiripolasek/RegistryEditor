// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JPSoftworks.Regedit.Infrastructure.Collections;

internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    internal void AddRange(IEnumerable<T> items, bool reset = true)
    {
        var additions = items as T[] ?? items.ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        if (additions.Length == 1)
        {
            this.Add(additions[0]);
            return;
        }

        this.CheckReentrancy();
        foreach (var item in additions)
        {
            this.Items.Add(item);
            if (!reset)
            {
                this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, this.Count - 1));
            }
        }

        this.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.Count)));
        this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        if (reset)
        {
            this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    internal void ReplaceAll(IEnumerable<T> items)
    {
        var replacement = items as T[] ?? items.ToArray();
        this.CheckReentrancy();
        var countChanged = this.Count != replacement.Length;
        this.Items.Clear();
        foreach (var item in replacement)
        {
            this.Items.Add(item);
        }

        if (countChanged)
        {
            this.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.Count)));
        }

        this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
