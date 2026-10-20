using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// A connected fixture plus the host it was connected with. Disposing the
/// wrapper runs <see cref="IProtocolUnderTest.CleanupAsync"/> while the session
/// is still live and then disposes the session.
/// </summary>
public sealed class ConformanceSession : IAsyncDisposable
{
    private bool _disposed;

    private ConformanceSession(IProtocolUnderTest fixture, ConformanceHost host, IProtocolSession session)
    {
        Fixture = fixture;
        Host = host;
        Session = session;
    }

    /// <summary>The fixture that produced the session.</summary>
    public IProtocolUnderTest Fixture { get; }

    /// <summary>The host services used for the connection.</summary>
    public ConformanceHost Host { get; }

    /// <summary>The live session under test.</summary>
    public IProtocolSession Session { get; }

    /// <summary>Connects the fixture with a fresh <see cref="ConformanceHost"/>.</summary>
    public static Task<ConformanceSession> OpenAsync(
        IProtocolUnderTest fixture,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        OpenAsync(fixture, new ConformanceHost(), timeout, cancellationToken);

    /// <summary>Connects the fixture with the supplied host.</summary>
    public static async Task<ConformanceSession> OpenAsync(
        IProtocolUnderTest fixture,
        ConformanceHost host,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(host);
        IProtocolSession session = await ConformanceConnector.ConnectAsync(fixture, host, timeout, cancellationToken);
        return new ConformanceSession(fixture, host, session);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await Fixture.CleanupAsync(Session);
        }
        finally
        {
            await Session.DisposeAsync();
        }
    }
}
