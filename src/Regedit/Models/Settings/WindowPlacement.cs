// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Settings;

public sealed record WindowPlacement(WindowBounds Bounds, WindowBounds WorkArea, double Scale = 1, bool Maximized = false)
{
    internal bool IsValid => this.Bounds is { IsValid: true }
                             && this.WorkArea is { IsValid: true }
                             && double.IsFinite(this.Scale)
                             && this.Scale is >= 0.5 and <= 8;

    internal WindowBounds Restore(WindowBounds workArea, double scale)
    {
        var ratio = scale / this.Scale;
        var width = (int)Math.Clamp(Math.Round(this.Bounds.Width * ratio), Math.Min(640 * scale, workArea.Width), workArea.Width);
        var height = (int)Math.Clamp(Math.Round(this.Bounds.Height * ratio), Math.Min(480 * scale, workArea.Height), workArea.Height);
        var x = (int)Math.Clamp(
            Math.Round(workArea.X + (this.Bounds.X - this.WorkArea.X) * ratio), workArea.X, (double)workArea.X + workArea.Width - width);
        var y = (int)Math.Clamp(
            Math.Round(workArea.Y + (this.Bounds.Y - this.WorkArea.Y) * ratio), workArea.Y, (double)workArea.Y + workArea.Height - height);
        return new WindowBounds(x, y, width, height);
    }
}
