// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ValueEditorInputTests
{
    internal static void Run(Action<bool, string> check)
    {
        var original = new RegistryValue("Number", RegistryValueType.DWord, [255, 0, 0, 0]);
        var number = new RegistryValueEditorViewModel(original, RegistryValueType.DWord, false, false);
        foreach (var type in new RegistryValueType[] { RegistryValueType.DWord, RegistryValueType.DWordBigEndian, RegistryValueType.QWord })
        {
            var size = type == RegistryValueType.QWord ? 8 : 4;
            foreach (var length in new[] { 0, size - 1, size + 1 })
            {
                var value = new RegistryValue("Malformed", type, Enumerable.Repeat((byte)255, length).ToArray());
                var editor = new RegistryValueEditorViewModel(value, type, false, false);
                check(
                    editor.IsRaw
                    && !editor.IsNumeric
                    && editor.HasRawFallback
                    && editor.RawFallbackMessage.Contains($"{value.TypeName} requires {size} bytes")
                    && editor.RawFallbackMessage.Contains($"contains {length:N0}")
                    && editor.Validate()
                    && editor.Value.SameData(value),
                    $"Malformed {value.TypeName} explains the binary fallback and preserves all {length} bytes");
                check(
                    !new RegistryValueEditorViewModel(value, type, true, false).HasRawFallback,
                    "Explicit binary editing does not report an automatic fallback");
            }

            var valid = new RegistryValueEditorViewModel(new RegistryValue("Valid", type, new byte[size]), type, false, false);
            var created = new RegistryValueEditorViewModel(null, type, false, true);
            check(
                valid.IsNumeric && !valid.HasRawFallback && created.IsNumeric && !created.HasRawFallback,
                $"Valid and new {valid.TypeName} values keep the numeric editor without a fallback notice");
        }

        var malformedString = new RegistryValueEditorViewModel(
            new RegistryValue("Malformed", RegistryValueType.String, [65]), RegistryValueType.String, false, false);
        check(
            malformedString.HasRawFallback && malformedString.RawFallbackMessage.Contains("REG_SZ"),
            "An automatic fallback for malformed string data also explains why the binary editor is used");
        foreach (var type in new RegistryValueType[] { RegistryValueType.Binary, (RegistryValueType)99 })
        {
            check(
                !new RegistryValueEditorViewModel(new RegistryValue("Raw", type, [1]), type, false, false).HasRawFallback,
                "Values normally edited as bytes do not report an automatic fallback");
        }

        check(
            number.DataHelp.Contains("255") && number.DataHelp.Contains("0xFF") && !number.CanApplyDataCorrection,
            "Numeric help shows both bases without changing the draft or offering an unnecessary correction");
        var baseChange = new RegistryValueEditorViewModel(
            new RegistryValue("Number", RegistryValueType.DWord, [16, 0, 0, 0]), RegistryValueType.DWord, false, false)
        {
            BaseIndex = 1, Data = "10"
        };
        check(
            baseChange.DataHelp.Contains("Decimal: 10") && baseChange.Validate() && baseChange.Value.Data[0] == 10,
            "A decimal draft matching the original hexadecimal text still uses the selected base");
        number.BaseIndex = 1;
        number.Data = "4294967296";
        check(
            number.DataHelp.Contains("4294967295") && !number.HasError && number.Value.SameData(original),
            "Live range guidance is advisory and leaves the committed value unchanged");
        check(
            !number.Validate() && number.Error!.Contains("4294967295") && number.Data == "4294967296",
            "Saving an out-of-range DWORD reports its actual limit and keeps the entered text");
        var qword = new RegistryValueEditorViewModel(null, RegistryValueType.QWord, false, true)
        {
            BaseIndex = 1, Data = "18446744073709551616"
        };
        check(qword.DataHelp.Contains("18446744073709551615") && !qword.Validate(), "QWORD range guidance uses the unsigned 64-bit limit");

        foreach (var (type, numberBase, literal, text, bytes) in new (RegistryValueType, int, string, string, byte[])[]
                 {
                     (RegistryValueType.DWord, 0, "dword:00000010", "10", [16, 0, 0, 0]),
                     (RegistryValueType.DWord, 1, "DWORD:00000010", "16", [16, 0, 0, 0]),
                     (RegistryValueType.DWord, 1, "0x10", "16", [16, 0, 0, 0]),
                     (RegistryValueType.DWordBigEndian, 1, "hex(5):00,00,00,10", "16", [0, 0, 0, 16]),
                     (RegistryValueType.QWord, 1, "hex(b):ff,ff,ff,ff,ff,ff,ff,ff", "18446744073709551615",
                         [255, 255, 255, 255, 255, 255, 255, 255])
                 })
        {
            var editor = new RegistryValueEditorViewModel(null, type, false, true) { BaseIndex = numberBase, Data = literal };
            check(
                editor.CanApplyDataCorrection
                && editor.ApplyDataCorrectionCommand.CanExecute(null)
                && editor.Data == literal
                && editor.Value.Data.All(value => value == 0), "Recognizing pasted numeric data only offers a correction: " + literal);
            editor.ApplyDataCorrectionCommand.Execute(null);
            check(
                editor.Data == text
                && editor.BaseIndex == numberBase
                && !editor.CanApplyDataCorrection
                && editor.Validate()
                && editor.Value.Type == type
                && editor.Value.Data.SequenceEqual(bytes),
                "Explicit conversion preserves the numeric value, selected base, type and byte order: " + literal);
        }

        var binary = new RegistryValueEditorViewModel(
            new RegistryValue("Bytes", RegistryValueType.Binary, [1, 2]), RegistryValueType.Binary, false, false);
        binary.Data = "01A4FF";
        check(
            binary.Validate() && !binary.CanApplyDataCorrection && binary.PreviewBytes.SequenceEqual(new byte[] { 1, 164, 255 }),
            "Already-valid compact hexadecimal input stays accepted without conversion");
        binary.Data = "01 A";
        check(
            binary.DataHelp.Contains("two hexadecimal digits")
            && binary.DataHelp.Contains("last valid")
            && !binary.HasError
            && binary.PreviewBytes.SequenceEqual(new byte[] { 1, 164, 255 }),
            "Incomplete input keeps the last valid preview and explains that it is stale without blocking typing");
        foreach (var literal in new[] { "01,A4,FF", "hex:01, A4, FF", "HEX:01A4FF" })
        {
            binary.Data = literal;
            check(
                binary.Data == literal && binary.CanApplyDataCorrection, "Pasted binary data waits for an explicit conversion: " + literal);
            binary.ApplyDataCorrectionCommand.Execute(null);
            check(
                binary.Data == "01 A4 FF" && binary.Validate() && binary.Value.Data.SequenceEqual(new byte[] { 1, 164, 255 }),
                "Converting a validated byte list retains every byte: " + literal);
        }

        foreach (var literal in new[] { "01,GG", "01,,FF", "0,1", "00-11", "hex(2):01,02" })
        {
            binary.Data = literal;
            check(
                !binary.CanApplyDataCorrection && binary.Data == literal,
                "Help never drops unknown input or converts a literal for another value type: " + literal);
        }

        binary.ApplyBytes([42, 0]);
        check(
            binary.Data == "2A 00" && binary.DataHelp == "Bytes: 2" && !binary.CanApplyDataCorrection,
            "Editing the hex view clears stale input advice and keeps the text and help in sync");
        var target = new RegistryEditTarget(RegistryLocation.Parse(@"HKCU\Software"), original.Name);
        var denied = new UnauthorizedAccessException("Access denied");
        number.SetFailure(denied, target, false, _ => Task.CompletedTask);
        var recovery = number.Recovery;
        number.Data = "0x10";
        number.ApplyDataCorrectionCommand.Execute(null);
        check(
            number.Data == "16"
            && ReferenceEquals(number.Failure, denied)
            && ReferenceEquals(number.Recovery, recovery)
            && number.Error == denied.Message, "Typing and applying help preserve the existing write failure and permission recovery");

        foreach (var name in new[] { string.Empty, "@", "(Default)", "  named  ", @"a\b" })
        {
            RegistryService.ValidateValueName(name);
            var value = new RegistryValue(
                name, RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, @"  ""\\server\share""  "));
            var editor = new RegistryValueEditorViewModel(value, RegistryValueType.String, false, false);
            check(
                !editor.HasDataHelp && !editor.CanApplyDataCorrection && editor.Validate() && editor.Value.SameData(value),
                "Help preserves legal names, whitespace, quotes and literal UNC paths: " + name);
        }

        foreach (var value in new[]
                 {
                     new RegistryValue("Malformed", RegistryValueType.String, [65, 0, 255]),
                     new RegistryValue("Uncommon", (RegistryValueType)99, [0, 255, 1]),
                     new RegistryValue("Multi", RegistryValueType.MultiString, [0, 0])
                 })
        {
            var editor = new RegistryValueEditorViewModel(value, value.Type, false, false);
            check(
                !editor.CanApplyDataCorrection && editor.Validate() && editor.Value.SameData(value),
                "Adding help preserves an unchanged nonstandard value exactly: " + value.Name);
        }
    }
}
