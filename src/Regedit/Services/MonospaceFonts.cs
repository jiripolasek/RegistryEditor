// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security;
using System.Text.Json;

namespace JPSoftworks.Regedit.Services;

internal static class MonospaceFonts
{
    internal const string FallbackFamily = "Cascadia Code, Consolas, Courier New";

    internal static string ChooseFamily(IReadOnlyList<string> installed, string? preferred)
    {
        foreach (var candidate in new[] { preferred }.Concat(FallbackFamily.Split(", ")))
        {
            if (installed.FirstOrDefault(family => string.Equals(
                    family, candidate, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        return "Courier New";
    }

    internal static string? FindTerminalFont(string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        foreach (var relative in new[]
                 {
                     "Packages/Microsoft.WindowsTerminal_8wekyb3d8bbwe/LocalState/settings.json",
                     "Packages/Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe/LocalState/settings.json",
                     "Microsoft/Windows Terminal/settings.json"
                 })
        {
            try
            {
                using var file = new FileStream(
                    Path.Combine(localAppData, relative), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(
                    file,
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (ReadTerminalFont(document.RootElement) is { } family)
                {
                    return family;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                           or JsonException)
            {
                // A missing, unreadable, or unfinished Terminal settings file is optional.
            }
        }

        return null;
    }

    private static string? ReadTerminalFont(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("defaultProfile", out var selected) && selected.ValueKind == JsonValueKind.String &&
            profiles.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in list.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var matches = profile.TryGetProperty("guid", out var guid) && guid.ValueKind == JsonValueKind.String &&
                              Guid.TryParse(selected.GetString(), out var expected) &&
                              Guid.TryParse(guid.GetString(), out var actual) && expected == actual;
                matches |= profile.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                           string.Equals(name.GetString(), selected.GetString(), StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    return ReadFace(profile) ?? (profiles.TryGetProperty("defaults", out var inherited)
                        ? ReadFace(inherited)
                        : null);
                }
            }
        }

        return profiles.TryGetProperty("defaults", out var defaults) ? ReadFace(defaults) : null;
    }

    private static string? ReadFace(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (profile.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.Object &&
            font.TryGetProperty("face", out var face) && face.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(face.GetString()))
        {
            return face.GetString()!.Trim();
        }

        return profile.TryGetProperty("fontFace", out var legacy) && legacy.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(legacy.GetString())
            ? legacy.GetString()!.Trim()
            : null;
    }
}
