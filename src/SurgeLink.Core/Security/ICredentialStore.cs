namespace SurgeLink.Core.Security;

/// <summary>Secret material remembered for a saved profile.</summary>
public sealed record StoredCredential
{
    /// <summary>A stored secret exists but could not be decrypted; it must not be silently replaced.</summary>
    public bool DecryptionFailed { get; init; }

    public string? Password { get; init; }

    public string? PrivateKeyPassphrase { get; init; }
}

/// <summary>Stores secrets encrypted at rest, keyed by session profile id.</summary>
public interface ICredentialStore
{
    /// <summary>Returns the stored credential, or <c>null</c> when nothing is remembered.</summary>
    StoredCredential? Get(string profileId);

    /// <summary>Persists (or replaces) the credential for a profile.</summary>
    void Save(string profileId, StoredCredential credential);

    /// <summary>Removes any stored credential for a profile.</summary>
    void Delete(string profileId);
}
