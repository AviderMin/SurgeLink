namespace SurgeLink.Core.Protocols;

/// <summary>
/// A one-shot connection request handed to an <see cref="IProtocolPlugin"/>.
/// Secrets live only in memory and never reach the session store.
/// </summary>
public sealed record ConnectionRequest
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions =
        new Dictionary<string, string>();

    /// <summary>Remote host name/address, or a local device name such as COM3.</summary>
    public required string Host { get; init; }

    /// <summary>Remote TCP port.</summary>
    public int Port { get; init; }

    /// <summary>User name used for authentication.</summary>
    public required string Username { get; init; }

    /// <summary>Authentication mechanism to use.</summary>
    public AuthenticationMethod Authentication { get; init; } = AuthenticationMethod.Password;

    /// <summary>Password, when <see cref="Authentication"/> is <see cref="AuthenticationMethod.Password"/>.</summary>
    public string? Password { get; init; }

    /// <summary>Absolute path to a private key file for public key authentication.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Passphrase protecting <see cref="PrivateKeyPath"/>, when any.</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Overall connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Keep-alive interval; <see cref="TimeSpan.Zero"/> disables keep-alives.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Identifier of the saved profile this request originated from, when any.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Friendly name used in logs and window titles.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Protocol specific extra options; unknown keys must be ignored.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = NoOptions;

    /// <summary>Returns an option value or <paramref name="fallback"/> when absent.</summary>
    public string GetOption(string key, string fallback) =>
        Options.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    /// <summary>Returns <c>true</c> when the option exists and parses as a boolean.</summary>
    public bool GetFlag(string key, bool fallback = false) =>
        Options.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
}
