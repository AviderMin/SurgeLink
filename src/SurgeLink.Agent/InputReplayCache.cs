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

    private readonly Dictionary<string, (string Fingerprint, AgentResponse Response, long Bytes)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _bytes;

    public int Count => _entries.Count;

    public long EstimatedBytes => _bytes;

    public bool TryGetValue(string operationId, out (string Fingerprint, AgentResponse Response, long Bytes) value) =>
        _entries.TryGetValue(operationId, out value);

    public void Remember(string operationId, string fingerprint, AgentResponse response)
    {
        if (_entries.Remove(operationId, out var prior))
        {
            _bytes -= prior.Bytes;
        }

        var size = SizeOf(response);
        _entries[operationId] = (fingerprint, response, size);
        _bytes += size;
        _order.Enqueue(operationId);

        while (_entries.Count > maximumEntries || _bytes > maximumBytes)
        {
            if (!_order.TryDequeue(out var oldest)) break;
            if (_entries.Remove(oldest, out var evicted))
            {
                _bytes -= evicted.Bytes;
            }
        }
    }

    private static long SizeOf(AgentResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, AgentJson.Options).LongLength;
}
