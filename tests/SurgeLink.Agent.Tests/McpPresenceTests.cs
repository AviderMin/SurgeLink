using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class McpPresenceTests
{
    private static readonly string ConnectionId = new('A', 48);

    [Fact]
    public async Task PresenceIsObservableWithoutGrantingAnySessionAccessOrFloodingAudit()
    {
        var clock = new TestClock();
        var gateway = new AgentGateway(timeProvider: clock);
        var activity = 0;
        gateway.OperationCompleted += (_, _, _) => activity++;
        gateway.McpPeersChanged += () => throw new IOException("observer failed");
        var heartbeat = new AgentRequest("client.heartbeat", ClientId: ConnectionId, ClientName: "Test client");
        Assert.True((await gateway.ExecuteAsync(heartbeat, default)).Success);
        Assert.Equal("Test client", Assert.Single(gateway.McpPeers).Name);
        Assert.True((await gateway.ExecuteAsync(heartbeat, default)).Success);
        Assert.Equal(0, activity);
        Assert.Empty((await gateway.ExecuteAsync(new("sessions.list"), default)).Data!.Value.EnumerateArray());
        Assert.Equal("session_unavailable", (await gateway.ExecuteAsync(new("terminal.screen", "no-grant"), default)).ErrorCode);

        clock.Now += AgentGateway.McpPresenceLifetime;
        Assert.Empty(gateway.McpPeers);
        Assert.True((await gateway.ExecuteAsync(heartbeat, default)).Success);
        Assert.Single(gateway.McpPeers);
        Assert.True((await gateway.ExecuteAsync(new("client.disconnect", ClientId: ConnectionId), default)).Success);
        Assert.Empty(gateway.McpPeers);
    }

    [Theory]
    [InlineData(null, "client", null)]
    [InlineData("wrong", "client", null)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "client\n", null)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "client", "handle")]
    public async Task MalformedPresenceIsRejected(string? id, string name, string? session)
    {
        var gateway = new AgentGateway();
        var result = await gateway.ExecuteAsync(new("client.heartbeat", session, ClientId: id, ClientName: name), default);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Empty(gateway.McpPeers);
    }

    [Fact]
    public async Task PresenceIsBoundedAndExpiredSlotsCanBeReused()
    {
        var clock = new TestClock();
        var gateway = new AgentGateway(timeProvider: clock);
        for (var i = 0; i < 32; i++)
            Assert.True((await gateway.ExecuteAsync(new("client.heartbeat", ClientId: i.ToString("X48"), ClientName: "MCP"), default)).Success);
        var extra = new AgentRequest("client.heartbeat", ClientId: 32.ToString("X48"), ClientName: "MCP");
        Assert.Equal("client_limit", (await gateway.ExecuteAsync(extra, default)).ErrorCode);
        clock.Now += AgentGateway.McpPresenceLifetime;
        Assert.True((await gateway.ExecuteAsync(extra, default)).Success);
        Assert.Single(gateway.McpPeers);
    }

    [Fact]
    public async Task WrongContractVersionCannotRegisterPresence()
    {
        var gateway = new AgentGateway();
        Assert.Equal("version_mismatch", (await gateway.ExecuteAsync(new("client.heartbeat", Version: 99,
            ClientId: ConnectionId, ClientName: "client"), default)).ErrorCode);
        Assert.Empty(gateway.McpPeers);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
