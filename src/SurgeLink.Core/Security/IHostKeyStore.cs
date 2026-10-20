namespace SurgeLink.Core.Security;

/// <summary>One remembered host key.</summary>
public sealed record KnownHostEntry
{
    public required string Endpoint { get; init; }

    public required string Algorithm { get; init; }

    public required string FingerprintSha256 { get; init; }

    public DateTimeOffset TrustedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Trust-on-first-use host key storage; protects against silent key swaps.</summary>
public interface IHostKeyStore
{
    /// <summary>Looks up the fingerprint remembered for <paramref name="endpoint"/>.</summary>
    bool TryGetFingerprint(string endpoint, out KnownHostEntry entry);

    /// <summary>Remembers (or replaces) the fingerprint for an endpoint.</summary>
    void Save(KnownHostEntry entry);

    /// <summary>Forgets an endpoint.</summary>
    bool Remove(string endpoint);

    /// <summary>Everything remembered so far, for a settings page.</summary>
    IReadOnlyList<KnownHostEntry> List();
}
