// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;
using JPSoftworks.Regedit.Core.Import;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;
using Registry = Microsoft.Win32.Registry;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class RegistryFileTests
{
    private const string Header = "Windows Registry Editor Version 5.00\r\n\r\n";

    internal static async Task RunAsync(Action<bool, string> check, bool native)
    {
        MetadataAndKeyChanges(check);
        var source = Header
                     + "[HKEY_CURRENT_USER\\Software\\Preview]\r\n"
                     + "@=\"A \\\"quote\\\" and \\\\ path\"\r\n\"Number\"=dword:ffffffff\r\n"
                     + "\"Bytes\"=hex:01,ab,\\\r\n  ff\r\n\"Expand\"=hex(2):25,00,00,00\r\n\"Remove\"=-\r\n"
                     + "[-HKEY_CURRENT_USER\\Software\\Preview]\r\n[HKEY_CURRENT_USER\\Software\\Preview]\r\n\"Number\"=dword:1\r\n";
        var document = RegistryFileDocument.Parse(source);
        check(
            document.CanImport && document.Sections.Count == 3 && document.Sections[1].IsDeletion,
            "Registry file retains ordered repeated sections and subtree deletion");
        var values = document.Sections[0].Values;
        check(
            values.Count == 5 && values[0].Name == "" && values[0].Value!.DisplayData == "A \"quote\" and \\ path",
            "Registry file decodes escaped strings and the default value");
        check(
            values[1].Value!.Data.SequenceEqual(new byte[] { 255, 255, 255, 255 }) && values[1].Line == 5,
            "Registry DWORD retains unsigned bytes and source line");
        check(
            values[2].Value!.Data.SequenceEqual(new byte[] { 1, 171, 255 }) && values[3].Value!.Type == RegistryValueType.ExpandString,
            "Registry hex continuations and typed byte payloads are preserved");
        check(values[4].Value is null && values[4].Operation == "Delete value", "Registry value deletion is explicit");

        foreach (var invalid in new[]
                 {
                     "", "not a header\r\n[HKEY_CURRENT_USER]", Header + "\"Name\"=\"Outside a section\"", Header + "[HKCU\\Software]",
                     Header + "[-HKEY_CURRENT_USER]", Header + "[HKEY_CURRENT_USER] trailing",
                     Header + "[HKEY_CURRENT_USER]\r\n\"x\"=dword:123456789", Header + "[HKEY_CURRENT_USER]\r\n\"x\"=hex:01,zz",
                     Header + "[HKEY_CURRENT_USER]\r\n\"x\"=hex:01,\\", Header + "[HKEY_CURRENT_USER]\r\n\"x\"=\"unterminated",
                     Header + "[HKEY_CURRENT_USER]\r\n\"x\"=qword:1", Header + "[HKEY_CURRENT_USER]\r\n\"x\"=\"bad\\escape\"",
                     Header + "; hidden\0content"
                 })
        {
            check(!RegistryFileDocument.Parse(invalid).CanImport, "Registry file refuses invalid or unsupported source");
        }

        foreach (var hive in new[] { "HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG" })
        {
            var mixed = RegistryFileDocument.Parse(Header + "[" + hive.ToLowerInvariant() + "\\Software\\MixedCaseKey]\r\n@=\"value\"");
            check(
                mixed.CanImport && mixed.Sections[0].Path == hive + "\\Software\\MixedCaseKey",
                "Registry file accepts hive casing and canonicalizes only the hive name");
        }

        check(
            !RegistryFileDocument.Parse(Header + "[-hKeY_cUrReNt_UsEr]").CanImport,
            "Mixed-case hive names cannot bypass the root deletion restriction");

        var unknown = RegistryFileDocument.Parse(Header + "[HKEY_CURRENT_USER]\r\n\"x\"=hex(1234):01,02");
        check(
            unknown.CanImport && (uint)unknown.Sections[0].Values[0].Value!.Type == 0x1234,
            "Registry file preserves unknown native value types");
        var bracket = RegistryFileDocument.Parse(Header + "[HKEY_CURRENT_USER\\Software\\Name]]\r\n@=\"data\"");
        check(
            bracket.CanImport && bracket.Sections[0].Path.EndsWith("Name]"),
            "Registry file removes only the outer brackets of a key section");
        var legacy = RegistryFileDocument.Parse("REGEDIT4\r\n[HKEY_CURRENT_USER]\r\n\"x\"=hex(2):41,00");
        check(
            legacy.CanImport && legacy.IsLegacy && legacy.Sections[0].Values[0].Value!.DisplayData == "A",
            "REGEDIT4 hexadecimal strings decode using Windows ANSI");

        var folder = Directory.CreateTempSubdirectory("Regedit.RegistryFile.Tests.");
        try
        {
            foreach (var encoding in new Encoding[]
                     {
                         new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF8Encoding(true, true),
                         Encoding.ASCII
                     })
            {
                var file = Path.Combine(folder.FullName, "roundtrip.reg");
                var text = source.ReplaceLineEndings("\n");
                var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                await File.WriteAllBytesAsync(file, bytes);
                var buffer = await RegistryFileBuffer.OpenAsync(file);
                check(buffer.Text == text && !buffer.IsDirty, "Registry file opens without changing source or line endings");
                await buffer.SaveAsync(file);
                check((await File.ReadAllBytesAsync(file)).SequenceEqual(bytes), "Unchanged save preserves every original file byte");
                buffer.Text += "; unsaved edit\n";
                var snapshot = buffer.ImportBytes();
                check(
                    buffer.IsDirty
                    && Encoding.Unicode.GetString(snapshot.AsSpan(2)).EndsWith("; unsaved edit\n")
                    && (await File.ReadAllBytesAsync(file)).SequenceEqual(bytes),
                    "Import snapshot includes unsaved edits without saving the file");
                await File.WriteAllTextAsync(file, "external change");
                try
                {
                    await buffer.SaveAsync(file);
                    check(false, "External file change must prevent overwriting");
                }
                catch (IOException)
                {
                    check(
                        await File.ReadAllTextAsync(file) == "external change" && buffer.IsDirty,
                        "Registry file detects an external change and retains unsaved edits");
                }

                var copy = Path.Combine(folder.FullName, "copy.reg");
                await buffer.SaveAsync(copy);
                check(
                    !buffer.IsDirty && buffer.FilePath == copy && (await RegistryFileBuffer.OpenAsync(copy)).Text == buffer.Text,
                    "Save as preserves source and establishes the new file baseline");
                File.Delete(copy);
                buffer.Text += "; after external deletion\n";
                try
                {
                    await buffer.SaveAsync(copy);
                    check(false, "An externally deleted file requires an explicit Save as decision");
                }
                catch (IOException ex)
                {
                    check(
                        ex.Message == "The file changed outside Registry Editor. Reload it or use Save as to keep your changes."
                        && ex.InnerException is FileNotFoundException
                        && !File.Exists(copy)
                        && buffer.IsDirty,
                        "External deletion reports a save conflict and retains unsaved data without recreating the file");
                }

                var recovered = Path.Combine(folder.FullName, "recovered.reg");
                await buffer.SaveAsync(recovered);
                check(
                    !buffer.IsDirty && await File.ReadAllTextAsync(recovered) == buffer.Text,
                    "Save as can recover edits after external deletion");
            }

            var invalidEncoding = Path.Combine(folder.FullName, "invalid.reg");
            await File.WriteAllBytesAsync(invalidEncoding, [0xff, 0xfe, 0x41]);
            try
            {
                await RegistryFileBuffer.OpenAsync(invalidEncoding);
                check(false, "Invalid UTF-16 must be refused");
            }
            catch (DecoderFallbackException)
            {
                check(true, "Malformed Unicode cannot be silently replaced on save");
            }

            if (native)
            {
                await NativeImportAsync(folder.FullName, check);
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }

    private static void MetadataAndKeyChanges(Action<bool, string> check)
    {
        const string root = "HKEY_CURRENT_USER\\Software\\Preview";
        var text = Header
                   + "; title = First\n;TITLE=Example tweak\n;Description=Unicode café = data\n;Author=Example author\n"
                   + ";Requires=explorerrestart\n;Requires=Logoff\n;Requires=Restart\n;Requires=Restart\n;Requires=Unknown\n;Unknown=ignored\n"
                   + $"[{root}]\n;Title=Ignored after a section\n";
        var document = RegistryFileDocument.Parse(text);
        check(
            document.CanImport
            && document.Metadata.Title == "Example tweak"
            && document.Metadata.Description == "Unicode café = data"
            && document.Metadata.Author == "Example author",
            "Registry metadata accepts header directives, whitespace, case and the last scalar value");
        check(
            document.Metadata.FollowUp
            == (RegistryFileFollowUp.ExplorerRestart | RegistryFileFollowUp.Logoff | RegistryFileFollowUp.Restart)
            && document.Metadata.FollowUpText == "Restart Windows Explorer; Sign out; Restart Windows",
            "Registry follow-up directives accumulate, deduplicate and ignore unknown actions");
        check(
            !RegistryFileDocument.Parse(Header + $";Unknown=x\n;Requires=Unknown\n[{root}]").Metadata.HasContent,
            "Unknown metadata does not create an empty metadata panel");
        check(
            !RegistryFileDocument.Parse(Header + $"[{root}]\n;Title=late").Metadata.HasContent,
            "Registry metadata stops at the first section");
        check(
            RegistryFileDocument.Parse("REGEDIT4\n;Title=Legacy\n[" + root + "]").Metadata.Title == "Legacy",
            "Registry metadata works with REGEDIT4");
        check(
            document.Metadata.HasContent && RegistryFileDocument.Parse(Header + $"[{root}]").Metadata == RegistryFileMetadata.Empty,
            "Metadata-free registry files retain an empty metadata value");

        RegistryFileDocument Parse(params string[] sections)
        {
            return RegistryFileDocument.Parse(Header + string.Join("\n", sections));
        }

        check(Parse($"[-{root}]").KeyChanges[root] == RegistryFileKeyChange.Delete, "A final key deletion is marked as deletion");
        check(
            Parse($"[{root}]", $"[-{root}]").KeyChanges[root] == RegistryFileKeyChange.Delete,
            "Creating then deleting a key finishes deleted");
        check(
            Parse($"[-{root}]", $"[{root}]").KeyChanges[root] == RegistryFileKeyChange.Recreate,
            "Deleting then opening a key is marked as recreation");
        check(
            Parse($"[-{root}]", $"[{root}]", $"[-{root}]").KeyChanges[root] == RegistryFileKeyChange.Delete,
            "A later deletion wins over an earlier recreation");
        var inherited = Parse($"[{root}\\Removed]", $"[-{root}]", $"[{root}\\Kept]");
        check(
            inherited.KeyChanges[root] == RegistryFileKeyChange.Recreate
            && inherited.KeyChanges[root + "\\Kept"] == RegistryFileKeyChange.Recreate
            && inherited.KeyChanges[root + "\\Removed"] == RegistryFileKeyChange.Delete,
            "Ancestor deletion removes old children and creating one child recreates only its ancestors");
        var descendant = Parse($"[{root}\\Child]", $"[-{root}\\Child]", $"[{root}]");
        check(
            descendant.KeyChanges[root] == RegistryFileKeyChange.None
            && descendant.KeyChanges[root + "\\Child"] == RegistryFileKeyChange.Delete,
            "Reopening a parent does not recreate a deleted descendant");
        var sibling = Parse($"[-{root}]", $"[{root}2]", $"[{root.ToUpperInvariant()}\\New]");
        check(
            sibling.KeyChanges[root] == RegistryFileKeyChange.Recreate && sibling.KeyChanges[root + "2"] == RegistryFileKeyChange.None,
            "Key summaries compare case-insensitively at whole path-component boundaries");
        check(
            Parse($"[{root}]", $"[{root}]").KeyChanges[root] == RegistryFileKeyChange.None,
            "Repeated writes without a deletion are ordinary key instructions");
    }

    private static async Task NativeImportAsync(string folder, Action<bool, string> check)
    {
        var subKey = "Software\\Regedit.RegistryFile.Tests." + Guid.NewGuid().ToString("N");
        var path = "HKEY_CURRENT_USER\\" + subKey;
        var sourceFile = Path.Combine(folder, "native.reg");
        try
        {
            var source = Header
                         + $"[{path}]\r\n\"Old\"=\"discard\"\r\n[-{path}]\r\n[{path}]\r\n"
                         + "@=\"Unicode žluťoučký 🐈\"\r\n\"Number\"=dword:ffffffff\r\n\"Bytes\"=hex:01,ab,\\\r\n  ff\r\n";
            await File.WriteAllTextAsync(sourceFile, source, new UTF8Encoding(true));
            var buffer = await RegistryFileBuffer.OpenAsync(sourceFile);
            buffer.Text += "\"Unsaved\"=\"present\"\r\n" + $"[{path}\\Name]]\r\n@=\"bracket key\"\r\n";
            var snapshot = Path.Combine(folder, "snapshot.reg");
            await File.WriteAllBytesAsync(snapshot, buffer.ImportBytes());
            await RegistryService.RunRegAsync("import", snapshot, "/reg:64");
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = root.OpenSubKey(subKey)!)
            {
                check(
                    (string?)key.GetValue("") == "Unicode žluťoučký 🐈"
                    && key.GetValue("Old") is null
                    && (string?)key.GetValue("Unsaved") == "present"
                    && (int)key.GetValue("Number")! == -1
                    && ((byte[])key.GetValue("Bytes")!).SequenceEqual(new byte[] { 1, 171, 255 }),
                    "Windows imports the exact Unicode buffer and preserves ordered deletion, types, continuations and unsaved changes");
                using var bracketKey = root.OpenSubKey(subKey + "\\Name]");
                check(
                    (string?)bracketKey?.GetValue("") == "bracket key",
                    "Windows key names containing a closing bracket agree with the file preview");
            }

            await File.WriteAllTextAsync(sourceFile, $"REGEDIT4\r\n[{path}]\r\n\"Legacy\"=hex(2):41,00\r\n", Encoding.ASCII);
            var legacy = await RegistryFileBuffer.OpenAsync(sourceFile);
            await File.WriteAllBytesAsync(snapshot, legacy.ImportBytes());
            await RegistryService.RunRegAsync("import", snapshot, "/reg:32");
            using var root32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            using var key32 = root32.OpenSubKey(subKey)!;
            check(
                (string?)key32.GetValue("Legacy") == "A",
                "Windows REGEDIT4 import agrees with the preview's ANSI hexadecimal string decoding");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
        }
    }
}
