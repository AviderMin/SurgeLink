using SurgeLink.Core.Protocols;

namespace SurgeLink.Core.Sessions;

/// <summary>
/// A saved connection definition. Contains no secrets: passwords live in
/// <see cref="Security.ICredentialStore"/> and private keys stay on disk.
/// </summary>
public sealed record SessionProfile
{
    /// <summary>Stable identifier used by the credential store.</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the sidebar.</summary>
    public required string Name { get; init; }

    /// <summary>Protocol plugin id, for example <c>ssh</c>.</summary>
    public required string ProtocolId { get; init; }

    /// <summary>Remote host name or address.</summary>
    public required string Host { get; init; }

    /// <summary>Remote port; 0 means "use the protocol default".</summary>
    public int Port { get; init; }

    /// <summary>User name.</summary>
    public required string Username { get; init; }

    /// <summary>Display address; protocols with terminal login may omit the user name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserHost => string.IsNullOrWhiteSpace(Username) ? Host : $"{Username}@{Host}";

    /// <summary>Preferred authentication mechanism.</summary>
    public AuthenticationMethod Authentication { get; init; } = AuthenticationMethod.Password;

    /// <summary>Private key file path for public key authentication.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Optional folder/group used to organise the sidebar.</summary>
    public string? Group { get; init; }

    /// <summary>Accent color tag rendered next to the name.</summary>
    public string? ColorTag { get; init; }

    /// <summary>Last OS description discovered after connecting; used for the saved-session icon.</summary>
    public string? DetectedOperatingSystem { get; init; }

    /// <summary>When the profile was last used, used for "recent" ordering.</summary>
    public DateTimeOffset? LastUsedUtc { get; init; }

    /// <summary>Protocol specific options (encoding, terminal type, jump host…).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Creates a profile with a fresh identifier.</summary>
    public static SessionProfile New(string protocolId, string host, int port, string username, string? name = null) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Name = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(username) ? host : $"{username}@{host}") : name!,
        ProtocolId = protocolId,
        Host = host,
        Port = port,
        Username = username,
    };

    /// <summary>Returns a copy marked as used now.</summary>
    public SessionProfile MarkUsed(DateTimeOffset now) => this with { LastUsedUtc = now };

    /// <summary>Port to dial, falling back to <paramref name="defaultPort"/>.</summary>
    public int ResolvePort(int defaultPort) => Port > 0 ? Port : defaultPort;
}
