// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Controls.Shared;

/// <summary>
/// Maps <see cref="Symbol"/> values to Segoe Fluent Icons code points. Symbol values are legacy Segoe MDL2 code
/// points whose Fluent glyphs are offset and overflow a 16 px icon box, so toolbar icons use the current glyphs.
/// </summary>
internal static class SymbolGlyphs
{
    public static string Get(Symbol symbol)
    {
        var codePoint = symbol switch
        {
            Symbol.Add => 0xE710,
            Symbol.AllApps => 0xE71D,
            Symbol.Camera => 0xE722,
            Symbol.Cancel => 0xE711,
            Symbol.Clear => 0xE894,
            Symbol.ClearSelection => 0xE8E6,
            Symbol.Clock => 0xE81C,
            Symbol.Copy => 0xE8C8,
            Symbol.Delete => 0xE74D,
            Symbol.DockBottom => 0xE90E,
            Symbol.DockRight => 0xE90D,
            Symbol.Edit => 0xE70F,
            Symbol.Favorite => 0xE734,
            Symbol.Filter => 0xE71C,
            Symbol.Find => 0xE721,
            Symbol.Folder => 0xE8B7,
            Symbol.Forward => 0xE72A,
            Symbol.More => 0xE712,
            Symbol.MoveToFolder => 0xE8DE,
            Symbol.NewFolder => 0xE8F4,
            Symbol.NewWindow => 0xE78B,
            Symbol.OpenFile => 0xE8E5,
            Symbol.Paste => 0xE77F,
            Symbol.Pause => 0xE769,
            Symbol.Permissions => 0xE8D7,
            Symbol.Play => 0xE768,
            Symbol.Print => 0xE749,
            Symbol.Refresh => 0xE72C,
            Symbol.Rename => 0xE8AC,
            Symbol.Save => 0xE74E,
            Symbol.SelectAll => 0xE8B3,
            Symbol.Stop => 0xE71A,
            Symbol.Undo => 0xE7A7,
            Symbol.Up => 0xE74A,
            Symbol.View => 0xE890,
            _ => (int)symbol
        };
        return ((char)codePoint).ToString();
    }
}
