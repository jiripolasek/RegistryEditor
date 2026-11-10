// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed class ToolTab(ToolKind tool) : WindowTab(tool.Choice().Title, tool.Choice().Glyph)
{
    public override string IconFontFamily => this.Tool == ToolKind.Notes
        ? ToolPresentation.FluentSystemIconsFontFamily
        : base.IconFontFamily;

    internal ToolKind Tool { get; } = tool;
}
