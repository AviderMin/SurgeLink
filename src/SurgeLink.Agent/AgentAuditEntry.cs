using System.Text;
using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>
/// Structured audit record for one Agent/MCP/CLI operation.
/// Deliberately omits raw terminal text and file contents to avoid persisting secrets.
/// </summary>
public sealed record AgentAuditEntry(
    DateTimeOffset TimestampUtc,
    string Method,
    string? SessionHandle,
    string? TargetPath,
    string? OperationId,
    bool Success,
    string OutcomeCode,
    string? LocalPath = null,
    string? Destination = null);

/// <summary>Thread-safe bounded ring buffer of structured Agent audit records.</summary>
public sealed class AgentAuditLog
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly LinkedList<AgentAuditEntry> _entries = new();

    public AgentAuditLog(int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public void Record(AgentAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > _capacity)
            {
                _entries.RemoveLast();
            }
        }
    }

    /// <summary>Returns a newest-first snapshot of recorded audit entries.</summary>
    public IReadOnlyList<AgentAuditEntry> GetEntries()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>Serializes all recorded entries as newline-delimited JSON (JSONL), oldest first.</summary>
    public string ExportJsonLines()
    {
        AgentAuditEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.Reverse().ToArray();
        }

        if (snapshot.Length == 0) return string.Empty;
        var sb = new StringBuilder(snapshot.Length * 128);
        foreach (var entry in snapshot)
        {
            sb.AppendLine(JsonSerializer.Serialize(entry));
        }
        return sb.ToString();
    }
}
