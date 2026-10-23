using System.Text;

namespace SurgeLink.Core.Files;

/// <summary>Bounds actual bytes read before decoding editable text.</summary>
public static class BoundedTextFile
{
    public static async Task<string> ReadAsync(Stream input, int maximumBytes, CancellationToken cancellationToken) =>
        (await ReadDocumentAsync(input, maximumBytes, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Reads strict Unicode text while retaining its original encoding and BOM.</summary>
    public static async Task<TextFileDocument> ReadDocumentAsync(Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(81920, maximumBytes)];
        while (true)
        {
            var remaining = (long)maximumBytes - buffer.Length + 1;
            var read = await input.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            if (buffer.Length + read > maximumBytes) { throw new IOException("This file exceeds the editor size limit."); }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = buffer.ToArray();
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
        { encoding = new UTF32Encoding(false, true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
        { encoding = new UTF32Encoding(true, true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        { encoding = new UTF8Encoding(true, true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        if (text.Contains('\0')) { throw new IOException("Binary files cannot be opened in the text editor."); }
        return new TextFileDocument(text, encoding);
    }
}

/// <summary>Editable text and its file encoding, including the original BOM policy.</summary>
public sealed record TextFileDocument(string Text, Encoding Encoding)
{
    /// <summary>Encodes edited text using the original format.</summary>
    public byte[] Encode(string text) => [.. Encoding.GetPreamble(), .. Encoding.GetBytes(text)];
}
