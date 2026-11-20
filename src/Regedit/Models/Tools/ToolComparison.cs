// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;


namespace JPSoftworks.Regedit.Models.Tools;

public sealed record ToolComparison(
    string Name,
    string Before,
    string After,
    string BeforeLabel = "Before",
    string AfterLabel = "After",
    RegistryValue? BeforeValue = null,
    RegistryValue? AfterValue = null)
{
    public string DescribeByteDifference()
    {
        if (this.BeforeValue is null && this.AfterValue is null)
        {
            return "No value data.";
        }

        ReadOnlySpan<byte> before = this.BeforeValue?.Data;
        ReadOnlySpan<byte> after = this.AfterValue?.Data;
        var offset = before.CommonPrefixLength(after);
        if (this.BeforeValue is not null && this.AfterValue is not null && offset == before.Length && offset == after.Length)
        {
            return "Raw byte data is identical.";
        }

        if (offset == before.Length && offset == after.Length)
        {
            return this.BeforeValue is null ? "An empty value was added." : "An empty value was removed.";
        }

        static string BytesAt(RegistryValue? value, int index)
        {
            if (value is null)
            {
                return "Value absent";
            }

            if (index == value.Data.Length)
            {
                return "End of value";
            }

            return BitConverter.ToString(value.Data, index, Math.Min(16, value.Data.Length - index)).Replace('-', ' ');
        }

        return
            $"First differing byte at offset {offset:N0} (0x{offset:X}).\nBefore: {BytesAt(this.BeforeValue, offset)}\nAfter: {BytesAt(this.AfterValue, offset)}";
    }
}
