using System.Buffers.Binary;
using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>Length-prefixed JSON, bounded before allocation. One request/response per connection.</summary>
public static class AgentWire
{
    public const int MaximumFrameBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AgentJson.Options);
        if (bytes.Length > MaximumFrameBytes) throw new AgentFrameTooLargeException();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > MaximumFrameBytes) throw new IOException("Invalid agent frame length.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, AgentJson.Options) ?? throw new JsonException("Empty agent frame.");
    }
}

public sealed class AgentFrameTooLargeException() : IOException("Agent response exceeds the frame limit.");
