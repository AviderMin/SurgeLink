using System.Runtime.ExceptionServices;
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
    private readonly HashSet<string> _publishing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _deferredCloseWaiters = new(StringComparer.Ordinal);
    private TaskCompletionSource? _disposePublishWaiter;
    private int _activePublishes;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();

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
        CancellationTokenSource connectionLifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using var connection = connectionLifetime;

        var plugin = _catalog.Get(profile.ProtocolId);
        var request = BuildRequest(profile, plugin.Descriptor, options);

        _logger.LogInformation(
            "Connecting to {Host}:{Port} as {User} over {Protocol}",
            request.Host,
            request.Port,
            request.Username,
            plugin.Descriptor.Id);

        var session = await plugin.ConnectAsync(request, _hostContext, connection.Token).ConfigureAwait(false);
        var accepted = false;
        lock (_gate)
        {
            if (!_disposed && !connection.IsCancellationRequested)
            {
                session.StateChanged += OnSessionStateChanged;
                _sessions.Add(session);
                _publishing.Add(session.SessionId);
                _activePublishes++;
                accepted = true;
            }
        }
        if (!accepted)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose rejected session {SessionId}", session.SessionId);
            }
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); }
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        TaskCompletionSource? deferredClose = null;
        try
        {
            Exception? openedFailure = null;
            try
            {
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(profile, session));
            }
            catch (Exception ex)
            {
                openedFailure = ex;
            }

            bool disposedDuringPublish;
            lock (_gate)
            {
                _publishing.Remove(session.SessionId);
                _deferredCloseWaiters.Remove(session.SessionId, out deferredClose);
                disposedDuringPublish = _disposed;
            }

            if (openedFailure is not null)
            {
                try
                {
                    await CloseCoreAsync(session.SessionId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up session {SessionId} after open subscriber failure", session.SessionId);
                }
                ExceptionDispatchInfo.Capture(openedFailure).Throw();
            }

            if (deferredClose is not null)
            {
                await CloseCoreAsync(session.SessionId).ConfigureAwait(false);
            }

            if (disposedDuringPublish)
            {
                await CloseCoreAsync(session.SessionId).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(SessionManager));
            }

            return session;
        }
        finally
        {
            deferredClose?.TrySetResult();
            lock (_gate)
            {
                _publishing.Remove(session.SessionId);
                _activePublishes--;
                if (_activePublishes == 0 && _disposePublishWaiter is not null)
                {
                    _disposePublishWaiter.TrySetResult();
                }
            }
        }
    }

    /// <inheritdoc />
    public Task CloseAsync(string sessionId) => CloseCoreAsync(sessionId);

    private async Task CloseCoreAsync(string sessionId)
    {
        IProtocolSession? session = null;
        Task? deferredWait = null;
        lock (_gate)
        {
            if (_publishing.Contains(sessionId))
            {
                if (!_deferredCloseWaiters.TryGetValue(sessionId, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _deferredCloseWaiters[sessionId] = waiter;
                }
                deferredWait = waiter.Task;
            }
            else
            {
                session = _sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
                if (session is not null)
                {
                    _sessions.Remove(session);
                }
            }
        }

        if (deferredWait is not null)
        {
            await deferredWait.ConfigureAwait(false);
            return;
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
        if (descriptor.AuthenticatesInTerminal)
        {
            return new ConnectionRequest
            {
                Host = profile.Host,
                Port = descriptor.UsesNetworkAddress ? profile.ResolvePort(descriptor.DefaultPort) : 0,
                Username = descriptor.UsesNetworkAddress ? profile.Username : string.Empty,
                Authentication = AuthenticationMethod.KeyboardInteractive,
                ProfileId = profile.Id,
                DisplayName = profile.Name,
                Options = profile.Options,
            };
        }

        // Only touch the credential store when this connection actually needs a
        // stored secret. Keyboard-interactive and local-device profiles must not
        // decrypt unrelated values (DPAPI calls are neither free nor side-effect free).
        var needsStoredPassword = profile.Authentication == AuthenticationMethod.Password && options?.Password is null;
        var needsStoredPassphrase = profile.Authentication == AuthenticationMethod.PublicKey && options?.PrivateKeyPassphrase is null;
        var stored = needsStoredPassword || needsStoredPassphrase ? _credentials.Get(profile.Id) : null;

        var password = options?.Password ?? (needsStoredPassword ? stored?.Password : null);
        var passphrase = options?.PrivateKeyPassphrase ?? (needsStoredPassphrase ? stored?.PrivateKeyPassphrase : null);
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
        IProtocolSession[] snapshot;
        Task? publishWait = null;
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            snapshot = _sessions.Where(s => !_publishing.Contains(s.SessionId)).ToArray();
            foreach (var session in snapshot)
            {
                _sessions.Remove(session);
            }
            if (_activePublishes > 0)
            {
                _disposePublishWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                publishWait = _disposePublishWaiter.Task;
            }
        }
        try { _lifetime.Cancel(); }
        catch (AggregateException exception) { _logger.LogWarning(exception, "A connection cancellation callback failed."); }
        finally { _lifetime.Dispose(); }

        await DisposeSessionsAsync(snapshot).ConfigureAwait(false);

        if (publishWait is not null)
        {
            await publishWait.ConfigureAwait(false);
            IProtocolSession[] remaining;
            lock (_gate)
            {
                remaining = _sessions.ToArray();
                _sessions.Clear();
            }
            await DisposeSessionsAsync(remaining).ConfigureAwait(false);
        }
    }

    private async Task DisposeSessionsAsync(IProtocolSession[] sessions)
    {
        foreach (var session in sessions)
        {
            session.StateChanged -= OnSessionStateChanged;
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
