using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Core.Tests.Fakes;

/// <summary>Scriptable protocol plugin that records the connection request it received.</summary>
internal sealed class FakeProtocolPlugin : IProtocolPlugin
{
    public FakeProtocolPlugin(
        string id = "fake",
        int defaultPort = 2222,
        SessionCapabilities capabilities = SessionCapabilities.Terminal,
        bool authenticatesInTerminal = false,
        bool usesNetworkAddress = true)
    {
        Descriptor = new ProtocolDescriptor(id, "Fake protocol", defaultPort, capabilities)
        {
            AuthenticatesInTerminal = authenticatesInTerminal,
            UsesNetworkAddress = usesNetworkAddress,
        };
    }

    /// <inheritdoc />
    public ProtocolDescriptor Descriptor { get; }

    /// <summary>Request captured by the last <see cref="ConnectAsync"/> call.</summary>
    public ConnectionRequest? LastRequest { get; private set; }

    /// <summary>Host context captured by the last <see cref="ConnectAsync"/> call.</summary>
    public ProtocolHostContext? LastContext { get; private set; }

    /// <summary>Number of connection attempts.</summary>
    public int ConnectCalls { get; private set; }

    /// <summary>When set, every connection attempt fails with this exception.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Overrides the session returned on success.</summary>
    public Func<ConnectionRequest, IProtocolSession>? SessionFactory { get; set; }

    public Func<ConnectionRequest, CancellationToken, Task<IProtocolSession>>? AsyncSessionFactory { get; set; }

    /// <inheritdoc />
    public Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request,
        ProtocolHostContext context,
        CancellationToken cancellationToken)
    {
        ConnectCalls++;
        LastRequest = request;
        LastContext = context;

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IProtocolSession>(cancellationToken);
        }

        if (Failure is not null)
        {
            return Task.FromException<IProtocolSession>(Failure);
        }

        if (AsyncSessionFactory is { } factory) { return factory(request, cancellationToken); }

        IProtocolSession session = SessionFactory?.Invoke(request) ?? new FakeProtocolSession(Descriptor);
        return Task.FromResult(session);
    }
}
