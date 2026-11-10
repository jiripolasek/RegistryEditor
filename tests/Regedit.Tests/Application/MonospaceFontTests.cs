// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Services;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class MonospaceFontTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(
            MonospaceFonts.ChooseFamily(["Consolas", "Cascadia Code", "JetBrains Mono"], "jetbrains mono") ==
            "JetBrains Mono", "An installed preferred font takes priority and uses its installed family name");
        check(
            MonospaceFonts.ChooseFamily(["Consolas", "Cascadia Code", "Courier New"], "Missing font") ==
            "Cascadia Code", "Cascadia Code is the first fallback when a preferred font is unavailable");
        check(
            MonospaceFonts.ChooseFamily(["Courier New", "Consolas"], null) == "Consolas",
            "Consolas is the second fallback");
        check(
            MonospaceFonts.ChooseFamily(["Courier New"], null) == "Courier New",
            "Courier New is the final installed fallback");
        check(
            MonospaceFonts.ChooseFamily([], null) == "Courier New",
            "An empty font collection keeps the final named fallback");

        var directory = Directory.CreateTempSubdirectory("RegeditMonospace.");
        try
        {
            var stable = Path.Combine(
                directory.FullName, "Packages/Microsoft.WindowsTerminal_8wekyb3d8bbwe/LocalState/settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(stable)!);
            check(
                MonospaceFonts.FindTerminalFont(directory.FullName) is null,
                "Missing Terminal settings do not invent a font preference");
            var cases = new (string Json, string? Font)[]
            {
                ("""{"defaultProfile":"Work","profiles":{"defaults":{"font":{"face":"Consolas"}},"list":[{"name":"Work","font":{"face":"Fira Code"}}]}}""",
                    "Fira Code"),
                ("""{"defaultProfile":"AAAAAAAA-1111-2222-3333-444444444444","profiles":{"list":[{"guid":"{aaaaaaaa-1111-2222-3333-444444444444}","font":{"face":"Iosevka"}}]}}""",
                    "Iosevka"),
                ("""{"defaultProfile":"Work","profiles":{"defaults":{"font":{"face":"Consolas"}},"list":[{"name":"Work","font":{"size":16}}]}}""",
                    "Consolas"),
                ("""{"profiles":{"defaults":{"fontFace":"  DejaVu Sans Mono  "}}}""", "DejaVu Sans Mono"),
                ("""{"profiles":{"defaults":{"font":{"face":false},"fontFace":"Consolas"}}}""", "Consolas"),
                ("""{"profiles":{"defaults":{"font":{"face":" "}}}}""", null),
                ("""{"profiles":{"list":[{"name":"Unused","font":{"face":"Unrelated font"}}]}}""", null),
                ("""{"defaultProfile":{},"profiles":{"defaults":42,"list":[null]}}""", null), ("[]", null),
                ("{unfinished", null), ("""
                                        {
                                            // Terminal settings permit comments and trailing commas.
                                            "profiles": { "defaults": { "font": { "face": "Fira Code", }, }, },
                                        }
                                        """, "Fira Code")
            };
            foreach (var (json, font) in cases)
            {
                File.WriteAllText(stable, json);
                check(
                    MonospaceFonts.FindTerminalFont(directory.FullName) == font,
                    $"Terminal font lookup handles fixture {Array.IndexOf(cases, (json, font)) + 1}");
            }

            using (File.Open(stable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                check(
                    MonospaceFonts.FindTerminalFont(directory.FullName) is null,
                    "Locked Terminal settings leave automatic fallback available");
            }

            File.WriteAllText(stable, "{unfinished");
            var preview = Path.Combine(
                directory.FullName, "Packages/Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe/LocalState/settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
            File.WriteAllText(preview, """{"profiles":{"defaults":{"font":{"face":"Iosevka"}}}}""");
            check(
                MonospaceFonts.FindTerminalFont(directory.FullName) == "Iosevka",
                "Preview preferences are used when Stable settings cannot be read");
            File.WriteAllText(preview, "{}");
            var unpackaged = Path.Combine(directory.FullName, "Microsoft/Windows Terminal/settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(unpackaged)!);
            File.WriteAllText(unpackaged, """{"profiles":{"defaults":{"font":{"face":"Fira Code"}}}}""");
            check(
                MonospaceFonts.FindTerminalFont(directory.FullName) == "Fira Code",
                "Unpackaged Terminal preferences are also supported");
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
