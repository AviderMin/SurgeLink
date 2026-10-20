using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using Xunit.Sdk;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Connects a fixture with a bounded timeout so a hung protocol fails the test
/// instead of hanging the run.
/// </summary>
public static class ConformanceConnector
{
    /// <summary>Connects using the fixture's default request.</summary>
    public static Task<IProtocolSession> ConnectAsync(
        IProtocolUnderTest fixture,
        ConformanceHost host,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(host);
        return ConnectAsync(
            fixture.Plugin,
            fixture.CreateConnectionRequest(),
            host.Context,
            timeout ?? ProtocolConformanceSuite.DefaultOperationTimeout,
            cancellationToken);
    }

    /// <summary>Connects a specific plugin/request/context triple.</summary>
    public static async Task<IProtocolSession> ConnectAsync(
        IProtocolPlugin plugin,
        ConnectionRequest request,
        ProtocolHostContext hostContext,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostContext);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await plugin.ConnectAsync(request, hostContext, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XunitException(
                $"Protocol '{plugin.Descriptor.Id}' did not connect within {timeout.TotalMilliseconds:0} ms (request host '{request.Host}').");
        }
    }
}
