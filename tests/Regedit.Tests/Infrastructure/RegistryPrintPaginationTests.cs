// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Printing;


namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class RegistryPrintPaginationTests
{
    internal static void Run(Action<bool, string> check)
    {
        const int pageLength = 1000;
        var report = new string('x', 100_123);
        long measuredCharacters = 0;
        var maxProbe = 0;
        var probeCount = 0;
        var pages = new List<RegistryPrintPageSlice>();
        for (var offset = 0; offset < report.Length;)
        {
            var page = RegistryPrintPagination.FindNextSlice(
                report, offset, (_, length) =>
                {
                    measuredCharacters += length;
                    maxProbe = Math.Max(maxProbe, length);
                    probeCount++;
                    return length <= pageLength;
                }, CancellationToken.None);
            pages.Add(page);
            offset += page.Length;
        }

        check(
            pages.Count == 101 && pages.Take(100).All(page => page.Length == pageLength) && pages[^1].Length == 123,
            "Print pagination preserves exact page boundaries and final remainder");
        check(
            string.Concat(pages.Select(page => report.Substring(page.Start, page.Length))) == report,
            "Print pagination reconstructs the exact UTF-16 report");
        check(
            probeCount < 2200 && measuredCharacters < 1_100_000 && maxProbe <= 1024,
            "Print pagination bounds prefix measurement work with exponential probes");

        var lineEnd = "a\r\nb";
        var linePages = SliceAll(lineEnd, 2);
        check(
            linePages.Select(page => lineEnd.Substring(page.Start, page.Length)).SequenceEqual(["a", "\r\n", "b"]),
            "Print pagination keeps CRLF on one page");

        var surrogate = "a\U0001F600b";
        var surrogatePages = SliceAll(surrogate, 2);
        check(
            surrogatePages.Select(page => surrogate.Substring(page.Start, page.Length)).SequenceEqual(["a", "\U0001F600", "b"]),
            "Print pagination keeps surrogate pairs intact and advances");

        using var cancelled = new CancellationTokenSource();
        var cancelledProbes = 0;
        try
        {
            RegistryPrintPagination.FindNextSlice(
                new string('x', 100), 0, (_, _) =>
                {
                    cancelledProbes++;
                    cancelled.Cancel();
                    return true;
                }, cancelled.Token);
            check(false, "Print pagination observes cancellation between probes");
        }
        catch (OperationCanceledException)
        {
            check(cancelledProbes == 1, "Print pagination observes cancellation between probes");
        }

        var generations = new RegistryPrintLayoutGeneration();
        var stale = generations.Begin();
        var current = generations.Begin();
        check(
            !generations.IsCurrent(stale) && generations.IsCurrent(current),
            "A superseded print layout generation cannot publish later work");
        var remainingPages = 3;
        var completedPages = 0;
        var complete = generations.CompleteSynchronously(
            current, () => remainingPages > 0, () =>
            {
                remainingPages--;
                completedPages++;
            });
        check(
            complete && completedPages == 3, "An early final page request synchronously completes the current layout before adding pages");
        generations.Cancel();
        check(!generations.IsCurrent(current), "Cancelling print preview invalidates queued layout work");
    }

    private static List<RegistryPrintPageSlice> SliceAll(string report, int capacity)
    {
        var slices = new List<RegistryPrintPageSlice>();
        for (var offset = 0; offset < report.Length;)
        {
            var page = RegistryPrintPagination.FindNextSlice(report, offset, (_, length) => length <= capacity, CancellationToken.None);
            slices.Add(page);
            offset += page.Length;
        }

        return slices;
    }
}
