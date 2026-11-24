// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Controls;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class HexEditorTests
{
    internal static void Run(Action<bool, string> check)
    {
        byte[] original = [0x12, 0x34, 0xFF];
        var document = new HexDocument(new MemoryStream(original, writable: false));
        var editor = new HexEditorBuffer();
        editor.Load(document);
        check(
            editor.Type('a', false) && editor.LowNibble && editor.Type('b', false) && editor.Caret == 1,
            "Typing two hex digits overwrites a byte and advances");
        editor.MoveTo(2, extend: true);
        check(
            editor.SelectionStart == 1 && editor.SelectionLength == 2 && editor.TryPaste(" 00\r\n7e 46 ", false, out _),
            "Pasting hex replaces the entire selected range");
        var unchanged = document.Revision;
        check(
            !editor.TryPaste("AF Z", false, out _)
            && !editor.TryPaste("0", false, out _)
            && !editor.TryPaste("é", true, out _)
            && unchanged == document.Revision, "Invalid clipboard data leaves the whole draft intact");
        editor.MoveTo(2);
        editor.MoveTo(0, extend: true);
        check(
            editor.SelectionLength == 3 && editor.Delete(false) && editor.Type('X', true),
            "Reverse selection, deletion and ASCII overwrite");
        check(editor.Type('F', false) && editor.Type('e', false) && editor.Caret == 2, "Hex entry appends at the end");
        using var output = new MemoryStream();
        document.CopyTo(output);
        check(
            output.ToArray().AsSpan().SequenceEqual((byte[])[0x58, 0xFE]) && original.AsSpan().SequenceEqual((byte[])[0x12, 0x34, 0xFF]),
            "Export contains the edited bytes and preserves the source");
        editor.SetEditOptions(readOnly: true, fixedLength: false);
        check(
            editor.Caret == 1 && !editor.Type('0', false) && !editor.TryPaste("AB", false, out _) && !editor.Delete(false),
            "Read-only mode clamps the append caret and rejects every edit route");
        editor.Load(new HexDocument(Stream.Null));
        editor.SetEditOptions(readOnly: false, fixedLength: false);
        check(
            editor.SelectionLength == 0 && !editor.Delete(true) && editor.Type('a', false) && editor.Type('5', false),
            "Empty data accepts the first byte directly");

        CheckPieces(check);
        CheckLargeOffsets(check);
        CheckFixedLength(check);
        CheckMaxLength(check);
        CheckCaretAndInsertPaste(check);
        CheckOverwritePaste(check);
        CheckPasteAllocation(check);
        CheckSharedEdits(check);
        CheckHistory(check);
        CheckLargeEditRetention(check);

        var line = HexEditorRow.Format([0, (byte)'-', (byte)' '], 0, 8);
        check(
            line[HexEditorRow.HexStart(8)..HexEditorRow.AsciiStart(8)].TrimEnd() == "00 2D 20"
            && line[HexEditorRow.AsciiStart(8)..] == ".- "
            && HexEditorRow.Format([], 16, 8).TrimEnd() == "00000010",
            "Partial and empty rows contain only real bytes, including a trailing ASCII space");
    }

    private static void CheckCaretAndInsertPaste(Action<bool, string> check)
    {
        var editor = new HexEditorBuffer();
        var document = new HexDocument(new MemoryStream([0x10, 0x20, 0x30]));
        editor.Load(document);
        editor.MoveTo(2, extend: true);
        var bytes = new byte[5];
        check(
            editor.Type('A', false)
            && editor.Type('B', false)
            && document.Read(0, bytes) == 3
            && bytes.AsSpan(0, 3).SequenceEqual((byte[])[0x10, 0x20, 0xAB]), "Hex typing edits the active end of a forward selection");
        editor.MoveTo(2);
        editor.MoveTo(0, extend: true);
        check(
            editor.Type('!', true) && document.Read(0, bytes) == 3 && bytes[0] == '!' && bytes[1] == 0x20,
            "ASCII typing edits the active end of a reverse selection");
        editor.MoveTo(2, extend: true);
        editor.MoveTo(1, extend: true);
        check(
            editor.TryPaste("A A\r\nBB", false, out _)
            && document.Read(0, bytes) == 5
            && bytes.SequenceEqual((byte[])[0x21, 0xAA, 0xBB, 0x20, 0xAB]),
            "Collapsing a selection restores insert paste before the current byte");
        editor.Select(1, 1);
        check(
            editor.TryPaste("Z", true, out _) && document.Read(0, bytes) == 5 && bytes[1] == 'Z' && bytes[2] == 0xBB,
            "An explicitly selected single byte is replaced by paste");
        editor.Select(2, 1);
        check(
            editor.Delete(true) && document.Read(0, bytes) == 4 && bytes[1] == 'Z' && bytes[2] == 0x20,
            "Backspace deletes an explicitly selected byte before reverting to caret behavior");
        check(editor.Delete(true) && document.Read(0, bytes) == 3 && bytes[1] == 0x20, "Backspace at a caret removes the preceding byte");
        editor.Select(0, 1);
        document.Replace(0, document.Length, []);
        editor.SetEditOptions(readOnly: false, fixedLength: false);
        document.Replace(0, 0, [0x12]);
        editor.SetEditOptions(readOnly: false, fixedLength: false);
        check(
            editor.TryPaste("34", false, out _) && document.Read(0, bytes) == 2 && bytes[0] == 0x34 && bytes[1] == 0x12,
            "Emptying a shared draft clears a previous single-byte selection");
    }

    private static void CheckPasteAllocation(Action<bool, string> check)
    {
        var text = new string('A', 256 * 1024);
        foreach (var ascii in new[] { false, true })
        {
            var editor = new HexEditorBuffer { MaxLength = 1 };
            editor.Load(new HexDocument(Stream.Null));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pasted = editor.TryPaste(text, ascii, out var message);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            check(
                pasted && editor.Length == 1 && message is not null && allocated < 65536,
                "Capped paste allocates only for the accepted prefix in " + (ascii ? "ASCII" : "hex"));
            editor.Select(0, 1);
            var revision = editor.Document!.Revision;
            check(
                !editor.TryPaste(text + "é", ascii, out _) && editor.Document.Revision == revision,
                "Invalid data beyond the cap rejects the entire paste");
            before = GC.GetAllocatedBytesForCurrentThread();
            pasted = editor.TryPaste(text, ascii, out _, overwrite: true);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            check(
                !pasted && editor.Document.Revision == revision && allocated < 65536, "An oversized overwrite is rejected before decoding");
        }
    }

    private static void CheckSharedEdits(Action<bool, string> check)
    {
        var document = new HexDocument(new MemoryStream([0, 0]));
        var first = new HexEditorBuffer();
        var second = new HexEditorBuffer();
        first.Load(document);
        second.Load(document);
        first.Type('A', false);
        second.MoveTo(1);
        second.Type('E', false);
        first.ApplyExternalChange(document.LastChange);
        first.Type('B', false);
        Span<byte> pair = stackalloc byte[2];
        document.Read(0, pair);
        check(pair[0] == 0xAB && pair[1] == 0xE0, "An independent overwrite in another view preserves the pending low digit");
        first.Type('C', false);
        second.MoveTo(0);
        second.TryPaste("22", false, out _);
        first.ApplyExternalChange(document.LastChange);
        check(first.Caret == 2 && first.LowNibble, "An insertion before a half-typed byte moves its caret with the original byte");
        second.MoveTo(2);
        second.Type('3', false);
        first.ApplyExternalChange(document.LastChange);
        check(!first.LowNibble, "An external replacement of the pending byte resets its digit");
        first.Select(1, 1);
        second.Select(0, 2);
        second.Delete(false);
        first.ApplyExternalChange(document.LastChange);
        check(!first.HasSelection && first.Caret == 0, "An external deletion drops a selection whose bytes were removed");
        document = new HexDocument(new MemoryStream([0, 1, 2, 3, 4]));
        first.Load(document);
        first.Select(1, 3);
        document.Replace(2, 2, []);
        first.ApplyExternalChange(document.LastChange);
        check(
            first.SelectionStart == 1 && first.SelectionLength == 1,
            "Deleting the end of a shared selection excludes the next untouched byte");
    }

    private static void CheckHistory(Action<bool, string> check)
    {
        var document = new HexDocument(new MemoryStream([0x12, 0x34]));
        var editor = new HexEditorBuffer();
        editor.Load(document);
        editor.Type('A', false);
        editor.Type('B', false);
        var bytes = new byte[8];
        check(
            editor.ReplayHistory(false) && document.Read(0, bytes) == 2 && bytes[0] == 0x12 && !editor.CanReplayHistory(false),
            "Undo restores both digits of a typed byte in one step");
        check(editor.ReplayHistory(true) && document.Read(0, bytes) == 2 && bytes[0] == 0xAB, "Redo restores a completed hex byte");
        editor.Select(1, 1);
        editor.TryPaste("XYZ", true, out _);
        editor.Select(0, document.Length);
        editor.Delete(false);
        check(
            editor.ReplayHistory(false) && document.Read(0, bytes) == 4 && bytes.AsSpan(0, 4).SequenceEqual((byte[])[0xAB, 88, 89, 90]),
            "Undo of Select All and Delete restores source and edited bytes together");
        check(
            editor.ReplayHistory(false) && document.Read(0, bytes) == 2 && bytes[1] == 0x34,
            "Undo of a replacement paste restores the original length and content");
        editor.SetEditOptions(readOnly: true, fixedLength: false);
        check(!editor.ReplayHistory(false) && !editor.ReplayHistory(true), "Undo and redo respect read-only mode");
        editor.SetEditOptions(readOnly: false, fixedLength: true);
        check(!editor.ReplayHistory(true), "A fixed-length view rejects history that would resize the document");
        editor.SetEditOptions(readOnly: false, fixedLength: false);
        editor.MaxLength = 2;
        check(!editor.ReplayHistory(true), "Redo respects the current MaxLength without consuming history");
        editor.MaxLength = 0;
        check(
            editor.ReplayHistory(true) && editor.ReplayHistory(false),
            "A blocked history entry remains available after the limit is lifted");
        editor.Type('!', true);
        check(!editor.CanReplayHistory(true), "A new edit invalidates redo");

        using var source = new CountingStream(6L << 30);
        editor.Load(new HexDocument(source));
        editor.Select(0, 5L << 30);
        editor.Delete(false);
        check(
            editor.ReplayHistory(false)
            && editor.Length == source.Length
            && editor.ReplayHistory(true)
            && editor.Length == 1L << 30
            && source.BytesRead == 0, "Undo and redo of a multi-GiB source deletion perform no source reads");

        var random = new Random(42);
        document = new HexDocument(new MemoryStream([0x10, 0x20]));
        editor.Load(document);
        List<byte[]> states = [[0x10, 0x20]];
        for (var i = 0; i < 48; i++)
        {
            var previous = states[^1];
            var at = random.Next(previous.Length + 1);
            var remove = random.Next(Math.Min(9, previous.Length - at) + 1);
            var insert = new byte[i == 0 ? 20000 : random.Next(1, 8)];
            random.NextBytes(insert);
            document.Replace(at, remove, insert);
            states.Add([.. previous.AsSpan(0, at), .. insert, .. previous.AsSpan(at + remove)]);
        }

        foreach (var redo in new[] { false, true })
        {
            foreach (var expected in redo ? states.Skip(1) : states.SkipLast(1).Reverse())
            {
                var actual = new byte[expected.Length];
                if (!editor.ReplayHistory(redo)
                    || document.Length != expected.Length
                    || document.Read(0, actual) != actual.Length
                    || !actual.SequenceEqual(expected))
                {
                    throw new InvalidOperationException("Undo or redo differed from the reference array.");
                }
            }
        }

        check(true, "Full undo and redo reproduce mixed insertions and replacements spanning multiple edit blocks");
    }

    private static void CheckLargeEditRetention(Action<bool, string> check)
    {
        var baseline = GC.GetTotalMemory(true);
        var document = DeleteMostOfPaste();
        var retained = GC.GetTotalMemory(true) - baseline;
        Span<byte> last = stackalloc byte[1];
        check(
            document.Read(0, last) == 1 && last[0] == 42 && retained < 8 * 1024 * 1024,
            "A surviving byte and bounded undo history release the rest of a 32 MiB paste");
        GC.KeepAlive(document);

        static HexDocument DeleteMostOfPaste()
        {
            var bytes = new byte[32 * 1024 * 1024];
            bytes[^1] = 42;
            var document = new HexDocument(Stream.Null);
            document.Replace(0, 0, bytes);
            document.Replace(0, bytes.Length - 1, []);
            return document;
        }
    }

    private static void CheckOverwritePaste(Action<bool, string> check)
    {
        var editor = new HexEditorBuffer { MaxLength = 4 };
        var document = new HexDocument(new MemoryStream([0, 1, 2, 3, 4, 5]));
        editor.Load(document);
        editor.MoveTo(2);
        var bytes = new byte[6];
        check(
            editor.TryPaste("AA bb", false, out var message, overwrite: true)
            && message is null
            && editor.Caret == 4
            && document.Length == 6
            && document.Read(0, bytes) == 6
            && bytes.SequenceEqual((byte[])[0, 1, 0xAA, 0xBB, 4, 5]),
            "Overwrite paste extends past a single selected byte and preserves offsets above MaxLength");
        editor.MoveTo(1);
        editor.MoveTo(4, extend: true);
        check(
            editor.TryPaste("Z", true, out _, overwrite: true)
            && editor.Caret == 2
            && document.Length == 6
            && document.Read(0, bytes) == 6
            && bytes.SequenceEqual((byte[])[0, (byte)'Z', 0xAA, 0xBB, 4, 5]),
            "A shorter ASCII overwrite starts at the first selected byte and preserves the selection tail");
        editor.MoveTo(4);
        var revision = document.Revision;
        check(
            !editor.TryPaste("AABBCC", false, out message, overwrite: true)
            && message is not null
            && document.Revision == revision
            && editor.Caret == 4, "An overwrite beyond the end rejects the entire paste");
        check(
            editor.TryPaste("AABB", false, out _, overwrite: true)
            && editor.Caret == document.Length
            && !editor.TryPaste("FF", false, out _, overwrite: true)
            && document.Length == 6, "An overwrite may finish at the end but cannot append");
        editor.SetEditOptions(readOnly: true, fixedLength: false);
        check(
            !editor.TryPaste("FF", false, out _, overwrite: true) && document.Revision == revision + 1,
            "Read-only mode rejects overwrite paste");
    }

    private static void CheckMaxLength(Action<bool, string> check)
    {
        var editor = new HexEditorBuffer { MaxLength = 2 };
        var document = new HexDocument(Stream.Null);
        editor.Load(document);
        check(
            editor.Type('a', true) && editor.Type('B', false) && editor.Type('F', false) && document.Length == 2,
            "MaxLength counts bytes and allows both nibbles of the last byte");
        var revision = document.Revision;
        check(
            !editor.Type('z', true)
            && !editor.CanWriteByte(editor.Caret)
            && !editor.TryPaste("FF", false, out _)
            && revision == document.Revision, "Further input at the end preserves a full document");
        editor.MoveTo(0);
        check(
            !editor.TryPaste("11", false, out _) && revision == document.Revision,
            "Insert paste at a full document's interior caret preserves every byte");
        editor.Select(0, 1);
        check(
            editor.TryPaste("11 22 33", false, out var partial) && partial is not null && document.Length == 2,
            "Pasting at the cap replaces the selected byte with one complete clipboard byte");
        var bytes = new byte[2];
        document.Read(0, bytes);
        check(bytes.SequenceEqual((byte[])[0x11, 0xBF]), "A capped paste leaves neighboring bytes intact");
        editor.Select(0, 2);
        check(
            editor.TryPaste("xyz", true, out partial) && partial is not null && document.Length == 2,
            "A replacement paste uses the space freed by a multi-byte selection");
        editor.MaxLength = 1;
        editor.MoveTo(0);
        check(
            document.Length == 2 && editor.Type('A', true) && editor.Delete(false) && document.Length == 1,
            "Lowering MaxLength preserves existing data and allows overwrite and shrink");
        editor.MaxLength = 0;
        editor.MoveTo(document.Length);
        check(editor.TryPaste("yz", true, out _) && document.Length == 3, "Zero restores unrestricted input");
        editor.MaxLength = 4;
        editor.MoveTo(document.Length);
        check(
            editor.TryPaste("AB CD", false, out partial) && partial is not null && document.Length == 4,
            "Append paste retains only the complete bytes that fit");
        using var large = new CountingStream(5L << 30);
        editor.MaxLength = large.Length + 1;
        editor.Load(new HexDocument(large));
        editor.MoveTo(large.Length);
        check(
            editor.Type('X', true) && !editor.Type('Y', true) && editor.Length == large.Length + 1 && large.BytesRead == 0,
            "A 64-bit MaxLength applies beyond four GiB without reading the source");
    }

    private static void CheckFixedLength(Action<bool, string> check)
    {
        var editor = new HexEditorBuffer();
        foreach (var fixedDocument in new[] { false, true })
        {
            var document = new HexDocument(new MemoryStream([1, 2, 3, 4]), fixedDocument);
            editor.Load(document);
            editor.MoveTo(4);
            editor.SetEditOptions(readOnly: false, fixedLength: !fixedDocument);
            check(
                editor.Caret == 3 && !editor.CanResize && editor.Type('A', false) && editor.Type('B', false) && editor.Caret == 3,
                "Fixed length clamps the append position and permits overwriting the final byte");
            editor.Select(1, 3);
            check(
                editor.TryPaste("xy", true, out _) && !editor.Delete(true) && !editor.Delete(false),
                "A short fixed-length paste overwrites bytes and deletion is unavailable");
            editor.MoveTo(2);
            var revision = document.Revision;
            check(
                !editor.TryPaste("DE AD BE EF", false, out var error) && error is not null && document.Revision == revision,
                "An oversized paste rejects the entire edit");
            var data = new byte[4];
            check(
                document.Length == 4 && document.Read(0, data) == 4 && data.SequenceEqual((byte[])[1, (byte)'x', (byte)'y', 0xAB]),
                "Fixed-length editing preserves the unmodified bytes and size");
            editor.SetEditOptions(readOnly: true, fixedLength: false);
            check(
                !editor.CanWrite && !editor.Type('Z', true) && !editor.TryPaste("00", false, out _) && !editor.Delete(false),
                "The viewer takes precedence over either fixed-length setting");
            editor.SetEditOptions(readOnly: false, fixedLength: false);
            check(editor.CanResize != fixedDocument, "A view cannot unlock a fixed-length document");
            if (fixedDocument)
            {
                try
                {
                    document.Replace(0, 0, [1]);
                }
                catch (InvalidOperationException)
                {
                }

                check(
                    document.Length == 4 && document.Revision == revision, "The shared document rejects resizing at the mutation boundary");
            }
        }

        editor.Load(new HexDocument(Stream.Null, isFixedLength: true));
        check(
            !editor.CanWrite && !editor.Type('F', false) && !editor.TryPaste("00", false, out _),
            "An empty fixed-length document has no editable position");
    }

    private static void CheckPieces(Action<bool, string> check)
    {
        var random = new Random(181);
        var source = Enumerable.Range(0, 250).Select(i => (byte)i).ToArray();
        var document = new HexDocument(new MemoryStream(source, writable: false));
        var expected = source.ToList();
        for (var i = 0; i < 500; i++)
        {
            var offset = random.Next(expected.Count + 1);
            var remove = random.Next(Math.Min(expected.Count - offset, 8) + 1);
            var insert = new byte[random.Next(10)];
            random.NextBytes(insert);
            document.Replace(offset, remove, insert);
            expected.RemoveRange(offset, remove);
            expected.InsertRange(offset, insert);
            var actual = new byte[expected.Count];
            if (document.Length != actual.Length || document.Read(0, actual) != actual.Length || !actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException($"Draft differed from the reference array after edit {i}.");
            }
        }

        check(true, "Interleaved insert, overwrite and delete match a byte-array reference");

        using var stream = new CountingStream(65536);
        document = new HexDocument(stream);
        for (var offset = 1024; offset < stream.Length; offset += 1024)
        {
            document.Replace(offset, 0, [42]);
            document.Replace(offset, 1, []);
        }

        var restored = new byte[(int)stream.Length];
        check(
            document.Read(0, restored) == restored.Length
            && stream.ReadCalls == 1
            && restored.Select((value, i) => value == i % 251).All(matches => matches),
            "Reverted edits restore a contiguous source range that can be read in a single call");
    }

    private static void CheckLargeOffsets(Action<bool, string> check)
    {
        using var source = new CountingStream(long.MaxValue);
        var document = new HexDocument(source);
        check(document.Length == long.MaxValue && source.BytesRead == 0, "Opening a large source reads only its length");
        const long offset = (5L << 30) + 8190;
        Span<byte> row = stackalloc byte[16];
        document.Read(offset, row);
        document.Read(offset + 18, row);
        check(
            row[0] == (byte)((offset + 18) % 251) && source.BytesRead == 16384,
            "Visible rows above 4 GiB share bounded reads across a page boundary");

        var editor = new HexEditorBuffer();
        editor.Load(document);
        editor.MoveTo(offset);
        editor.Type('b', false);
        editor.Type('c', false);
        var beforeDelete = source.BytesRead;
        editor.Select(0, offset);
        editor.Delete(false);
        check(source.BytesRead == beforeDelete, "Range deletion performs no source I/O");
        document.Read(0, row);
        check(
            row[0] == 0xBC && document.Length == long.MaxValue - offset && source.BytesRead <= beforeDelete + 16384,
            "Deleting a five GiB prefix shifts the draft without reading or writing the intervening bytes");
        document = new HexDocument(source);
        editor.Load(document);
        editor.MoveTo(long.MaxValue - 2);
        editor.MoveBy(128, true);
        check(editor.Caret == long.MaxValue && editor.SelectionLength == 2, "Page movement clamps at the signed 64-bit limit");
        editor.SetEditOptions(readOnly: true, fixedLength: false);
        editor.Select(0, long.MaxValue);
        check(editor.SelectionLength == long.MaxValue && editor.Caret == 0, "Select all retains a 64-bit range without materializing it");
        editor.Select(long.MaxValue - 1, long.MaxValue);
        var beforeLast = source.BytesRead;
        document.Read(editor.Caret, row);
        var line = HexEditorRow.Format(row[..1], editor.Caret, 16);
        check(
            line.StartsWith("7FFFFFFFFFFFFFFE", StringComparison.Ordinal) && source.BytesRead <= beforeLast + 8192,
            "The last byte displays its full address with bounded I/O");
    }

    private sealed class CountingStream : Stream
    {
        public long BytesRead { get; private set; }

        public int ReadCalls { get; private set; }

        public override long Length { get; }

        public override long Position { get; set; }

        public override bool CanRead => true;

        public override bool CanWrite => false;

        public override bool CanSeek => true;

        public CountingStream(long length)
        {
            this.Length = length;
        }

        public override int Read(Span<byte> buffer)
        {
            this.ReadCalls++;
            var count = (int)Math.Min(this.Length - this.Position, buffer.Length);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = (byte)((this.Position + i) % 251);
            }

            this.Position += count;
            this.BytesRead += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
