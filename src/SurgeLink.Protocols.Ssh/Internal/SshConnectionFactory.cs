using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;
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

    /// <summary>Endpoint text used in messages and host key storage.</summary>
    public string Endpoint => string.Concat(_request.Host, ":", Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Keep-alive interval requested for the connection; zero or negative disables keep-alives.</summary>
    public TimeSpan KeepAliveInterval => _request.KeepAliveInterval;

    /// <summary>Creates a verifier bound to the supplied cancellation token.</summary>
    public HostKeyVerifier CreateVerifier(CancellationToken cancellationToken, SshConnectBudget? budget = null) =>
        new(_request.Host, Port, _context.HostKeyStore,
            _context.HostKeyPrompt is { } prompt && budget is not null
                ? async (challenge, token) =>
                {
                    budget.Pause();
                    try { return await prompt(challenge, token).ConfigureAwait(false); }
                    finally { budget.Resume(); }
                }
                : _context.HostKeyPrompt, _logger, cancellationToken);

    internal SshConnectBudget CreateConnectBudget(CancellationToken token) => new(_options.ConnectTimeout, token);

    /// <summary>Creates connection settings with authentication methods and algorithm preferences applied.</summary>
    /// <param name="cancellationToken">Token observed by interactive prompts.</param>
    public ConnectionInfo CreateConnectionInfo(CancellationToken cancellationToken, SshConnectBudget? budget = null)
    {
        var methods = BuildAuthenticationMethods(budget, cancellationToken);
        var info = new ConnectionInfo(_request.Host, Port, _request.Username, methods)
        {
            Timeout = _options.ConnectTimeout,
            Encoding = _options.Encoding,
        };

        ApplyAlgorithmPreferences(info);
        return info;
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

        if (!_options.UseLegacyAlgorithms)
        {
            return;
        }

        _logger.LogWarning(
            "Legacy SSH algorithms are enabled for {Endpoint}; this weakens transport security.",
            Endpoint);
        MoveToFront(info.KeyExchangeAlgorithms, LegacyKeyExchangeAlgorithms);
        MoveToFront(info.HostKeyAlgorithms, LegacyHostKeyAlgorithms);
        MoveToFront(info.HmacAlgorithms, LegacyHmacAlgorithms);
        MoveToFront(info.Encryptions, LegacyEncryptionAlgorithms);
    }

    private static void MoveToFront<TValue>(IOrderedDictionary<string, TValue> algorithms, IReadOnlyList<string> names)
    {
        for (var i = names.Count - 1; i >= 0; i--)
        {
            var name = names[i];
            if (algorithms.ContainsKey(name))
            {
                algorithms.SetPosition(name, 0);
            }
        }
    }
}
