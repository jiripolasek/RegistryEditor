// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Models.Tools;

/// <summary>Clipboard text for result lists and values, shared by the tool and investigation surfaces.</summary>
internal static class ResultText
{
    internal static string Paths(IEnumerable<InvestigationRow> rows)
    {
        return string.Join(Environment.NewLine, rows.Select(row => row.Path).Distinct());
    }

    internal static string Table(IEnumerable<InvestigationRow> rows, Func<InvestigationRow, string> subject)
    {
        return string.Join(Environment.NewLine, rows.Select(row => $"{subject(row)}\t{row.Path}\t{row.TableSummary}"));
    }

    internal static string Hex(RegistryValue value)
    {
        return Convert.ToHexString(value.Data);
    }

    internal static string BytesCopied(RegistryValue value)
    {
        return $"Copied {value.Data.Length:N0} bytes as hexadecimal.";
    }
}
