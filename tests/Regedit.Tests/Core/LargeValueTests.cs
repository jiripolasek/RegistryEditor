// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class LargeValueTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool useRegistry)
    {
        var contents = new string('x', 524_288 - 12) + "suffixneedle";
        var value = new RegistryValue("Large", RegistryValueType.String, Encoding.Unicode.GetBytes(contents + "\0"));
        var warmup = new RegistryEntry(null, new RegistryValue("Warmup", RegistryValueType.String, [65, 0]));
        _ = warmup.DataPreview;
        _ = warmup.AccessibleName;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var row = new RegistryEntry(null, value);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(allocated < 512, "Creating a large-value row does not decode offscreen data");
        allocated = GC.GetAllocatedBytesForCurrentThread();
        var preview = row.DataPreview;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(
            allocated < 16_384 && preview.Length == 257 && preview.EndsWith("…"),
            "A 524,288-character string row allocates only its bounded preview when displayed");
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            _ = row.NamePreview;
            _ = row.DataPreview;
            _ = row.AccessibleName;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(
            ReferenceEquals(preview, row.DataPreview) && allocated < 16_384 && row.AccessibleName.Length < 600,
            "Repeated row text tooltip and automation reads never decode or allocate the full string");
        var otherRow = new RegistryEntry(null, value);
        var selected = new HashSet<RegistryEntry> { row };
        var originalHash = row.GetHashCode();
        var fullData = row.Data;
        check(
            fullData == contents && ReferenceEquals(row.Value, value),
            "Row previews retain full decoded contents and the original value identity");
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            _ = row.Data;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(
            ReferenceEquals(fullData, row.Data) && allocated < 1024,
            "Repeated filter sort and copy data requests reuse the full decoded snapshot");
        check(
            row != otherRow && !row.Equals(otherRow) && !Equals(row, otherRow) && row.GetHashCode() == originalHash &&
            selected.Contains(row) && !selected.Contains(otherRow),
            "Rows with identical data have separate identities and cached presentation preserves hash lookups");
        var changed = row with
        {
            Value = new RegistryValue(
                "Changed", RegistryValueType.String, Encoding.Unicode.GetBytes("new contents\0"))
        };
        check(
            changed.NamePreview == "Changed" && changed.DataPreview == "new contents" &&
            changed.Data == "new contents" && changed.AccessibleName.Contains("Changed") && row.Data == contents &&
            changed != row, "Record copies rebuild caches for their changed value without changing the original row");
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            _ = new RegistryEntry(null, value).Glyph;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(allocated < 256_000, "Offscreen and icon-only row construction does not allocate value previews");
        var inspector = new ValueInspectorViewModel(value, row.Data);
        check(
            inspector.Data == contents && inspector.Data.EndsWith("suffixneedle"),
            "Explicit inspection retains the entire large string including its suffix");

        var cases = new RegistryValue[]
        {
            new("Empty", RegistryValueType.String, []), new("Nuls", RegistryValueType.String, new byte[6000]),
            new("Unterminated", RegistryValueType.ExpandString, Encoding.Unicode.GetBytes("unexpanded %PATH%")),
            new("Link", RegistryValueType.Link, Encoding.Unicode.GetBytes("link\0")),
            new("Odd", RegistryValueType.String, [65, 0, 0, 0, 255]),
            new("Surrogate", RegistryValueType.String, [0, 216, 65, 0]),
            new("Multi", RegistryValueType.MultiString, Encoding.Unicode.GetBytes("one\0\0two\0\0")),
            new(
                "LongMulti", RegistryValueType.MultiString,
                Encoding.Unicode.GetBytes(string.Join('\0', Enumerable.Repeat("a", 400)) + "\0\0")),
            new(
                "PairBoundary", RegistryValueType.String,
                Encoding.Unicode.GetBytes(new string('a', 255) + "😀tail")),
            new("Unknown", (RegistryValueType)99, Enumerable.Range(0, 400).Select(index => (byte)index).ToArray()),
            new("MalformedNumber", RegistryValueType.DWord, [1, 2, 3]),
            new("Number", RegistryValueType.DWord, [1, 0, 0, 0])
        };
        foreach (var sample in cases)
        {
            var full = sample.DisplayData;
            var length = full.Length > 256 && char.IsHighSurrogate(full[255]) ? 255 : 256;
            var expected
                = sample.Type is not (RegistryValueType.String or RegistryValueType.ExpandString
                    or RegistryValueType.Link or RegistryValueType.MultiString) || full.Length <= 256
                    ? full
                    : full[..length] + "…";
            check(sample.DisplayPreview == expected, "Preview matches existing display semantics: " + sample.Name);
        }

        const string multiline = "one\r\ntwo\rthree\nfour\ffive\u0085six\u2028seven\u2029eight";
        foreach (var type in new RegistryValueType[]
                 {
                     RegistryValueType.String, RegistryValueType.ExpandString, RegistryValueType.Link
                 })
        {
            var text = new RegistryValue("Multiline", type, Encoding.Unicode.GetBytes(multiline + "\0"));
            var textRow = new RegistryEntry(null, text);
            check(
                textRow.DataPreview == "one two three four five six seven eight" && textRow.Data == multiline,
                $"Type {type} separates preview lines with spaces without changing the full value");
        }

        var linkPath = "\\Registry\\Machine\\first line\r\nsecond line";
        var linkRow = new RegistryEntry(
            null, new RegistryValue("MultilineLink", RegistryValueType.Link, Encoding.Unicode.GetBytes(linkPath)));
        check(
            linkRow.DataPreview == @"HKLM\first line second line" && linkRow.Data == linkPath,
            "Resolved link previews separate lines with spaces without changing the stored target path");

        var multilineMulti = new RegistryValue(
            "MultilineMulti", RegistryValueType.MultiString,
            Encoding.Unicode.GetBytes("one\r\ntwo\0three\rfour\nfive\0\0"));
        check(
            multilineMulti.DisplayPreview == "one two · three four five" &&
            multilineMulti.DisplayData == "one\r\ntwo · three\rfour\nfive",
            "Multi-string previews flatten embedded line breaks with spaces and retain entry separators");
        var newlinePrefix = new RegistryValue(
            "NewlinePrefix", RegistryValueType.String, Encoding.Unicode.GetBytes(new string('\n', 524_288) + "tail\0"));
        allocated = GC.GetAllocatedBytesForCurrentThread();
        var newlinePreview = newlinePrefix.DisplayPreview;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(
            newlinePreview == new string(' ', 256) + "…" && allocated < 16_384,
            "Flattening a large newline-only prefix keeps the bounded allocation and continuation indicator");

        var binary = cases.Single(sample => sample.Name == "Unknown");
        var binaryInspector = new ValueInspectorViewModel(binary);
        var numberInspector = new ValueInspectorViewModel(cases.Single(sample => sample.Name == "Number"));
        check(
            binary.DisplayPreview == binary.DisplayData && binary.DisplayPreview.EndsWith("FF …") &&
            binary.DisplayPreview.Split(' ').Length == 257,
            "Binary and unknown previews retain 256 complete bytes and their existing continuation indicator");
        check(
            !binaryInspector.HasText && binaryInspector.Data == string.Empty &&
            binaryInspector.RawBytes.Length == 800 &&
            binaryInspector.Heading.EndsWith($"{binary.Data.Length:N0} bytes") && !numberInspector.HasText &&
            numberInspector.HasNumber && numberInspector.Data == string.Empty,
            "Non-text inspection preserves byte counts raw bytes and numeric controls without a partial full-text field");
        var longName = new RegistryEntry(null, value with { Name = new string('n', 16_000) });
        check(
            longName.Name.Length == 16_000 && longName.NamePreview.Length == 257 &&
            longName.AccessibleName.Length < 600,
            "Long value names are bounded in row text tooltips and automation without changing identity");
        check(
            new ValueInspectorViewModel(longName.Value!).Name == longName.Name,
            "Inspection exposes the complete value name for keyboard selection and copying");
        if (!useRegistry)
        {
            return;
        }

        var location = RegistryLocation.Parse(@"HKCU\Software\RegeditLargeValue-" + Guid.NewGuid().ToString("N"));
        var service = new RegistryService();
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".reg");
        service.CreateKey(location.Parent!, location.Name);
        try
        {
            var prefix = new string('x', 524_288 - 1);
            service.Write(
                location, value with { Name = "ZSortFirst", Data = Encoding.Unicode.GetBytes(prefix + "a\0") }, null);
            service.Write(
                location, value with { Name = "ASortLast", Data = Encoding.Unicode.GetBytes(prefix + "z\0") }, null);
            service.Write(location, value, null);
            var clipboard = new RegistryEntryClipboard();
            var browser = new BrowserViewModel(service, location, clipboard);
            await browser.NavigateAsync(location);
            browser.Filter = "suffixneedle";
            await browser.PendingFilter;
            check(
                browser.Entries.Count == 1 && browser.Entries[0].Value?.Name == "Large",
                "Filtering finds a suffix beyond the bounded large-value row preview");
            browser.SelectedEntry = browser.Entries[0];
            string? copied = null;
            browser.CopyTextRequested += text => copied = text;
            browser.CopyCommand.Execute(null);
            check(
                copied == contents && clipboard.Entry?.Value?.SameData(value) == true,
                "Copy retains the complete large string text and exact registry bytes");
            string? inspected = null;
            browser.IsInspectorOpen = false;
            browser.InspectorRequested += model =>
            {
                inspected = model.Data;
                return Task.CompletedTask;
            };
            await browser.InspectCommand.ExecuteAsync(null);
            check(inspected == contents, "The Inspect command supplies full contents without a preview preference");
            browser.Filter = string.Empty;
            browser.SortCommand.Execute(RegistrySortColumn.Data);
            var sorted = browser.Entries.Where(entry => entry.Value?.Name is "ZSortFirst" or "ASortLast").ToArray();
            check(
                sorted[0].Value!.Name == "ZSortFirst" && sorted[1].Value!.Name == "ASortLast",
                "Data sorting compares suffixes beyond identical large-value previews");
            await RegistryExport.ExportAsync(service, [location], file);
            var exported = (await File.ReadAllTextAsync(file)).Replace(",\\\r\n  ", ",");
            var line = exported.Split("\r\n").Single(text => text.StartsWith("\"Large\"=hex(1):"));
            var bytes = Convert.FromHexString(line[(line.IndexOf(':') + 1)..].Replace(",", string.Empty));
            check(
                bytes.AsSpan().SequenceEqual(value.Data), "Registry export retains every byte beyond the row preview");
            browser.CancelPending();
        }
        finally
        {
            service.DeleteKey(location);
            File.Delete(file);
        }
    }
}
