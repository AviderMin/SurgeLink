using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>
/// Retains exact terminal-input acknowledgements so a retried operation ID never
/// writes twice. Both the record count and the total serialized payload are
/// bounded: a single command can return hundreds of kilobytes, so a count-only
/// limit would let a long control session accumulate an unbounded amount of data.
/// </summary>
internal sealed class InputReplayCache(int maximumEntries = InputReplayCache.DefaultMaximumEntries,
    long maximumBytes = InputReplayCache.DefaultMaximumBytes)
{
    internal const int DefaultMaximumEntries = 1024;
    internal const long DefaultMaximumBytes = 16L * 1024 * 1024;

    private readonly Dictionary<string, (string Fingerprint, AgentResponse Response, long Bytes, long Generation)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, long Generation)> _order = new();
    private long _bytes;
    private long _generation;

    public int Count => _entries.Count;

    public long EstimatedBytes => _bytes;

    public bool TryGetValue(string operationId, out (string Fingerprint, AgentResponse Response, long Bytes) value)
    {
        if (_entries.TryGetValue(operationId, out var entry))
        {
            value = (entry.Fingerprint, entry.Response, entry.Bytes);
            return true;
        }
        value = default;
        return false;
    }

    public void Remember(string operationId, string fingerprint, AgentResponse response)
    {
        if (_entries.Remove(operationId, out var prior))
        {
            _bytes -= prior.Bytes;
        }

        var size = SizeOf(response);
        var generation = ++_generation;
        _entries[operationId] = (fingerprint, response, size, generation);
        _bytes += size;
        _order.Enqueue((operationId, generation));

        while (_entries.Count > maximumEntries || _bytes > maximumBytes)
        {
            if (!_order.TryDequeue(out var oldest)) break;
            if (_entries.TryGetValue(oldest.Key, out var current) && current.Generation == oldest.Generation)
            {
                _entries.Remove(oldest.Key);
                _bytes -= current.Bytes;
            }
        }
    }

    private static long SizeOf(AgentResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, AgentJson.Options).LongLength;
}
