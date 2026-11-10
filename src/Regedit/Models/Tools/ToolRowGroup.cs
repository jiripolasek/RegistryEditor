// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Tools;

[WinRT.GeneratedWinRTExposedType]
public sealed partial class ToolRowGroup : List<InvestigationRow>
{
    public string Title { get; }

    public ToolRowGroup(string title, IEnumerable<InvestigationRow> rows)
        : base(rows)
    {
        this.Title = title;
    }

    public override string ToString()
    {
        return this.Title;
    }
}
