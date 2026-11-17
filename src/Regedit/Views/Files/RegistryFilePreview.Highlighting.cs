// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.Regedit.Models.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace JPSoftworks.Regedit.Views.Files;

public sealed partial class RegistryFilePreview
{
    private int _lineMarginDigits;

    private void InitializeHighlighting()
    {
        var lexer = CreateLexer("registry");
        if (lexer == 0)
        {
            throw new InvalidOperationException("WinUIEdit's registry lexer is unavailable.");
        }

        // Scintilla owns the lexer after SetILexer and releases it with the document.
        this.SourceEditor.Editor.SetILexer((ulong)lexer);
    }

    internal void ApplyEditorFont(RegistryFont font)
    {
        this._editorFont = font;
        this.ApplyEditorSurface();
    }

    private void ApplyEditorSurface()
    {
        if (this.ViewModel is { IsClosed: true } || this.EditorSurface.Background is not SolidColorBrush brush)
        {
            return;
        }

        var editor = this.SourceEditor.Editor;
        if (this._editorFont is { } font)
        {
            for (var style = 0; style <= 39; style++)
            {
                editor.StyleSetFont(style, font.Family);
                editor.StyleSetSizeFractional(style, (int)Math.Round(font.Size * 100));
            }

            this.UpdateLineMargin(true);
        }

        var background = brush.Color;
        var nativeBackground = background.R | background.G << 8 | background.B << 16;
        // Palette indices follow Lexilla's SCE_REG_* styles in SciLexer.h.
        uint[] colors = this.ActualTheme == ElementTheme.Dark
            ?
            [
                0xD4D4D4, 0x6A9955, 0x9CDCFE, 0xCE9178, 0xB5CEA8, 0x569CD6, 0x4EC9B0,
                0xF48771, 0xD7BA7D, 0xB8D7A3, 0xB8D7A3, 0xDCDCAA, 0xD4D4D4
            ]
            :
            [
                0x1F1F1F, 0x008000, 0x001080, 0xA31515, 0x098658, 0x0000FF, 0x0000FF,
                0xA1260D, 0xAA5D00, 0x267F99, 0x267F99, 0x795E26, 0x000000
            ];
        var highContrast = new AccessibilitySettings().HighContrast;
        var foreground = new UISettings().UIElementColor(UIElementType.WindowText);
        for (var style = 0; style < colors.Length; style++)
        {
            var rgb = colors[style];
            var nativeForeground = highContrast
                ? foreground.R | foreground.G << 8 | foreground.B << 16
                : (int)((rgb & 0xFF) << 16 | rgb & 0xFF00 | rgb >> 16);
            editor.StyleSetFore(style, nativeForeground);
            editor.StyleSetBack(style, nativeBackground);
            editor.StyleSetBold(style, style is 6 or 7);
            editor.StyleSetItalic(style, style == 1);
            editor.StyleSetUnderline(style, style == 7);
        }

        editor.StyleSetBack(32, nativeBackground);
        editor.StyleSetBack(33, nativeBackground);
        this.ApplyDiagnosticMarkerStyle();
    }

    private void UpdateLineMargin(bool force = false)
    {
        var editor = this.SourceEditor.Editor;
        var digits = Math.Max(3, editor.LineCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        if (force || digits != this._lineMarginDigits)
        {
            this._lineMarginDigits = digits;
            editor.SetMarginWidthN(0, (int)editor.TextWidth(33, new string('9', digits)) + 16);
        }
    }

    [LibraryImport("WinUIEditor.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CreateLexer(string name);
}
