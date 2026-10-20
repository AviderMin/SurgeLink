using Microsoft.Extensions.Logging;

namespace SurgeLink.Core.Protocols;

/// <summary>
/// Services the shell lends to a protocol while it connects. Keeps protocol
/// assemblies free of UI references: everything user facing arrives as a callback.
/// </summary>
public sealed class ProtocolHostContext
{
    /// <summary>Creates a context; typically called once by the composition root.</summary>
    public ProtocolHostContext(
        ILoggerFactory loggerFactory,
        Security.IHostKeyStore hostKeyStore,
        Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? hostKeyPrompt = null,
        Func<InteractivePrompt, CancellationToken, Task<string?>>? interactivePrompt = null)
    {
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        HostKeyStore = hostKeyStore ?? throw new ArgumentNullException(nameof(hostKeyStore));
        HostKeyPrompt = hostKeyPrompt;
        InteractivePrompt = interactivePrompt;
    }

    /// <summary>Logger factory scoped to the protocol implementation.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Store of previously trusted host keys (trust on first use).</summary>
    public Security.IHostKeyStore HostKeyStore { get; }

    /// <summary>Asks the user whether an unknown or changed host key may be trusted.</summary>
    public Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? HostKeyPrompt { get; }

    /// <summary>Asks the user for a keyboard-interactive answer (OTP, 2FA).</summary>
    public Func<InteractivePrompt, CancellationToken, Task<string?>>? InteractivePrompt { get; }

    /// <summary>Creates a logger for a protocol component.</summary>
    public ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
}

/// <summary>Host key presented by a server that is not yet trusted or has changed.</summary>
public sealed record HostKeyChallenge(
    string Host,
    int Port,
    string Algorithm,
    string FingerprintSha256,
    bool IsChanged)
{
    /// <summary>Endpoint key used for storage and display.</summary>
    public string Endpoint => $"{Host}:{Port}";
}

/// <summary>The user's answer to a <see cref="HostKeyChallenge"/>.</summary>
public sealed record HostKeyDecision(bool Trusted, bool Remember)
{
    /// <summary>Reject and abort the connection.</summary>
    public static HostKeyDecision Reject { get; } = new(false, false);

    /// <summary>Trust for this attempt only.</summary>
    public static HostKeyDecision TrustOnce { get; } = new(true, false);

    /// <summary>Trust and persist the fingerprint.</summary>
    public static HostKeyDecision TrustAlways { get; } = new(true, true);
}

/// <summary>A keyboard-interactive prompt raised during authentication.</summary>
public sealed record InteractivePrompt(string Instruction, string Question, bool IsSecret, int Index, int Total);
