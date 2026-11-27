using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>
/// Trust on first use host key verification for a single endpoint. Attach
/// <see cref="Handle"/> to <c>BaseClient.HostKeyReceived</c> before connecting.
/// </summary>
internal sealed class HostKeyVerifier
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _endpoint;
    private readonly IHostKeyStore _store;
    private readonly Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? _prompt;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly StrongBox<string?> _sessionFingerprint;
    private readonly object _gate = new();
    private string? _rejectionReason;

    /// <summary>Creates a verifier for one endpoint.</summary>
    public HostKeyVerifier(
        string host,
        int port,
        IHostKeyStore store,
        Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? prompt,
        ILogger logger,
        CancellationToken cancellationToken,
        StrongBox<string?>? sessionFingerprint = null)
    {
        _host = NormalizeHost(host);
        _port = port;
        _endpoint = FormatEndpoint(_host, port);
        _store = store;
        _prompt = prompt;
        _logger = logger;
        _cancellationToken = cancellationToken;
        _sessionFingerprint = sessionFingerprint ?? new();
    }

    /// <summary>Endpoint key used for storage and display.</summary>
    public string Endpoint => _endpoint;

    internal static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            return ip.ToString();
        }

        var withoutTrailingDot = trimmed.TrimEnd('.');
        return (withoutTrailingDot.Length > 0 ? withoutTrailingDot : trimmed).ToLowerInvariant();
    }

    internal static string FormatEndpoint(string? host, int port)
    {
        var normalized = NormalizeHost(host);
        var displayHost = IPAddress.TryParse(normalized, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{normalized}]"
            : normalized;
        return string.Concat(displayHost, ":", port.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Reason the last presented key was refused, or <c>null</c> when it was trusted.</summary>
    public string? RejectionReason
    {
        get
        {
            lock (_gate)
            {
                return _rejectionReason;
            }
        }
    }

    /// <summary>Handles <c>BaseClient.HostKeyReceived</c> by verifying the presented key.</summary>
    /// <param name="sender">Event source.</param>
    /// <param name="args">Host key event arguments whose <c>CanTrust</c> flag is set.</param>
    public void Handle(object? sender, HostKeyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            args.CanTrust = Verify(args);
        }
        catch (Exception exception)
        {
            SetRejection($"Host key verification for {Endpoint} failed: {exception.Message}");
            args.CanTrust = false;
        }
    }

    private bool Verify(HostKeyEventArgs args)
    {
        var algorithm = string.IsNullOrWhiteSpace(args.HostKeyName) ? "unknown" : args.HostKeyName;
        var fingerprint = HostKeyFingerprint.ComputeSha256(args.HostKey);
        var pinned = Volatile.Read(ref _sessionFingerprint.Value);
        if (pinned is not null) { return MatchesSessionKey(pinned, fingerprint); }

        var known = _store.TryGetFingerprint(Endpoint, out var entry);

        if (known && string.Equals(entry.FingerprintSha256, fingerprint, StringComparison.Ordinal))
        {
            _logger.LogDebug("Host key for {Endpoint} matches the remembered fingerprint.", Endpoint);
            return PinSessionKey(fingerprint);
        }

        var prompt = _prompt;
        if (prompt is null)
        {
            SetRejection(known
                ? $"The host key for {Endpoint} changed and no host key prompt is configured."
                : $"The host key for {Endpoint} is not trusted and no host key prompt is configured.");
            return false;
        }

        var challenge = new HostKeyChallenge(_host, _port, algorithm, fingerprint, known);
        var decision = AskUser(prompt, challenge);
        if (!decision.Trusted)
        {
            SetRejection($"The host key for {Endpoint} was rejected.");
            return false;
        }
        if (!PinSessionKey(fingerprint)) { return false; }

        if (decision.Remember)
        {
            try
            {
                _store.Save(new KnownHostEntry
                {
                    Endpoint = Endpoint,
                    Algorithm = algorithm,
                    FingerprintSha256 = fingerprint,
                });
                _logger.LogInformation("Trusted {Algorithm} host key {Fingerprint} for {Endpoint}.", algorithm, fingerprint, Endpoint);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not remember the host key for {Endpoint}.", Endpoint);
            }
        }

        return true;
    }

    private bool PinSessionKey(string fingerprint) =>
        MatchesSessionKey(Interlocked.CompareExchange(ref _sessionFingerprint.Value, fingerprint, null) ?? fingerprint, fingerprint);

    private bool MatchesSessionKey(string pinned, string presented)
    {
        if (string.Equals(pinned, presented, StringComparison.Ordinal)) { return true; }
        SetRejection($"The host key for {Endpoint} changed during the session.");
        return false;
    }

    private HostKeyDecision AskUser(Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>> prompt, HostKeyChallenge challenge)
    {
        var promptTask = Task.Run(() => prompt(challenge, _cancellationToken), CancellationToken.None);
        TaskObservation.ObserveFaults(promptTask);

        try
        {
            return promptTask.WaitAsync(_cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            SetRejection($"Host key verification for {Endpoint} was cancelled.");
            return HostKeyDecision.Reject;
        }
        catch (Exception exception)
        {
            SetRejection($"The host key prompt for {Endpoint} failed: {exception.Message}");
            return HostKeyDecision.Reject;
        }
    }

    private void SetRejection(string reason)
    {
        _logger.LogWarning("{Reason}", reason);
        lock (_gate)
        {
            _rejectionReason ??= reason;
        }
    }
}
