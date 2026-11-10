// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Printing;

internal sealed class RegistryPrintLayoutGeneration
{
    private long _current;

    internal long Current => this._current;

    internal long Begin()
    {
        return ++this._current;
    }

    internal void Cancel()
    {
        this._current++;
    }

    internal bool IsCurrent(long generation)
    {
        return generation == this._current;
    }

    internal bool CompleteSynchronously(long generation, Func<bool> hasMore, Action buildNext)
    {
        while (this.IsCurrent(generation) && hasMore())
        {
            buildNext();
        }

        return this.IsCurrent(generation) && !hasMore();
    }
}
