using SurgeLink.Core.Security;

namespace SurgeLink.Core.Tests.Fakes;

/// <summary>In-memory host key store used to build a <see cref="Protocols.ProtocolHostContext"/>.</summary>
internal sealed class FakeHostKeyStore : IHostKeyStore
{
    private readonly Dictionary<string, KnownHostEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry)
    {
        if (_entries.TryGetValue(endpoint, out KnownHostEntry? found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    /// <inheritdoc />
    public void Save(KnownHostEntry entry) => _entries[entry.Endpoint] = entry;

    /// <inheritdoc />
    public bool Remove(string endpoint) => _entries.Remove(endpoint);

    /// <inheritdoc />
    public IReadOnlyList<KnownHostEntry> List() => _entries.Values.ToList();
}
