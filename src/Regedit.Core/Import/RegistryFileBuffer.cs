// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text;

namespace JPSoftworks.Regedit.Core.Import;

/// <summary>Retains source text and encoding; preview parsing never rewrites the document.</summary>
public sealed class RegistryFileBuffer
{
    public const int MaximumBytes = 10 * 1024 * 1024;

    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private byte[] _savedBytes;
    private string _savedText;

    public string FilePath { get; private set; }

    public string Text { get; set; }

    public bool IsDirty => this.Text != this._savedText;

    public string EncodingName => this._encoding.EncodingName;

    private RegistryFileBuffer(string path, byte[] bytes, Encoding encoding, byte[] preamble, string text)
    {
        this.FilePath = path;
        this._savedBytes = bytes;
        this._encoding = encoding;
        this._preamble = preamble;
        this.Text = this._savedText = text;
    }

    public static async Task<RegistryFileBuffer> OpenAsync(string path)
    {
        var bytes = await ReadAsync(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, true, true);
            skip = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, true, true);
            skip = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            encoding = new UTF8Encoding(true, true);
            skip = 3;
        }
        else
        {
            encoding = Encoding.GetEncoding(0, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0'))
        {
            throw new IOException(
                "The file contains null characters or an unsupported encoding. Use UTF-16 with a byte order mark, UTF-8 with a byte order mark, or Windows ANSI.");
        }

        return new(Path.GetFullPath(path), bytes, encoding, bytes[..skip], text);
    }

    public async Task SaveAsync(string path)
    {
        path = Path.GetFullPath(path);
        var text = this.Text;
        var bytes = this.Encode(text);
        var samePath = StringComparer.OrdinalIgnoreCase.Equals(path, this.FilePath);
        if (samePath)
        {
            const string changed
                = "The file changed outside Registry Editor. Reload it or use Save as to keep your changes.";
            byte[] currentBytes;
            try
            {
                currentBytes = await ReadAsync(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new IOException(changed, ex);
            }

            if (!this._savedBytes.AsSpan().SequenceEqual(currentBytes))
            {
                throw new IOException(changed);
            }
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        this.FilePath = path;
        this._savedBytes = bytes;
        this._savedText = text;
    }

    public byte[] ImportBytes()
    {
        return this.ImportBytes(out _);
    }

    public byte[] ImportBytes(out RegistryFileMetadata metadata)
    {
        var text = this.Text;
        var document = RegistryFileDocument.Parse(text);
        metadata = document.Metadata;
        if (!document.CanImport)
        {
            throw new InvalidOperationException("Resolve the file errors before importing.");
        }

        // Windows imports version 5 text as UTF-16; REGEDIT4 retains its ANSI hexadecimal string payloads.
        if (!document.IsLegacy)
        {
            return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(0, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            .GetBytes(text);
    }

    private byte[] Encode(string text)
    {
        var bytes = this._encoding.GetBytes(text);
        if (bytes.Length + this._preamble.Length > MaximumBytes)
        {
            throw new IOException("Registry file preview supports files up to 10 MiB.");
        }

        return [.. this._preamble, .. bytes];
    }

    private static async Task<byte[]> ReadAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes)
        {
            throw new IOException("Registry file preview supports files up to 10 MiB.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }
}
