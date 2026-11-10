// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Tools;

internal interface IToolSurface
{
    ToolKind Tool { get; }

    Button MenuButton { get; }

    IReadOnlyList<ToolCommand> PanelCommands { get; }

    void RequestFocus(bool preferSelection = false);

    void CopyContextFrom(IToolSurface source);
}
