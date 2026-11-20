using System.Buffers;
using System.Text;

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
                var skip = offset;
                var buffer = new byte[81920];
                while (skip > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, skip)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    skip -= read;
                }
            }
        }

        var capacity = limit + BoundarySlack;
        var data = new byte[capacity];
        var total = 0;
        while (total < data.Length)
        {
            var read = await stream.ReadAsync(data.AsMemory(total, data.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total == 0) return new FileTextPage(string.Empty, 0, 0, false);

        // The requested offset may point inside a multi-byte code point. Skip the
        // remaining continuation bytes and report them, so nextOffset stays aligned.
        var start = 0;
        while (start < total && start < 4 && (data[start] & 0xC0) == 0x80) start++;
        if (start == total) throw new DecoderFallbackException("The file offset is not a UTF-8 character boundary.");

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
        var truncated = end < total || total == data.Length;
        return new FileTextPage(text, start + (end - start), start, truncated);
    }
}

internal sealed record FileTextPage(string Text, int BytesRead, int SkippedBytes, bool Truncated);
