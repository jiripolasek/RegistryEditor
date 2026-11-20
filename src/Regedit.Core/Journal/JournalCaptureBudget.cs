// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Journal;

internal sealed class JournalCaptureBudget
{
    // Raw bytes and UTF-16 metadata leave room for base64 and escaped JSON within the 256 MiB file limit.
    internal const long DefaultLimit = 64 * 1024 * 1024;
    internal const int KeyBytes = 256;
    internal const int ValueBytes = 128;
    private long _remaining;

    internal long Remaining => this._remaining;

    internal JournalCaptureBudget(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        this._remaining = limit;
    }

    internal static long TextBytes(string? text)
    {
        return (long)(text?.Length ?? 0) * sizeof(char);
    }

    internal void Check(long bytes)
    {
        if (bytes < 0 || bytes > this._remaining)
        {
            throw new IOException("This edit exceeds the journal capture limit. Select a smaller key or fewer values and try again.");
        }
    }

    internal void Charge(long bytes)
    {
        this.Check(bytes);
        this._remaining -= bytes;
    }

    internal void ValueName(string name)
    {
        this.Charge(ValueBytes + TextBytes(name));
    }

    internal static long MinimumStateBytes(JournalState[] states)
    {
        return states.Sum(state =>
            512
            + 2 * TextBytes(state.Scope.Location.SubKey)
            + 2 * TextBytes(state.Scope.Location.Machine)
            + TextBytes(Environment.MachineName)
            + (state.Scope.KeyPaths?.Sum(TextBytes) ?? 0)
            + (state.Scope.ValueNames?.Sum(TextBytes) ?? 0)
            + state.Snapshot.Keys.Sum(key =>
                KeyBytes + TextBytes(key.Path) + key.Values.Sum(value => ValueBytes + TextBytes(value.Name) + value.Data.LongLength))
            + state.Security.Sum(security => 128 + TextBytes(security.Path) + TextBytes(security.Descriptor)));
    }
}

internal sealed class JournalWriteStream : Stream
{
    private readonly Stream _stream;
    private readonly long _limit;
    private long _written;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => this._written;

    public override long Position { get => this._written; set => throw new NotSupportedException(); }

    internal JournalWriteStream(Stream stream, long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        this._stream = stream;
        this._limit = limit;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > this._limit - this._written)
        {
            throw new IOException("This edit exceeds the journal-entry file limit. Select a smaller key or fewer values and try again.");
        }

        this._stream.Write(buffer);
        this._written += buffer.Length;
    }

    public override void Flush()
    {
        this._stream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}
