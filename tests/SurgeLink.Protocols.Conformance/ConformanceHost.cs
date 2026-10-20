using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Host services handed to a protocol during conformance checks: a silent logger
/// factory, an in-memory host key store and deterministic prompt stubs. Host key
/// challenges are accepted once (never persisted); interactive prompts answer
/// <see cref="InteractiveAnswer"/> (null by default, which means "no answer").
/// </summary>
public sealed class ConformanceHost
{
    private readonly object _gate = new();
    private readonly List<HostKeyChallenge> _hostKeyChallenges = new();
    private readonly List<InteractivePrompt> _interactivePrompts = new();

    public ConformanceHost()
    {
        HostKeys = new InMemoryHostKeyStore();
        Context = new ProtocolHostContext(
            NullLoggerFactory.Instance,
            HostKeys,
            HandleHostKeyPromptAsync,
            HandleInteractivePromptAsync);
    }

    /// <summary>Host services passed to <see cref="IProtocolPlugin.ConnectAsync"/>.</summary>
    public ProtocolHostContext Context { get; }

    /// <summary>Host key store the protocol sees.</summary>
    public InMemoryHostKeyStore HostKeys { get; }

    /// <summary>When false, host key challenges are rejected instead of trusted once.</summary>
    public bool TrustUnknownHostKeys { get; set; } = true;

    /// <summary>Answer returned for keyboard-interactive prompts; null means no answer.</summary>
    public string? InteractiveAnswer { get; set; }

    /// <summary>Host key challenges raised by the protocol, in order.</summary>
    public IReadOnlyList<HostKeyChallenge> HostKeyChallenges
    {
        get
        {
            lock (_gate)
            {
                return _hostKeyChallenges.ToList();
            }
        }
    }

    /// <summary>Keyboard-interactive prompts raised by the protocol, in order.</summary>
    public IReadOnlyList<InteractivePrompt> InteractivePrompts
    {
        get
        {
            lock (_gate)
            {
                return _interactivePrompts.ToList();
            }
        }
    }

    private Task<HostKeyDecision> HandleHostKeyPromptAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _hostKeyChallenges.Add(challenge);
        }

        return Task.FromResult(TrustUnknownHostKeys ? HostKeyDecision.TrustOnce : HostKeyDecision.Reject);
    }

    private Task<string?> HandleInteractivePromptAsync(InteractivePrompt prompt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _interactivePrompts.Add(prompt);
        }

        return Task.FromResult(InteractiveAnswer);
    }
}
