// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Infrastructure.Input;

internal sealed class MiddleClickGesture
{
    private uint _pointerId;
    private object? _target;

    public void Press(uint pointerId, object? target)
    {
        this._pointerId = pointerId;
        this._target = target;
    }

    public bool Release(uint pointerId, object? target)
    {
        // Identity prevents a recycled row from opening a different item.
        var clicked = this._target is not null && this._pointerId == pointerId && ReferenceEquals(this._target, target);
        this.Cancel();
        return clicked;
    }

    public void Cancel()
    {
        this._target = null;
    }
}
