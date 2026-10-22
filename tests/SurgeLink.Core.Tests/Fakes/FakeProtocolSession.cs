using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Tests.Fakes;

/// <summary>Scriptable <see cref="IProtocolSession"/> that records disposal and can raise events.</summary>
internal sealed class FakeProtocolSession : IProtocolSession
{
    public FakeProtocolSession(ProtocolDescriptor protocol, string? sessionId = null)
    {
        Protocol = protocol;
        SessionId = sessionId ?? Guid.NewGuid().ToString("n");
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public ProtocolDescriptor Protocol { get; }

    /// <inheritdoc />
    public ConnectionState State { get; private set; } = ConnectionState.Connected;

    /// <inheritdoc />
    public RemoteHostInfo HostInfo { get; } = new() { HostName = "fake-host" };

    /// <inheritdoc />
    public IRemoteTerminal? Terminal { get; set; }

    /// <inheritdoc />
    public IRemoteFileSystem? FileSystem => null;

    /// <inheritdoc />
    public IRemoteSystemMonitor? Monitor => null;

    /// <summary>Number of times <see cref="DisposeAsync"/> ran.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="DisposeAsync"/> throws after counting the call.</summary>
    public Exception? DisposeException { get; set; }

    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    /// <inheritdoc />
    public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken) => Task.FromResult(HostInfo);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        if (DisposeException is not null)
        {
            throw DisposeException;
        }

        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    /// <summary>Raises <see cref="StateChanged"/> to exercise manager subscriptions.</summary>
    public void RaiseStateChanged(ConnectionState oldState, ConnectionState newState, string? message = null) =>
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(oldState, newState, message));

    /// <summary>Raises <see cref="Faulted"/>.</summary>
    public void RaiseFaulted(string message) => Faulted?.Invoke(this, message);
}
