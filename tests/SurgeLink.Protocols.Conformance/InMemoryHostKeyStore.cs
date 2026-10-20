using SurgeLink.Core.Security;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// In-memory <see cref="IHostKeyStore"/> used by <see cref="ConformanceHost"/>.
/// Fixtures that need a pre-trusted host key can seed it before connecting.
/// </summary>
public sealed class InMemoryHostKeyStore : IHostKeyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, KnownHostEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(endpoint, out KnownHostEntry? found))
            {
                entry = found;
                return true;
            }

            entry = null!;
            return false;
        }
    }

    /// <inheritdoc />
    public void Save(KnownHostEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.Endpoint] = entry;
        }
    }

    /// <inheritdoc />
    public bool Remove(string endpoint)
    {
        lock (_gate)
        {
            return _entries.Remove(endpoint);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KnownHostEntry> List()
    {
        lock (_gate)
        {
            return _entries.Values.OrderBy(entry => entry.Endpoint, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Forgets every remembered host key.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
