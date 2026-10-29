using SurgeLink.Core.Storage;

namespace SurgeLink.Core.Security;

/// <summary>File backed <see cref="IHostKeyStore"/>.</summary>
public sealed class JsonHostKeyStore : IHostKeyStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, KnownHostEntry> _entries;

    /// <summary>Creates a store backed by <paramref name="path"/>.</summary>
    public JsonHostKeyStore(string path)
    {
        _path = path;
        // Defer disk errors to verification, where HostKeyVerifier denies trust.
        _entries = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry)
    {
        lock (_gate)
        {
            _entries = Load(_path);
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
            AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = Load(_path, tolerateErrors: false);
                next[entry.Endpoint] = entry;
                Persist(next);
                _entries = next;
                return true;
            });
        }
    }

    /// <inheritdoc />
    public bool Remove(string endpoint)
    {
        lock (_gate)
        {
            return AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = Load(_path, tolerateErrors: false);
                if (!next.Remove(endpoint)) return false;
                Persist(next);
                _entries = next;
                return true;
            });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KnownHostEntry> List()
    {
        lock (_gate)
        {
            _entries = Load(_path);
            return _entries.Values.OrderBy(e => e.Endpoint, StringComparer.Ordinal).ToList();
        }
    }

    private void Persist(Dictionary<string, KnownHostEntry> entries) =>
        AtomicJsonFile.Write(_path, entries.Values.OrderBy(e => e.Endpoint, StringComparer.Ordinal).ToList());

    private static Dictionary<string, KnownHostEntry> Load(string path, bool tolerateErrors = false)
    {
        var list = AtomicJsonFile.Read(path, new List<KnownHostEntry>(), tolerateErrors);
        var map = new Dictionary<string, KnownHostEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Endpoint)
                || string.IsNullOrWhiteSpace(entry.Algorithm)
                || string.IsNullOrWhiteSpace(entry.FingerprintSha256))
                throw new IOException("The known-hosts store contains an invalid entry. Restore or repair it before connecting.");
            if (map.ContainsKey(entry.Endpoint))
                throw new IOException("The known-hosts store contains duplicate endpoints. Restore or repair it before connecting.");
            map[entry.Endpoint] = entry;
        }

        return map;
    }
}
