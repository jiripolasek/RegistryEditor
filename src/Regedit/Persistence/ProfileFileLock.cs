// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;

namespace JPSoftworks.Regedit.Persistence;

internal static class ProfileFileLock
{
    internal static FileStream Acquire(string path, string busyMessage)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3))
                {
                    throw new IOException(busyMessage, ex);
                }

                Thread.Sleep(25);
            }
        }
    }
}
