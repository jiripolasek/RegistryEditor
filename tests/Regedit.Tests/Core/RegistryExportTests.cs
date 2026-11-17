// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class RegistryExportTests
{
    public static async Task RunAsync(Action<bool, string> check, bool useRegistry)
    {
        (RegistryValue Value, string Text)[] cases =
        [
            (new("DeploymentRing", RegistryValueType.String, Encoding.Unicode.GetBytes("Stable\0")),
                "\"DeploymentRing\"=\"Stable\""),
            (new(string.Empty, RegistryValueType.String, [0, 0]), "@=\"\""),
            (new("quoted\\\"name", RegistryValueType.String, Encoding.Unicode.GetBytes("C:\\český 🐈\\\"file\"\\\0")),
                "\"quoted\\\\\\\"name\"=\"C:\\\\český 🐈\\\\\\\"file\\\"\\\\\""),
            (new("tab", RegistryValueType.String, Encoding.Unicode.GetBytes("one\ttwo\0")), "\"tab\"=\"one\ttwo\""),
            (new("replacement", RegistryValueType.String, [0xfd, 0xff, 0, 0]), "\"replacement\"=\"\ufffd\""),
            (new("Enabled", RegistryValueType.DWord, [1, 0, 0, 0]), "\"Enabled\"=dword:00000001"),
            (new("StartupDelayMs", RegistryValueType.DWord, [0xc4, 9, 0, 0]), "\"StartupDelayMs\"=dword:000009c4"),
            (new("zero", RegistryValueType.DWord, [0, 0, 0, 0]), "\"zero\"=dword:00000000"),
            (new("maximum", RegistryValueType.DWord, [255, 255, 255, 255]), "\"maximum\"=dword:ffffffff"),
            (new("empty bytes", RegistryValueType.String, []), "\"empty bytes\"=hex(1):"),
            (new("unterminated", RegistryValueType.String, [65, 0]), "\"unterminated\"=hex(1):41,00"),
            (new("odd bytes", RegistryValueType.String, [65, 0, 66]), "\"odd bytes\"=hex(1):41,00,42"),
            (new("embedded null", RegistryValueType.String, [65, 0, 0, 0, 66, 0, 0, 0]),
                "\"embedded null\"=hex(1):41,00,00,00,42,00,00,00"),
            (new("extra null", RegistryValueType.String, [65, 0, 0, 0, 0, 0]), "\"extra null\"=hex(1):41,00,00,00,00,00"),
            (new("high surrogate", RegistryValueType.String, [0, 0xd8, 0, 0]), "\"high surrogate\"=hex(1):00,d8,00,00"),
            (new("low surrogate", RegistryValueType.String, [0, 0xdc, 0, 0]), "\"low surrogate\"=hex(1):00,dc,00,00"),
            (new("newline", RegistryValueType.String, [65, 0, 10, 0, 0, 0]), "\"newline\"=hex(1):41,00,0a,00,00,00"),
            (new("carriage return", RegistryValueType.String, [65, 0, 13, 0, 0, 0]),
                "\"carriage return\"=hex(1):41,00,0d,00,00,00"),
            (new("short dword", RegistryValueType.DWord, [1, 2]), "\"short dword\"=hex(4):01,02"),
            (new("long dword", RegistryValueType.DWord, [1, 2, 3, 4, 5]), "\"long dword\"=hex(4):01,02,03,04,05"),
            (new("big endian", RegistryValueType.DWordBigEndian, [0, 0, 0, 1]), "\"big endian\"=hex(5):00,00,00,01"),
            (new("qword", RegistryValueType.QWord, [1, 0, 0, 0, 0, 0, 0, 0]), "\"qword\"=hex(b):01,00,00,00,00,00,00,00"),
            (new("expand", RegistryValueType.ExpandString, [37, 0, 65, 0, 37, 0, 0, 0]),
                "\"expand\"=hex(2):25,00,41,00,25,00,00,00"),
            (new("multi", RegistryValueType.MultiString, [65, 0, 0, 0, 0, 0]), "\"multi\"=hex(7):41,00,00,00,00,00"),
            (new("bytes", RegistryValueType.Binary, [0, 255]), "\"bytes\"=hex:00,ff"),
            (new("empty binary", RegistryValueType.Binary, []), "\"empty binary\"=hex:"),
            (new("Binary", RegistryValueType.Binary, Enumerable.Range(0, 49).Select(i => (byte)i).ToArray()),
                "\"Binary\"=hex:00,01,02,03,04,05,06,07,08,09,0a,0b,0c,0d,0e,0f,10,11,12,13,14,15,\\\r\n" +
                "  16,17,18,19,1a,1b,1c,1d,1e,1f,20,21,22,23,24,25,26,27,28,29,2a,2b,2c,2d,2e,\\\r\n  2f,30"),
            (new(new string('n', 80), RegistryValueType.Binary, [0, 1]),
                $"\"{new string('n', 80)}\"=hex:00,\\\r\n  01"),
            (new("none", RegistryValueType.None, [0, 255]), "\"none\"=hex(0):00,ff"),
            (new("link", RegistryValueType.Link, [65, 0, 0, 0]), "\"link\"=hex(6):41,00,00,00"),
            (new("resources", RegistryValueType.ResourceList, [0, 255]), "\"resources\"=hex(8):00,ff"),
            (new("descriptor", RegistryValueType.FullResourceDescriptor, [0, 255]), "\"descriptor\"=hex(9):00,ff"),
            (new("requirements", RegistryValueType.ResourceRequirementsList, [0, 255]), "\"requirements\"=hex(a):00,ff"),
            (new("unknown", (RegistryValueType)99, [0, 255]), "\"unknown\"=hex(63):00,ff")
        ];
        foreach (var (value, expected) in cases)
        {
            using var writer = new StringWriter { NewLine = "\r\n" };
            await RegistryExport.WriteValueAsync(writer, value, default);
            check(writer.ToString() == expected + "\r\n", "Readable lossless export: " + value.DisplayName);
        }

        if (!useRegistry)
        {
            return;
        }

        var service = new RegistryService();
        var root = RegistryLocation.Parse(@"HKCU\Software\Regedit.ExportTests." + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".reg");
        var systemFile = file + ".system.reg";
        service.CreateKey(root.Parent!, root.Name);
        try
        {
            foreach (var (value, _) in cases)
            {
                service.Write(root, value, null);
            }

            var wrappedData = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray();
            service.Write(root, new RegistryValue("typed wrap", (RegistryValueType)99, wrappedData), null);
            service.Write(
                root, new RegistryValue("Capabilities", RegistryValueType.MultiString,
                    RegistryValue.ParseData(RegistryValueType.MultiString, "EventCollection\r\nMetricAggregation")), null);
            foreach (var name in new[] { "wrap", "escaped\\\"name", "Unicode 🐈" })
            {
                for (var length = 0; length <= 80; length++)
                {
                    service.Write(root, new RegistryValue(name + length, RegistryValueType.Binary, wrappedData[..length]), null);
                }
            }

            var before = service.Read(root).Values;
            await RegistryExport.ExportAsync(service, [root], file);
            var exported = await File.ReadAllTextAsync(file);
            check(
                exported.Contains("\"DeploymentRing\"=\"Stable\"\r\n") && exported.Contains("\"Enabled\"=dword:00000001\r\n"),
                "Branch export uses readable string and DWORD syntax");
            check(
                exported.IndexOf("\"Binary\"=", StringComparison.Ordinal) < exported.IndexOf("\"bytes\"=", StringComparison.Ordinal),
                "Branch export retains its alphabetical value order");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
            start.ArgumentList.Add("/e");
            start.ArgumentList.Add(systemFile);
            start.ArgumentList.Add(root.Path);
            using var process = Process.Start(start) ?? throw new IOException("Could not start system Regedit.");
            await process.WaitForExitAsync();
            check(process.ExitCode == 0 && File.Exists(systemFile), "System Regedit exports the temporary fixture");
            var systemExport = await File.ReadAllTextAsync(systemFile);
            foreach (var value in before)
            {
                using var writer = new StringWriter { NewLine = "\r\n" };
                await RegistryExport.WriteValueAsync(writer, value, default);
                var text = writer.ToString();
                if (value.Type == RegistryValueType.String && text.StartsWith(RegistryExport.ValueName(value.Name) + "=hex(1):"))
                {
                    continue;
                }

                check(systemExport.Contains(text), "Value formatting matches system Regedit: " + value.DisplayName);
            }

            service.DeleteKey(root);
            await RegistryService.RunRegAsync("import", file, "/reg:64");
            foreach (var value in before)
            {
                check(value.SameData(service.ReadValue(root, value.Name)), "Native export/import preserves: " + value.DisplayName);
            }
        }
        finally
        {
            using var parent = service.Open(root.Parent!, true);
            parent.DeleteSubKeyTree(root.Name, false);
            File.Delete(file);
            File.Delete(systemFile);
        }
    }
}
