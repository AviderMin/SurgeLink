using SurgeLink.Core.Storage;

namespace SurgeLink.Core.Security;

/// <summary>File backed <see cref="IHostKeyStore"/>.</summary>
public sealed class JsonHostKeyStore : IHostKeyStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, KnownHostEntry> _entries;

    /// <summary>Creates a store backed by <paramref name="path"/>.</summary>
    public JsonHostKeyStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    /// <inheritdoc />
    public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(endpoint, out var found))
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
            Persist();
        }
    }

    /// <inheritdoc />
    public bool Remove(string endpoint)
    {
        lock (_gate)
        {
            if (!_entries.Remove(endpoint))
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KnownHostEntry> List()
    {
        lock (_gate)
        {
            return _entries.Values.OrderBy(e => e.Endpoint, StringComparer.Ordinal).ToList();
        }
    }

    private void Persist() =>
        AtomicJsonFile.Write(_path, _entries.Values.OrderBy(e => e.Endpoint, StringComparer.Ordinal).ToList());

    private static Dictionary<string, KnownHostEntry> Load(string path)
    {
        var list = AtomicJsonFile.Read(path, new List<KnownHostEntry>());
        var map = new Dictionary<string, KnownHostEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            map[entry.Endpoint] = entry;
        }

        return map;
    }
}
