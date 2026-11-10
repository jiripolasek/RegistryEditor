// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Models.Lists;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ColumnLayoutTests
{
    private static readonly ColumnSpec[] Specs =
    [
        new("Name", 0, 80, FillWeight: 2, CanHide: false),
        new("Type", 170, 80),
        new("Data", 0, 80, FillWeight: 3),
        new("Size", 110, 60, DefaultVisible: false),
        new("Pinned", 40, 40, CanResize: false, CanReorder: false)
    ];

    internal static void Run(Action<bool, string> check)
    {
        var layout = ColumnLayout.Default(Specs);
        check(
            layout.Columns.Select(column => column.Id).SequenceEqual(["Name", "Type", "Data", "Size", "Pinned"]) &&
            !layout.Find("Size")!.Visible && layout.Find("Type")!.Width == 170 && layout.Find("Name")!.Width == 0,
            "Default layouts follow declaration order, default widths and default visibility");

        var stale = new ColumnLayout(
        [
            new ColumnState("Data", double.NaN, true),
            new ColumnState("Removed", 100, true),
            new ColumnState("Name", 5, false),
            new ColumnState("Data", 300, false),
            new ColumnState("Type", 99999, true)
        ]);
        var normalized = stale.Normalize(Specs);
        check(
            normalized.Columns.Select(column => column.Id).SequenceEqual(["Data", "Name", "Type", "Size", "Pinned"]),
            "Normalizing drops unknown and duplicate ids and inserts missing columns near their declared slot");
        check(
            normalized.Find("Data")!.Width == 0 && normalized.Find("Name")!.Width == 80 &&
            normalized.Find("Type")!.Width == 1600,
            "Normalizing replaces invalid widths with defaults and clamps to the column limits");
        check(normalized.Find("Name")!.Visible, "Normalizing keeps non-hideable columns visible");
        check(
            new ColumnLayout([new ColumnState("Type", 100, false), new ColumnState("Data", 0, false)])
                .Normalize([Specs[1], Specs[2]]).Visible.Count() == 1, "Normalizing guarantees one visible column");
        check(
            new ColumnLayout(null!).Normalize(Specs).Columns.Count == Specs.Length,
            "A missing column list from old settings normalizes to defaults");

        check(layout.Resize(Specs[1], 20).Find("Type")!.Width == 80, "Resizing clamps to the minimum width");
        check(layout.Resize(Specs[1], 5000).Find("Type")!.Width == 1600, "Resizing clamps to the maximum width");
        check(layout.Resize(Specs[2], 240).Find("Data")!.Width == 240, "Resizing a fill column fixes its width");
        check(ReferenceEquals(layout.Resize(Specs[4], 300), layout), "Fixed-size columns ignore resizing");
        check(ReferenceEquals(layout.Resize(Specs[1], 170), layout), "Resizing to the same width keeps the instance");

        var moved = layout.Move("Data", "Name", Specs);
        check(
            moved.Columns.Select(column => column.Id).Take(3).SequenceEqual(["Data", "Name", "Type"]),
            "Moving left takes the target's slot");
        check(
            layout.Move("Name", "Data", Specs).Columns.Select(column => column.Id).Take(3)
                .SequenceEqual(["Type", "Data", "Name"]), "Moving right takes the target's slot");
        check(
            ReferenceEquals(layout.Move("Data", "Pinned", Specs), layout) &&
            ReferenceEquals(layout.Move("Pinned", "Name", Specs), layout),
            "Non-reorderable columns neither move nor accept moved columns");
        check(
            layout.MoveBy("Type", 1, Specs).Columns.Select(column => column.Id).Take(3)
                .SequenceEqual(["Name", "Data", "Type"]), "Move right skips to the next visible column");
        check(
            layout.MoveBy("Data", 1, Specs) == layout && layout.Neighbour("Data", 1, Specs) is null,
            "Moving past hidden columns stops at a fixed neighbour");
        check(layout.Neighbour("Name", -1, Specs) is null, "The first column has no left neighbour");
        check(
            layout.MoveBefore("Data", "Name", Specs).Columns.Select(column => column.Id).Take(3)
                .SequenceEqual(["Data", "Name", "Type"]), "Dropping before a column inserts into that gap");
        check(
            layout.MoveBefore("Name", "Data", Specs).Columns.Select(column => column.Id).Take(3)
                .SequenceEqual(["Type", "Name", "Data"]),
            "Dropping rightwards lands just before the target, not on its slot");
        var free = new ColumnSpec[] { Specs[0], Specs[1], Specs[2], Specs[3] };
        var freeLayout = ColumnLayout.Default(free);
        check(
            freeLayout.MoveBefore("Name", null, free).Columns.Select(column => column.Id)
                .SequenceEqual(["Type", "Data", "Name", "Size"]),
            "Dropping at the end places the column after the last visible one, before hidden columns");
        check(
            ReferenceEquals(layout.MoveBefore("Type", "Data", Specs), layout) && ReferenceEquals(
                layout.MoveBefore("Type", "Type", Specs), layout),
            "Dropping into the column's own gap changes nothing");
        check(
            ReferenceEquals(layout.MoveBefore("Name", null, Specs), layout) && ReferenceEquals(
                layout.MoveBefore("Pinned", "Name", Specs), layout),
            "Drops that would shift or move a fixed column are refused");
        check(
            ReferenceEquals(layout.MoveBefore("Ghost", "Name", Specs), layout) && ReferenceEquals(
                layout.MoveBefore("Name", "Ghost", Specs), layout), "Unknown columns cannot be dropped or targeted");

        var hidden = layout.Toggle("Type", Specs);
        check(
            !hidden.Find("Type")!.Visible && hidden.Toggle("Type", Specs).Find("Type")!.Visible,
            "Toggle hides and shows");
        check(ReferenceEquals(layout.Toggle("Name", Specs), layout), "Non-hideable columns stay visible");
        var single = new ColumnLayout([new ColumnState("Type", 100, true), new ColumnState("Data", 0, false)]);
        check(
            ReferenceEquals(single.Toggle("Type", Specs), single) && !single.CanToggle("Type", Specs) &&
            single.CanToggle("Data", Specs), "The last visible column cannot be hidden");
        check(
            !ColumnLayout.Default(Specs).IsEquivalentTo(hidden) && !layout.IsEquivalentTo(moved) &&
            !layout.IsEquivalentTo(layout.Resize(Specs[1], 300)) &&
            ColumnLayout.Default(Specs).IsEquivalentTo(new ColumnLayout(layout.Columns.ToList())),
            "Equivalence compares order, visibility and widths by value");

        var json = JsonSerializer.Serialize(moved.Resize(Specs[1], 222), ColumnListJsonContext.Default.ColumnLayout);
        var restored = JsonSerializer.Deserialize(json, ColumnListJsonContext.Default.ColumnLayout)!.Normalize(Specs);
        check(restored.IsEquivalentTo(moved.Resize(Specs[1], 222)), "Layouts round-trip through source-generated JSON");
        var map = new Dictionary<string, ColumnLayout> { ["registry"] = moved };
        check(
            JsonSerializer.Deserialize(
                JsonSerializer.Serialize(map, ColumnListJsonContext.Default.DictionaryStringColumnLayout),
                ColumnListJsonContext.Default.DictionaryStringColumnLayout)!["registry"].IsEquivalentTo(moved),
            "Per-list layout maps round-trip");

        RunSizing(check);
    }

    private static void RunSizing(Action<bool, string> check)
    {
        var layout = ColumnLayout.Default(Specs);
        var widths = ColumnSizing.Compute(layout, Specs, 1000);
        check(widths.Length == 4, "Sizing returns one width per visible column");
        check(
            widths[1] == 170 && widths[3] == 40 && widths[0] == Math.Floor(790 * 2 / 5d) &&
            widths[2] == Math.Floor(790 * 3 / 5d), "Fill columns share the remaining width by weight");
        var narrow = ColumnSizing.Compute(layout, Specs, 100);
        check(narrow[0] == 80 && narrow[2] == 80, "Fill columns keep their minimum width when space runs out");
        check(
            ColumnSizing.Compute(layout, Specs, double.NaN).All(double.IsFinite) &&
            ColumnSizing.Compute(layout, Specs, -50)[0] == 80,
            "Invalid available widths size fill columns at their minimum");
        var hidden = layout.Toggle("Type", Specs).Toggle("Size", Specs);
        var shown = ColumnSizing.Compute(hidden, Specs, 1000);
        check(
            shown.Length == 4 && shown[2] == 110 && shown[0] + shown[1] <= 1000 - 110 - 40,
            "Hidden columns take no width and newly shown columns use their default");
        check(
            ColumnSizing.Compute(new ColumnLayout([new ColumnState("Ghost", 100, true)]), Specs, 500).Length == 0,
            "Sizing ignores columns without a specification");
    }
}
