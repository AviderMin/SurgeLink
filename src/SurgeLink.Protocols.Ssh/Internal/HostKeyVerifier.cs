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
    private readonly IHostKeyStore _store;
    private readonly Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? _prompt;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly object _gate = new();
    private string? _rejectionReason;

    /// <summary>Creates a verifier for one endpoint.</summary>
    public HostKeyVerifier(
        string host,
        int port,
        IHostKeyStore store,
        Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? prompt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _host = host;
        _port = port;
        _store = store;
        _prompt = prompt;
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Endpoint key used for storage and display.</summary>
    public string Endpoint => string.Concat(_host, ":", _port.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
        var known = _store.TryGetFingerprint(Endpoint, out var entry);

        if (known && string.Equals(entry.FingerprintSha256, fingerprint, StringComparison.Ordinal))
        {
            _logger.LogDebug("Host key for {Endpoint} matches the remembered fingerprint.", Endpoint);
            return true;
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