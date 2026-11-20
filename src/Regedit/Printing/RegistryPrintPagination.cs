// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Printing;

internal static class RegistryPrintPagination
{
    internal static RegistryPrintPageSlice FindNextSlice(string report, int start, Func<int, int, bool> fits, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(fits);
        if ((uint)start >= (uint)report.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var remaining = report.Length - start;
        var low = 0;
        var high = 1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!fits(start, high))
            {
                break;
            }

            low = high;
            if (high == remaining)
            {
                return new RegistryPrintPageSlice(start, AtomicLength(report, start, high));
            }

            high = (int)Math.Min(remaining, (long)high * 2);
        }

        var left = low + 1;
        var right = high - 1;
        while (left <= right)
        {
            token.ThrowIfCancellationRequested();
            var middle = left + ((right - left) / 2);
            if (fits(start, middle))
            {
                low = middle;
                left = middle + 1;
            }
            else
            {
                right = middle - 1;
            }
        }

        return new RegistryPrintPageSlice(start, AtomicLength(report, start, Math.Max(1, low)));
    }

    private static int AtomicLength(string report, int start, int length)
    {
        var end = start + length;
        if (end < report.Length
            && length > 1
            && (char.IsHighSurrogate(report[end - 1]) && char.IsLowSurrogate(report[end])
                || report[end - 1] == '\r' && report[end] == '\n'))
        {
            length--;
        }

        // A single code unit may be the first half of an indivisible pair. Keep the pair together
        // and advance, even when that means the page exceeds its measured limit by one code unit.
        if (length == 1
            && start + 1 < report.Length
            && (char.IsHighSurrogate(report[start]) && char.IsLowSurrogate(report[start + 1])
                || report[start] == '\r' && report[start + 1] == '\n'))
        {
            return 2;
        }

        return length;
    }
}
