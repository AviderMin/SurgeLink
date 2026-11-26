using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>
/// Reads one UTF-8 character-aligned page from a remote file. Arbitrary byte
/// offsets and limits are adjusted to code point boundaries so a page either
/// decodes completely or reports invalid data instead of failing mid-character.
/// </summary>
internal static class AgentFilePage
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Up to three continuation bytes may precede the next character boundary,
    // and the page must be able to hold one complete code point (four bytes).
    private const int BoundarySlack = 8;

    // Leave room for the response envelope and an escaped path of up to 4096 characters.
    private const int MaximumEncodedTextBytes = AgentWire.MaximumFrameBytes - 32 * 1024;

    public static (int Start, int Length) FitWireText(string text, bool keepTail)
    {
        if (Fits(text.AsSpan())) { return (0, text.Length); }

        var lower = 0;
        var upper = text.Length - 1;
        while (lower < upper)
        {
            var middle = lower + (upper - lower + 1) / 2;
            var (start, length) = GetSlice(middle);
            if (Fits(text.AsSpan(start, length))) { lower = middle; }
            else { upper = middle - 1; }
        }
        return GetSlice(lower);

        (int Start, int Length) GetSlice(int length)
        {
            var start = keepTail ? text.Length - length : 0;
            if (keepTail && start < text.Length && char.IsLowSurrogate(text[start]))
            {
                start++;
                length--;
            }
            else if (!keepTail && length > 0 && char.IsHighSurrogate(text[length - 1]))
            {
                length--;
            }
            return (start, length);
        }

        static bool Fits(ReadOnlySpan<char> value) =>
            JsonEncodedText.Encode(value, AgentJson.Options.Encoder).EncodedUtf8Bytes.Length <= MaximumEncodedTextBytes;
    }

    public static async Task<FileTextPage> ReadAsync(Stream stream, long offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (offset > 0)
        {
            if (stream.CanSeek)
            {
                stream.Position = offset;
            }
            else
            {
                await SkipAsync(stream, offset, cancellationToken).ConfigureAwait(false);
            }
        }

        var data = new byte[limit + BoundarySlack];
        var total = 0;
        while (total < data.Length)
        {
            var read = await stream.ReadAsync(data.AsMemory(total, data.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return DecodePage(data, total, offset, limit, reachedEof: total < data.Length, cancellationToken);
    }

    /// <summary>Reads the final bounded UTF-8 slice of a stream without reading the whole file into memory.</summary>
    public static async Task<FileTailPage> ReadTailAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (stream.CanSeek)
        {
            var length = stream.Length;
            var offset = Math.Max(0L, length - limit);
            stream.Position = offset;
            var page = await ReadAsync(stream, offset, limit, cancellationToken).ConfigureAwait(false);
            return new FileTailPage(page.Text, offset, page.BytesRead, page.SkippedBytes, offset > 0);
        }

        var ring = new byte[limit];
        var chunkSize = Math.Min(limit, 81920);
        var chunk = ArrayPool<byte>.Shared.Rent(chunkSize);
        long totalRead = 0;
        var writePos = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var first = Math.Min(read, limit - writePos);
                chunk.AsSpan(0, first).CopyTo(ring.AsSpan(writePos));
                var remaining = read - first;
                if (remaining > 0) chunk.AsSpan(first, remaining).CopyTo(ring);
                writePos = (writePos + read) % limit;
                totalRead += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (totalRead == 0) return new FileTailPage(string.Empty, 0, 0, 0, false);

        var kept = (int)Math.Min(totalRead, limit);
        var tailOffset = totalRead - kept;
        byte[] data;
        if (totalRead <= limit)
        {
            data = ring;
        }
        else
        {
            data = new byte[kept];
            var first = kept - writePos;
            ring.AsSpan(writePos, first).CopyTo(data);
            if (writePos > 0) ring.AsSpan(0, writePos).CopyTo(data.AsSpan(first));
        }

        var decoded = DecodePage(data, kept, tailOffset, limit, reachedEof: true, cancellationToken);
        return new FileTailPage(decoded.Text, tailOffset, decoded.BytesRead, decoded.SkippedBytes, tailOffset > 0);
    }

    private static async Task SkipAsync(Stream stream, long offset, CancellationToken cancellationToken)
    {
        var skip = offset;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (skip > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, skip)), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                skip -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileTextPage DecodePage(byte[] data, int total, long offset, int limit, bool reachedEof, CancellationToken cancellationToken)
    {
        if (total == 0) return new FileTextPage(string.Empty, 0, 0, false);

        // The requested offset may point inside a multi-byte code point. Skip the
        // remaining continuation bytes (at most three for a four-byte character)
        // and report them, so nextOffset stays aligned. A continuation byte at
        // offset zero is invalid data, not an unaligned request.
        var start = 0;
        if (offset > 0)
        {
            while (start < total && start < 3 && (data[start] & 0xC0) == 0x80) start++;
        }
        if (start == total && offset > 0)
        {
            // The offset points into the final character and the file ends there,
            // so there is no complete character left to return. An empty EOF page
            // is more useful than a read failure, and nextOffset reaches the end.
            if (reachedEof) return new FileTextPage(string.Empty, total, total, false);
            throw new DecoderFallbackException("The file offset is not a UTF-8 character boundary.");
        }

        var end = start;
        while (end < total)
        {
            var status = Rune.DecodeFromUtf8(data.AsSpan(end, total - end), out _, out var used);
            if (status == OperationStatus.NeedMoreData) break;
            if (status == OperationStatus.InvalidData)
                throw new DecoderFallbackException("The file contains bytes that are not valid UTF-8.");
            if (end > start && end - start + used > limit) break;
            end += used;
            if (end - start >= limit) break;
        }
        if (end == start)
        {
            // A single code point is wider than the whole buffer (or the file ends
            // in the middle of a character), so this is not valid UTF-8 text.
            throw new DecoderFallbackException("The file does not contain a complete UTF-8 character.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var text = StrictUtf8.GetString(data, start, end - start);
        var truncated = end < total || !reachedEof;
        return new FileTextPage(text, start + (end - start), start, truncated);
    }
}

internal sealed record FileTextPage(string Text, int BytesRead, int SkippedBytes, bool Truncated);

internal sealed record FileTailPage(string Text, long Offset, int BytesRead, int SkippedBytes, bool Truncated);
