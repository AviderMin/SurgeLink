using System.Text;

namespace SurgeLink.Core.Files;

/// <summary>Bounds actual bytes read before decoding editable text.</summary>
public static class BoundedTextFile
{
    public static async Task<string> ReadAsync(Stream input, int maximumBytes, CancellationToken cancellationToken)
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
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (text.Contains('\0')) { throw new IOException("Binary files cannot be opened in the text editor."); }
        return text;
    }
}
