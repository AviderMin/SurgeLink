using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
            var key = NormalizeEndpoint(endpoint);
            if (key.Length > 0 && _entries.TryGetValue(key, out var found))
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
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.FingerprintSha256);

        var key = NormalizeEndpoint(entry.Endpoint);
        if (key.Length == 0)
        {
            throw new ArgumentException("Endpoint must not be empty.", nameof(entry));
        }

        lock (_gate)
        {
            AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = Load(_path, tolerateErrors: false);
                next[key] = entry;
                Persist(next);
                _entries = next;
                return true;
            });
        }
    }

    /// <inheritdoc />
    public bool Remove(string endpoint)
    {
        var key = NormalizeEndpoint(endpoint);
        if (key.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = Load(_path, tolerateErrors: false);
                if (!next.Remove(key)) return false;
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

    internal static string NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        var trimmed = endpoint.Trim();
        var hostPart = trimmed;
        int? port = null;

        if (trimmed[0] == '[')
        {
            var close = trimmed.IndexOf(']');
            if (close > 1)
            {
                if (close == trimmed.Length - 1)
                {
                    hostPart = trimmed[1..close];
                }
                else if (trimmed[close + 1] == ':'
                    && ushort.TryParse(trimmed[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
                {
                    hostPart = trimmed[1..close];
                    port = parsedPort;
                }
            }
        }
        else
        {
            var lastColon = trimmed.LastIndexOf(':');
            if (lastColon > 0
                && ushort.TryParse(trimmed[(lastColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
            {
                var candidateHost = trimmed[..lastColon];
                if (!candidateHost.Contains(':') || IPAddress.TryParse(candidateHost, out _))
                {
                    hostPart = candidateHost;
                    port = parsedPort;
                }
            }
        }

        hostPart = hostPart.Trim();
        if (hostPart.Length >= 2 && hostPart[0] == '[' && hostPart[^1] == ']')
        {
            hostPart = hostPart[1..^1].Trim();
        }

        string canonicalHost;
        if (IPAddress.TryParse(hostPart, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            canonicalHost = ip.AddressFamily == AddressFamily.InterNetworkV6 && port is not null
                ? $"[{ip}]"
                : ip.ToString();
        }
        else
        {
            var withoutTrailingDot = hostPart.TrimEnd('.');
            canonicalHost = (withoutTrailingDot.Length > 0 ? withoutTrailingDot : hostPart).ToLowerInvariant();
        }

        return port is int p
            ? $"{canonicalHost}:{p.ToString(CultureInfo.InvariantCulture)}"
            : canonicalHost;
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
            var key = NormalizeEndpoint(entry.Endpoint);
            if (key.Length == 0)
                throw new IOException("The known-hosts store contains an invalid entry. Restore or repair it before connecting.");
            if (map.ContainsKey(key))
                throw new IOException("The known-hosts store contains duplicate endpoints. Restore or repair it before connecting.");
            map[key] = entry;
        }

        return map;
    }
}
