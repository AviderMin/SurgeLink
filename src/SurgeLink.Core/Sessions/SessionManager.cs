using Microsoft.Extensions.Logging;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;

namespace SurgeLink.Core.Sessions;

/// <summary>
/// Owns connection lifetime: builds a <see cref="ConnectionRequest"/> from a
/// profile, resolves secrets, delegates to the protocol plugin and tracks state.
/// </summary>
public sealed class SessionManager : ISessionManager, IAsyncDisposable
{
    private readonly IProtocolCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly ProtocolHostContext _hostContext;
    private readonly ILogger<SessionManager> _logger;
    private readonly object _gate = new();
    private readonly List<IProtocolSession> _sessions = new();
    private bool _disposed;

    /// <summary>Creates a manager over a protocol catalog and credential store.</summary>
    public SessionManager(
        IProtocolCatalog catalog,
        ICredentialStore credentials,
        ProtocolHostContext hostContext,
        ILogger<SessionManager> logger)
    {
        _catalog = catalog;
        _credentials = credentials;
        _hostContext = hostContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<IProtocolSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToList();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<SessionOpenedEventArgs>? SessionOpened;

    /// <inheritdoc />
    public event EventHandler<SessionClosedEventArgs>? SessionClosed;

    /// <inheritdoc />
    public async Task<IProtocolSession> OpenAsync(SessionProfile profile, ConnectOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var plugin = _catalog.Get(profile.ProtocolId);
        var request = BuildRequest(profile, plugin.Descriptor, options);

        _logger.LogInformation(
            "Connecting to {Host}:{Port} as {User} over {Protocol}",
            request.Host,
            request.Port,
            request.Username,
            plugin.Descriptor.Id);

        var session = await plugin.ConnectAsync(request, _hostContext, cancellationToken).ConfigureAwait(false);
        session.StateChanged += OnSessionStateChanged;

        lock (_gate)
        {
            _sessions.Add(session);
        }

        SessionOpened?.Invoke(this, new SessionOpenedEventArgs(profile, session));
        return session;
    }

    /// <inheritdoc />
    public Task CloseAsync(string sessionId) => CloseCoreAsync(sessionId);

    private async Task CloseCoreAsync(string sessionId)
    {
        IProtocolSession? session;
        lock (_gate)
        {
            session = _sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
            if (session is not null)
            {
                _sessions.Remove(session);
            }
        }

        if (session is null)
        {
            return;
        }

        session.StateChanged -= OnSessionStateChanged;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose session {SessionId}", sessionId);
        }

        SessionClosed?.Invoke(this, new SessionClosedEventArgs(sessionId));
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (sender is not IProtocolSession session)
        {
            return;
        }

        if (e.NewState is ConnectionState.Disconnected or ConnectionState.Faulted)
        {
            _logger.LogInformation(
                "Session {SessionId} moved to {State}: {Message}",
                session.SessionId,
                e.NewState,
                e.Message ?? string.Empty);
        }
    }

    private ConnectionRequest BuildRequest(SessionProfile profile, ProtocolDescriptor descriptor, ConnectOptions? options)
    {
        var stored = options?.Password is null || options?.PrivateKeyPassphrase is null
            ? _credentials.Get(profile.Id)
            : null;

        var password = options?.Password ?? (profile.Authentication == AuthenticationMethod.Password ? stored?.Password : null);
        var passphrase = options?.PrivateKeyPassphrase ??
                         (profile.Authentication == AuthenticationMethod.PublicKey ? stored?.PrivateKeyPassphrase : null);
        var keyPath = options?.PrivateKeyPath ?? profile.PrivateKeyPath;

        if (profile.Authentication == AuthenticationMethod.PublicKey && string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ProtocolException(
                $"Session '{profile.Name}' is configured for public key authentication but has no key file.",
                ProtocolFailureKind.Configuration);
        }

        return new ConnectionRequest
        {
            Host = profile.Host,
            Port = profile.ResolvePort(descriptor.DefaultPort),
            Username = profile.Username,
            Authentication = profile.Authentication,
            Password = password,
            PrivateKeyPath = keyPath,
            PrivateKeyPassphrase = passphrase,
            ProfileId = profile.Id,
            DisplayName = profile.Name,
            Options = profile.Options,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IProtocolSession[] snapshot;
        lock (_gate)
        {
            snapshot = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (var session in snapshot)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose session {SessionId}", session.SessionId);
            }
        }
    }
}
