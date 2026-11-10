// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Regedit.Persistence;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.Regedit.Infrastructure;

internal sealed class JumpListService(
    Func<AppSettings> loadSettings,
    IJumpListBackend backend,
    string settingsPath,
    ILogger<JumpListService> logger)
{
    internal Task UpdateAsync()
    {
        return Task.Run(async () =>
        {
            try
            {
                using var lease = await this.AcquireLeaseAsync().ConfigureAwait(false);
                // Read after acquiring the lease so another process's removals are included.
                var settings = loadSettings();
                if (settings.LoadError is not null)
                {
                    throw new IOException(settings.LoadError);
                }

                var entries = JumpListEntries.Create(settings.Favorites, settings.HiddenJumpListFavorites);
                await backend.UpdateAsync(
                    entries, removed =>
                    {
                        // Persist removals before the backend replaces Windows' removal records.
                        settings.HideJumpListFavorites(
                            JumpListEntries.RemovedFavoriteIds(entries, removed), settingsPath);
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogFailure(LogLevel.Warning, "Update Jump List", ex);
            }
        });
    }

    private async Task<FileStream> AcquireLeaseAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(
                    settingsPath + ".jumplist.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3))
                {
                    throw new IOException("Another process is updating the Jump List.", ex);
                }

                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }
}
