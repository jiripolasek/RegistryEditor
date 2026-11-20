// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json.Serialization;

namespace JPSoftworks.Regedit.Models.Lists;

/// <summary>
///     Immutable order, visibility and widths of a list's columns. Every operation returns a new layout, or the same
///     instance when the request is not allowed, so hosts can compare references to detect changes.
/// </summary>
public sealed record ColumnLayout(IReadOnlyList<ColumnState> Columns)
{
    [JsonIgnore]
    public IEnumerable<ColumnState> Visible => this.Columns.Where(column => column.Visible);

    public static ColumnLayout Default(IReadOnlyList<ColumnSpec> specs)
    {
        return new ColumnLayout(
            specs.Select(spec => new ColumnState(spec.Id, spec.ClampWidth(spec.DefaultWidth), spec.DefaultVisible || !spec.CanHide))
                .ToArray()).EnsureVisible(specs);
    }

    /// <summary>
    ///     Drops unknown and duplicate ids, inserts missing columns near their declared position with their defaults,
    ///     clamps widths, keeps non-hideable columns visible and guarantees one visible column.
    /// </summary>
    public ColumnLayout Normalize(IReadOnlyList<ColumnSpec> specs)
    {
        var known = specs.ToDictionary(spec => spec.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<ColumnState>();
        foreach (var column in this.Columns ?? [])
        {
            if (column?.Id is null || !known.TryGetValue(column.Id, out var spec) || !seen.Add(column.Id))
            {
                continue;
            }

            columns.Add(new ColumnState(column.Id, spec.ClampWidth(column.Width), column.Visible || !spec.CanHide));
        }

        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            if (seen.Add(spec.Id))
            {
                columns.Insert(
                    Math.Min(index, columns.Count),
                    new ColumnState(spec.Id, spec.ClampWidth(spec.DefaultWidth), spec.DefaultVisible || !spec.CanHide));
            }
        }

        return new ColumnLayout(columns).EnsureVisible(specs);
    }

    public ColumnState? Find(string id)
    {
        return this.Columns.FirstOrDefault(column => column.Id == id);
    }

    public int IndexOf(string id)
    {
        for (var index = 0; index < this.Columns.Count; index++)
        {
            if (this.Columns[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    public ColumnLayout Resize(ColumnSpec spec, double width)
    {
        var index = this.IndexOf(spec.Id);
        if (index < 0 || !spec.CanResize || !double.IsFinite(width))
        {
            return this;
        }

        var clamped = Math.Clamp(width, spec.MinWidth, Math.Max(spec.MinWidth, spec.MaxWidth));
        return this.Columns[index].Width == clamped ? this : this.Replace(index, this.Columns[index] with { Width = clamped });
    }

    /// <summary>Moves <paramref name="id" /> to the slot currently held by <paramref name="targetId" />.</summary>
    public ColumnLayout Move(string id, string targetId, IReadOnlyList<ColumnSpec> specs)
    {
        var from = this.IndexOf(id);
        var to = this.IndexOf(targetId);
        if (from < 0 || to < 0 || from == to || !CanReorder(specs, id) || !CanReorder(specs, targetId))
        {
            return this;
        }

        var columns = this.Columns.ToList();
        var moved = columns[from];
        columns.RemoveAt(from);
        columns.Insert(to, moved);
        return new ColumnLayout(columns);
    }

    /// <summary>
    ///     Drops <paramref name="id" /> into the gap before <paramref name="beforeId" />, or after the last visible column
    ///     when it is null. Returns the same instance when nothing would change or when the move would shift the
    ///     visible position of a column that cannot be reordered.
    /// </summary>
    public ColumnLayout MoveBefore(string id, string? beforeId, IReadOnlyList<ColumnSpec> specs)
    {
        var from = this.IndexOf(id);
        if (from < 0 || id == beforeId || !CanReorder(specs, id))
        {
            return this;
        }

        var columns = this.Columns.ToList();
        var moved = columns[from];
        columns.RemoveAt(from);
        var to = beforeId is null
            ? columns.FindLastIndex(column => column.Visible) + 1
            : columns.FindIndex(column => column.Id == beforeId);
        if (to < 0)
        {
            return this;
        }

        columns.Insert(to, moved);
        var next = new ColumnLayout(columns);
        if (next.IsEquivalentTo(this))
        {
            return this;
        }

        var pinned = specs.Where(spec => !spec.CanReorder).Select(spec => spec.Id).ToHashSet(StringComparer.Ordinal);
        return pinned.All(pin => this.VisibleIndex(pin) == next.VisibleIndex(pin)) ? next : this;
    }

    private int VisibleIndex(string id)
    {
        var index = 0;
        foreach (var column in this.Visible)
        {
            if (column.Id == id)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>Moves a column past its nearest visible, reorderable neighbour; -1 is left and 1 is right.</summary>
    public ColumnLayout MoveBy(string id, int direction, IReadOnlyList<ColumnSpec> specs)
    {
        return this.Neighbour(id, direction, specs) is { } target ? this.Move(id, target, specs) : this;
    }

    public string? Neighbour(string id, int direction, IReadOnlyList<ColumnSpec> specs)
    {
        var index = this.IndexOf(id);
        if (index < 0 || direction == 0)
        {
            return null;
        }

        for (var next = index + Math.Sign(direction); next >= 0 && next < this.Columns.Count; next += Math.Sign(direction))
        {
            if (this.Columns[next].Visible)
            {
                return CanReorder(specs, this.Columns[next].Id) ? this.Columns[next].Id : null;
            }
        }

        return null;
    }

    public bool CanToggle(string id, IReadOnlyList<ColumnSpec> specs)
    {
        var column = this.Find(id);
        var spec = specs.FirstOrDefault(spec => spec.Id == id);
        return column is not null && spec is not null && (!column.Visible || (spec.CanHide && this.Visible.Skip(1).Any()));
    }

    /// <summary>Shows a hidden column or hides a visible one, never hiding the last or a non-hideable column.</summary>
    public ColumnLayout Toggle(string id, IReadOnlyList<ColumnSpec> specs)
    {
        if (!this.CanToggle(id, specs))
        {
            return this;
        }

        var index = this.IndexOf(id);
        return this.Replace(index, this.Columns[index] with { Visible = !this.Columns[index].Visible });
    }

    /// <summary>True when both layouts have the same columns in the same order, visibility and widths.</summary>
    public bool IsEquivalentTo(ColumnLayout? other)
    {
        return other is not null && this.Columns.SequenceEqual(other.Columns);
    }

    private ColumnLayout EnsureVisible(IReadOnlyList<ColumnSpec> specs)
    {
        if (this.Columns.Count == 0 || this.Columns.Any(column => column.Visible))
        {
            return this;
        }

        var first = specs.Select(spec => this.IndexOf(spec.Id)).FirstOrDefault(index => index >= 0);
        return this.Replace(first, this.Columns[first] with { Visible = true });
    }

    private ColumnLayout Replace(int index, ColumnState column)
    {
        var columns = this.Columns.ToArray();
        columns[index] = column;
        return new ColumnLayout(columns);
    }

    private static bool CanReorder(IReadOnlyList<ColumnSpec> specs, string id)
    {
        return specs.FirstOrDefault(spec => spec.Id == id)?.CanReorder ?? false;
    }
}
