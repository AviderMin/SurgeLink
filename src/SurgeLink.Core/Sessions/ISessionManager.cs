using SurgeLink.Core.Protocols;

namespace SurgeLink.Core.Sessions;

/// <summary>Optional per-connection overrides collected by the UI (for example a typed password).</summary>
public sealed record ConnectOptions
{
    public string? Password { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    public bool PersistPassword { get; init; }
}

/// <summary>Payload of <see cref="ISessionManager.SessionOpened"/>.</summary>
public sealed class SessionOpenedEventArgs : EventArgs
{
    public SessionOpenedEventArgs(SessionProfile profile, IProtocolSession session)
    {
        Profile = profile;
        Session = session;
    }

    public SessionProfile Profile { get; }

    public IProtocolSession Session { get; }
}

/// <summary>Payload of <see cref="ISessionManager.SessionClosed"/>.</summary>
public sealed class SessionClosedEventArgs : EventArgs
{
    public SessionClosedEventArgs(string sessionId) => SessionId = sessionId;

    public string SessionId { get; }
}

/// <summary>
/// Owns the lifetime of live sessions. The UI never talks to a protocol
/// plugin directly, which keeps the shell protocol agnostic.
/// </summary>
public interface ISessionManager
{
    /// <summary>Live sessions in open order.</summary>
    IReadOnlyList<IProtocolSession> Sessions { get; }

    /// <summary>Raised after a session connects successfully.</summary>
    event EventHandler<SessionOpenedEventArgs>? SessionOpened;

    /// <summary>Raised after a session closes or fails.</summary>
    event EventHandler<SessionClosedEventArgs>? SessionClosed;

    /// <summary>Connects using a saved profile plus optional UI overrides.</summary>
    Task<IProtocolSession> OpenAsync(SessionProfile profile, ConnectOptions? options, CancellationToken cancellationToken);

    /// <summary>Closes and disposes a session; safe to call twice.</summary>
    Task CloseAsync(string sessionId);
}
