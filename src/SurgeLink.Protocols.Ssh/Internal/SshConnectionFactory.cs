using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;
using System.Runtime.CompilerServices;
using CoreAuthenticationMethod = SurgeLink.Core.Protocols.AuthenticationMethod;
using SshAuthenticationMethod = Renci.SshNet.AuthenticationMethod;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>
/// Builds <see cref="ConnectionInfo"/> instances and host key verifiers for one
/// connection request. A fresh instance is created for every SSH connection because
/// authentication methods and key files are stateful.
/// </summary>
internal sealed class SshConnectionFactory
{
    private const int MaximumPromptsPerRound = 16;

    private static readonly string[] LegacyKeyExchangeAlgorithms =
    {
        "diffie-hellman-group1-sha1",
        "diffie-hellman-group14-sha1",
        "diffie-hellman-group-exchange-sha1",
    };

    private static readonly string[] LegacyHostKeyAlgorithms = { "ssh-rsa" };

    private static readonly string[] LegacyHmacAlgorithms = { "hmac-sha1", "hmac-sha1-etm@openssh.com" };

    private static readonly string[] LegacyEncryptionAlgorithms = { "aes128-cbc", "aes192-cbc", "aes256-cbc", "3des-cbc" };

    private readonly ConnectionRequest _request;
    private readonly SshProtocolOptions _options;
    private readonly ProtocolHostContext _context;
    private readonly ILogger _logger;
    private readonly StrongBox<string?> _sessionFingerprint = new();
    private string? _dialHostOverride;
    private int? _dialPortOverride;

    /// <summary>Routes target transports through an active local jump-host tunnel while keeping host-key verification on the real target.</summary>
    internal void SetJumpEndpoint(string host, int port)
    {
        _dialHostOverride = host;
        _dialPortOverride = port;
    }

    /// <summary>Creates a factory for one request.</summary>
    public SshConnectionFactory(
        ConnectionRequest request,
        SshProtocolOptions options,
        ProtocolHostContext context,
        ILogger logger)
    {
        _request = request;
        _options = options;
        _context = context;
        _logger = logger;
    }

    /// <summary>Port that will be dialled.</summary>
    public int Port => _request.Port > 0 ? _request.Port : SshProtocolPlugin.DefaultPort;

    /// <summary>Normalized unbracketed host used for dialling and forwarding.</summary>
    public string DialHost => HostKeyVerifier.NormalizeHost(_request.Host);

    /// <summary>Endpoint text used in messages and host key storage.</summary>
    public string Endpoint => HostKeyVerifier.FormatEndpoint(_request.Host, Port);

    internal static void ValidateEndpoint(ConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(HostKeyVerifier.NormalizeHost(request.Host)))
        {
            throw new ProtocolException("SSH host name is required.", ProtocolFailureKind.Configuration);
        }

        if (request.Port < 0 || request.Port > ushort.MaxValue)
        {
            throw new ProtocolException($"SSH port {request.Port} must be between 1 and 65535.", ProtocolFailureKind.Configuration);
        }
    }

    internal static void DisposeAuthenticationMethods(IEnumerable<SshAuthenticationMethod>? methods)
    {
        if (methods is null)
        {
            return;
        }

        foreach (var method in methods)
        {
            if (method is PrivateKeyAuthenticationMethod keyAuth)
            {
                foreach (var key in keyAuth.KeyFiles)
                {
                    if (key is IDisposable disposableKey)
                    {
                        try { disposableKey.Dispose(); } catch { }
                    }
                }
            }

            if (method is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Keep-alive interval requested for the connection; zero or negative disables keep-alives.</summary>
    public TimeSpan KeepAliveInterval => _request.KeepAliveInterval;

    /// <summary>Creates a verifier bound to the supplied cancellation token.</summary>
    public HostKeyVerifier CreateVerifier(CancellationToken cancellationToken, SshConnectBudget? budget = null) =>
        new(DialHost, Port, _context.HostKeyStore,
            _context.HostKeyPrompt is { } prompt && budget is not null
                ? async (challenge, token) =>
                {
                    budget.Pause();
                    try { return await prompt(challenge, token).ConfigureAwait(false); }
                    finally { budget.Resume(); }
                }
                : _context.HostKeyPrompt, _logger, cancellationToken, _sessionFingerprint);

    internal SshConnectBudget CreateConnectBudget(CancellationToken token) => new(_options.ConnectTimeout, token);

    /// <summary>Creates connection settings with authentication methods and algorithm preferences applied.</summary>
    /// <param name="cancellationToken">Token observed by interactive prompts.</param>
    public ConnectionInfo CreateConnectionInfo(
        CancellationToken cancellationToken,
        SshConnectBudget? budget = null,
        string? dialHostOverride = null,
        int? dialPortOverride = null)
    {
        ValidateEndpoint(_request);
        var methods = BuildAuthenticationMethods(budget, cancellationToken);
        try
        {
            var host = HostKeyVerifier.NormalizeHost(
                string.IsNullOrWhiteSpace(dialHostOverride) ? (_dialHostOverride ?? _request.Host) : dialHostOverride);
            var port = dialPortOverride is > 0 ? dialPortOverride.Value : (_dialPortOverride ?? Port);
            var info = new ConnectionInfo(host, port, _request.Username, methods)
            {
                Timeout = _options.ConnectTimeout,
                Encoding = _options.Encoding,
            };

            ApplyAlgorithmPreferences(info);
            if (_context.WebAuthenticationPrompt is { } webPrompt)
                info.AuthenticationBanner += new SshWebAuthentication(Endpoint, webPrompt, budget, cancellationToken).Handle;
            return info;
        }
        catch
        {
            DisposeAuthenticationMethods(methods);
            throw;
        }
    }

    private SshAuthenticationMethod[] BuildAuthenticationMethods(SshConnectBudget? budget, CancellationToken cancellationToken)
    {
        switch (_request.Authentication)
        {
            case CoreAuthenticationMethod.Password:
                var password = _request.Password;
                if (string.IsNullOrEmpty(password))
                {
                    throw new ProtocolException(
                        "Password authentication requires a password.",
                        ProtocolFailureKind.Configuration);
                }

                var passwordMethods = new List<SshAuthenticationMethod>
                {
                    new PasswordAuthenticationMethod(_request.Username, password),
                };
                var fallback = CreateKeyboardInteractiveMethod(password, budget, cancellationToken);
                if (fallback is not null)
                {
                    passwordMethods.Add(fallback);
                }

                return passwordMethods.ToArray();

            case CoreAuthenticationMethod.PublicKey:
                return new SshAuthenticationMethod[] { CreatePublicKeyMethod() };

            case CoreAuthenticationMethod.KeyboardInteractive:
                if (_context.InteractivePrompt is null)
                {
                    throw new ProtocolException(
                        "Keyboard interactive authentication requires an interactive prompt.",
                        ProtocolFailureKind.Configuration);
                }

                return new SshAuthenticationMethod[] { CreateKeyboardInteractiveMethod(null, budget, cancellationToken)! };

            default:
                throw new ProtocolException(
                    $"Authentication method '{_request.Authentication}' is not supported by the SSH protocol.",
                    ProtocolFailureKind.Configuration);
        }
    }

    private SshAuthenticationMethod CreatePublicKeyMethod()
    {
        var keyPath = _request.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ProtocolException(
                "Public key authentication requires a private key file.",
                ProtocolFailureKind.Configuration);
        }

        if (!File.Exists(keyPath))
        {
            throw new ProtocolException(
                $"Private key file not found: '{keyPath}'.",
                ProtocolFailureKind.Configuration);
        }

        PrivateKeyFile keyFile;
        try
        {
            keyFile = string.IsNullOrEmpty(_request.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, _request.PrivateKeyPassphrase);
        }
        catch (Exception exception) when (exception is SshException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ProtocolException(
                $"Could not load private key '{keyPath}': {exception.Message}",
                ProtocolFailureKind.Authentication,
                exception);
        }

        return new PrivateKeyAuthenticationMethod(_request.Username, keyFile);
    }

    private KeyboardInteractiveAuthenticationMethod? CreateKeyboardInteractiveMethod(string? password, SshConnectBudget? budget, CancellationToken cancellationToken)
    {
        var prompt = _context.InteractivePrompt;
        if (prompt is null && string.IsNullOrEmpty(password))
        {
            return null;
        }

        var method = new KeyboardInteractiveAuthenticationMethod(_request.Username);
        method.AuthenticationPrompt += (_, args) =>
        {
            budget?.Pause();
            try { AnswerPrompts(args, prompt, password, cancellationToken); }
            finally { budget?.Resume(); }
        };
        return method;
    }

    internal void AnswerPrompts(
        AuthenticationPromptEventArgs args,
        Func<InteractivePrompt, CancellationToken, Task<string?>>? prompt,
        string? password,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(args.Prompts.Count, MaximumPromptsPerRound);
        var answers = new string?[count];

        for (var i = 0; i < count; i++)
        {
            var item = args.Prompts[i];
            cancellationToken.ThrowIfCancellationRequested();
            if (password is not null && IsPasswordChallenge(item.Request, item.IsEchoed))
            {
                answers[i] = password;
                continue;
            }

            var request = new InteractivePrompt(
                args.Instruction ?? string.Empty,
                item.Request,
                !item.IsEchoed,
                i,
                args.Prompts.Count);
            if (prompt is null)
                throw new ProtocolException("The server requested an additional authentication answer, but no prompt is available.", ProtocolFailureKind.Authentication);
            answers[i] = AskPrompt(prompt, request, cancellationToken);
        }

        for (var i = 0; i < count; i++)
        {
            args.Prompts[i].Response = answers[i] ?? string.Empty;
        }
    }

    internal static bool IsPasswordChallenge(string question, bool isEchoed)
    {
        if (isEchoed) { return false; }
        var text = question.Trim().TrimEnd(':').Trim();
        return text.Equals("password", StringComparison.OrdinalIgnoreCase)
            || text.Equals("密码", StringComparison.Ordinal)
            || text.Equals("密码：", StringComparison.Ordinal)
            || (text.EndsWith("'s password", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("one-time", StringComparison.OrdinalIgnoreCase));
    }

    private string? AskPrompt(
        Func<InteractivePrompt, CancellationToken, Task<string?>> prompt,
        InteractivePrompt request,
        CancellationToken cancellationToken)
    {
        var promptTask = Task.Run(() => prompt(request, cancellationToken), CancellationToken.None);
        TaskObservation.ObserveFaults(promptTask);

        try
        {
            return promptTask.WaitAsync(cancellationToken).GetAwaiter().GetResult()
                ?? throw new ProtocolException("Interactive authentication was cancelled.", ProtocolFailureKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Keyboard interactive prompt {Question} was cancelled.", request.Question);
            throw;
        }
        catch (ProtocolException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Keyboard interactive prompt {Question} failed.", request.Question);
            throw new ProtocolException("The interactive authentication prompt failed.", ProtocolFailureKind.Authentication, exception);
        }
    }

    private void ApplyAlgorithmPreferences(ConnectionInfo info)
    {
        if (_options.UseLegacyAlgorithms)
        {
            _logger.LogWarning(
                "Legacy SSH algorithms are enabled for {Endpoint}; this weakens transport security.",
                Endpoint);
        }
        ApplyLegacyPolicy(info.KeyExchangeAlgorithms, LegacyKeyExchangeAlgorithms, _options.UseLegacyAlgorithms);
        ApplyLegacyPolicy(info.HostKeyAlgorithms, LegacyHostKeyAlgorithms, _options.UseLegacyAlgorithms);
        ApplyLegacyPolicy(info.HmacAlgorithms, LegacyHmacAlgorithms, _options.UseLegacyAlgorithms);
        ApplyLegacyPolicy(info.Encryptions, LegacyEncryptionAlgorithms, _options.UseLegacyAlgorithms);

        var preferred = _options.PreferredKeyExchange;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (info.KeyExchangeAlgorithms.ContainsKey(preferred!))
            {
                info.KeyExchangeAlgorithms.SetPosition(preferred!, 0);
            }
            else
            {
                _logger.LogWarning("Preferred key exchange {Algorithm} is not offered by this SSH client.", preferred);
            }
        }
    }

    private static void ApplyLegacyPolicy<TValue>(IOrderedDictionary<string, TValue> algorithms,
        IReadOnlyList<string> names, bool allowLegacy)
    {
        foreach (var name in names)
        {
            if (!allowLegacy)
            {
                algorithms.Remove(name);
            }
            else if (algorithms.ContainsKey(name))
            {
                algorithms.SetPosition(name, algorithms.Count - 1);
            }
        }
    }
}
