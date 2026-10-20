using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Conformance;

public sealed class SessionLifecycleConformanceTests
{
    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task ConnectReturnsConnectedSessionWithMatchingCapabilities(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);

        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IProtocolSession session = connection.Session;

        Assert.False(string.IsNullOrWhiteSpace(session.SessionId), "session id must be set");
        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.NotNull(session.Protocol);
        Assert.Equal(fixture.Plugin.Descriptor.Id, session.Protocol.Id);
        ConformanceAssert.AssertCapabilityHandles(session, fixture.Plugin.Descriptor.Capabilities);
        Assert.NotNull(session.HostInfo);
    }

    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task TwoConnectionsGetDistinctSessionIds(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);

        await using ConformanceSession first = await ConformanceSession.OpenAsync(fixture);
        await using ConformanceSession second = await ConformanceSession.OpenAsync(fixture);

        Assert.NotEqual(first.Session.SessionId, second.Session.SessionId);
        Assert.Equal(ConnectionState.Connected, first.Session.State);
        Assert.Equal(ConnectionState.Connected, second.Session.State);
    }

    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task HostInfoLoadsSafelyRepeatedly(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        TimeSpan timeout = ProtocolConformanceSuite.DefaultOperationTimeout;

        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);

        RemoteHostInfo first = await ConformanceAssert.CompletesWithinAsync(
            connection.Session.LoadHostInfoAsync(CancellationToken.None),
            timeout,
            "LoadHostInfoAsync (first call)");
        RemoteHostInfo second = await ConformanceAssert.CompletesWithinAsync(
            connection.Session.LoadHostInfoAsync(CancellationToken.None),
            timeout,
            "LoadHostInfoAsync (second call)");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(string.IsNullOrWhiteSpace(first.Summary), "host info summary must render");
        Assert.False(string.IsNullOrWhiteSpace(second.Summary), "host info summary must render on repeated calls");
    }

    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task DisposeIsIdempotentAndTransitionsToDisconnected(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        TimeSpan timeout = ProtocolConformanceSuite.DefaultOperationTimeout;
        IProtocolSession session = await ConformanceConnector.ConnectAsync(fixture, new ConformanceHost());

        await ConformanceAssert.CompletesWithinAsync(session.DisposeAsync().AsTask(), timeout, "first DisposeAsync");

        ConformanceAssert.AssertDisposed(session);

        Exception? secondDispose = await Record.ExceptionAsync(async () => await session.DisposeAsync());
        Assert.Null(secondDispose);
        ConformanceAssert.AssertDisposed(session);
    }

    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task NoCallbacksArriveAfterDispose(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        TimeSpan timeout = ProtocolConformanceSuite.DefaultOperationTimeout;
        IProtocolSession session = await ConformanceConnector.ConnectAsync(fixture, new ConformanceHost());

        int stateEvents = 0;
        int faultedEvents = 0;
        session.StateChanged += (_, _) => Interlocked.Increment(ref stateEvents);
        session.Faulted += (_, _) => Interlocked.Increment(ref faultedEvents);

        await ConformanceAssert.CompletesWithinAsync(session.DisposeAsync().AsTask(), timeout, "DisposeAsync");

        int stateAfterDispose = Volatile.Read(ref stateEvents);
        int faultedAfterDispose = Volatile.Read(ref faultedEvents);

        await Task.Delay(ProtocolConformanceSuite.LateCallbackObservationWindow);

        Assert.Equal(stateAfterDispose, Volatile.Read(ref stateEvents));
        Assert.Equal(faultedAfterDispose, Volatile.Read(ref faultedEvents));
        Assert.Equal(ConnectionState.Disconnected, session.State);
    }
}
