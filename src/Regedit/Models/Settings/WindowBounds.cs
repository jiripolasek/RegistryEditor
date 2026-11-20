// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Settings;

public sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    internal bool IsValid => this.Width is > 0 and <= 100000
                             && this.Height is > 0 and <= 100000
                             && this.X is >= -1000000 and <= 1000000
                             && this.Y is >= -1000000 and <= 1000000;
}
