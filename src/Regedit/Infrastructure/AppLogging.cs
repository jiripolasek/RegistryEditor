// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;

namespace JPSoftworks.Regedit.Infrastructure;

internal static class AppLogging
{
    internal static string Folder => Path.Combine(AppSettings.Folder, "Logs");

    internal static ILoggerFactory Open(
        string? appDataFolder = null,
        long fileSizeLimitBytes = 5 * 1024 * 1024,
        int retainedFileCountLimit = 7)
    {
        try
        {
            var folder = appDataFolder is null ? Folder : Path.Combine(appDataFolder, "Logs");
            Directory.CreateDirectory(folder);
            var logger = new LoggerConfiguration().Enrich.WithProperty("ProcessId", Environment.ProcessId).WriteTo.File(
                Path.Combine(folder, "regedit-.log"),
                outputTemplate: "{Timestamp:O} [{Level:u3}] [{ProcessId}] {SourceContext}: {Message:lj}{NewLine}",
                rollingInterval: RollingInterval.Day, fileSizeLimitBytes: fileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit, shared: true).CreateLogger();
            return new SerilogLoggerFactory(logger, true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Registry Editor file logging unavailable ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            return NullLoggerFactory.Instance;
        }
    }

    internal static void LogFailure(
        this Microsoft.Extensions.Logging.ILogger logger,
        LogLevel level,
        string operation,
        Exception exception)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        // Exception messages can contain registry values, note text, or launch arguments.
        logger.Log(
            level, "{Operation} failed ({ExceptionType}, 0x{HResult:X8}). {StackTrace}", operation,
            exception.GetType().FullName, exception.HResult, new StackTrace(exception, false).ToString());
    }
}
