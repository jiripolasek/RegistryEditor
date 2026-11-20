// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using JPSoftworks.Regedit.Services;
using Microsoft.Graphics.Canvas.Text;

namespace JPSoftworks.Regedit.Infrastructure;

internal static class SystemFonts
{
    // Keep Win2D type initialization inside the background loader's exception boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string[] GetFontFamilies()
    {
        return CanvasTextFormat.GetSystemFontFamilies();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string GetDefaultMonospaceFontFamily()
    {
        var preferred = MonospaceFonts.FindTerminalFont(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (preferred is not null)
        {
            using var fonts = CanvasFontSet.GetSystemFontSet();
            using var matches = fonts.GetMatchingFonts(
            [
                new CanvasFontProperty { Identifier = CanvasFontPropertyIdentifier.FamilyName, Value = preferred, Locale = "" }
            ]);
            var monospaced = false;
            foreach (var face in matches.Fonts)
            {
                using (face)
                {
                    monospaced |= face.IsMonospaced;
                }
            }

            if (!monospaced)
            {
                preferred = null;
            }
        }

        return MonospaceFonts.ChooseFamily(GetFontFamilies(), preferred);
    }
}
