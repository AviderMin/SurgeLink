using SurgeLink.Core.Security;

namespace SurgeLink.Core.Tests.Fakes;

/// <summary>In-memory credential store that counts lookups.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, StoredCredential> _items = new(StringComparer.Ordinal);

    /// <summary>Number of <see cref="Get"/> calls.</summary>
    public int GetCalls { get; private set; }

    /// <summary>Current entries, for assertions.</summary>
    public IReadOnlyDictionary<string, StoredCredential> Items => _items;

    /// <inheritdoc />
    public StoredCredential? Get(string profileId)
    {
        GetCalls++;
        return _items.TryGetValue(profileId, out StoredCredential? credential) ? credential : null;
    }

    /// <inheritdoc />
    public void Save(string profileId, StoredCredential credential) => _items[profileId] = credential;

    /// <inheritdoc />
    public void Delete(string profileId) => _items.Remove(profileId);
}
