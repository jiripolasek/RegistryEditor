// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Globalization;
using JPSoftworks.Regedit.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class LoggingTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Directory.CreateTempSubdirectory("RegeditLogging.");
        try
        {
            var shared = Path.Combine(root.FullName, "Logs");
            Directory.CreateDirectory(shared);
            var date = DateTime.Today.AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var yesterday = Path.Combine(shared, $"regedit-{date}.log");
            File.WriteAllText(yesterday, "previous day");
            var error = new IOException("private-key-name", new ArgumentException("private-registry-value"));
            using (var first = AppLogging.Open(root.FullName))
            using (var second = AppLogging.Open(root.FullName))
            {
                var left = first.CreateLogger("FirstWindow");
                var right = second.CreateLogger("SecondWindow");
                Parallel.For(
                    0, 24, index =>
                    {
                        var logger = index % 2 == 0 ? left : right;
                        logger.LogInformation("Concurrent event {Marker}", $"shared-{index:D2}-end");
                    });
                left.LogFailure(LogLevel.Warning, "Saving session", error);
            }

            check(File.ReadAllText(yesterday) == "previous day", "Daily rolling leaves the previous day's log intact");
            var files = Directory.GetFiles(shared, "regedit-*.log").Where(path => path != yesterday).ToArray();
            var lines = files.SelectMany(File.ReadAllLines).ToArray();
            check(
                files.Length > 0
                && files.All(path => DateOnly.TryParseExact(
                    Path.GetFileNameWithoutExtension(path)["regedit-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
                && Enumerable.Range(0, 24).All(i => lines.Count(line => line.Contains($"shared-{i:D2}-end")) == 1),
                "Concurrent sessions preserve every event in the profile's Logs folder");
            var failure = lines.Single(line => line.Contains("Saving session"));
            check(
                failure.Contains(nameof(IOException))
                && (failure.Contains(error.HResult.ToString(CultureInfo.InvariantCulture))
                    || failure.Contains(error.HResult.ToString("X8"), StringComparison.OrdinalIgnoreCase)),
                "A logged failure identifies its operation, exception type and HRESULT");
            check(
                !lines.Any(line => line.Contains("private-key-name") || line.Contains("private-registry-value")),
                "Outer and inner exception messages stay out of persisted diagnostics");
            foreach (var path in files)
            {
                using var handle = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            check(true, "Disposing the last logger releases each file for exclusive access");

            var rolling = Path.Combine(root.FullName, "rolling");
            using (var factory = AppLogging.Open(rolling, 256, 3))
            {
                var logger = factory.CreateLogger("Retention");
                for (var index = 0; index < 40; index++)
                {
                    logger.LogInformation("Size roll {Marker}", $"retention-{index:D2}-end");
                }
            }

            var retained = Directory.GetFiles(Path.Combine(rolling, "Logs"), "regedit-*.log");
            var content = string.Join("\n", retained.Select(File.ReadAllText));
            check(
                retained.Length == 3 && content.Contains("retention-39-end") && !content.Contains("retention-00-end"),
                "Size rolling retains the three newest files and removes old events");

            var blocked = Path.Combine(root.FullName, "not-a-directory");
            File.WriteAllText(blocked, "preserve");
            using (var factory = AppLogging.Open(blocked))
            {
                factory.CreateLogger("Unavailable").LogError("An unavailable destination must allow the app to continue");
            }

            check(File.ReadAllText(blocked) == "preserve", "Logging tolerates an unavailable folder and preserves existing data");
        }
        finally
        {
            root.Delete(true);
        }
    }
}
