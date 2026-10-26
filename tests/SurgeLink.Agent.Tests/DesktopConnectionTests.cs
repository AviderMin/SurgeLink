using System.Security.Cryptography;
using SurgeLink.AgentHost;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class DesktopConnectionTests
{
    [Fact]
    public async Task EachCallDiscoversCurrentDesktopAndOldHandlesCannotCrossRestarts()
    {
        var firstPid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var secondPid = firstPid - 1;
        var firstGateway = new AgentGateway();
        var oldHandle = firstGateway.GrantSession("old", "Before restart", new AgentGatewayTests.TestSession(), () => "old");
        var desktop = firstPid;
        var resolves = 0;
        var connection = new DesktopConnection(resolveDesktop: () => { resolves++; return desktop; });
        await using (var server = new AgentPipeServer(AgentEndpoint.ForProcess(firstPid), firstGateway))
        {
            var listed = await connection.SendAsync(new("sessions.list"), default);
            Assert.Equal(oldHandle, Assert.Single(listed.Data!.Value.EnumerateArray()).GetProperty("handle").GetString());
        }

        desktop = secondPid;
        await using var restarted = new AgentPipeServer(AgentEndpoint.ForProcess(secondPid), new AgentGateway());
        Assert.Empty((await connection.SendAsync(new("sessions.list"), default)).Data!.Value.EnumerateArray());
        Assert.Equal("session_unavailable", (await connection.SendAsync(new("terminal.screen", oldHandle), default)).ErrorCode);
        Assert.Equal(3, resolves);
    }

    [Fact]
    public async Task AmbiguousDiscoveryDoesNotContactAnArbitraryDesktop()
    {
        var connection = new DesktopConnection(resolveDesktop: () => AgentEndpoint.SelectDesktopProcess([10, 20]));
        Assert.Equal("desktop_unavailable", (await connection.SendAsync(new("sessions.list"), default)).ErrorCode);
        Assert.Throws<IOException>(() => AgentEndpoint.SelectDesktopProcess([]));
        Assert.Equal(25, AgentEndpoint.SelectDesktopProcess([25]));
    }

    [Fact]
    public async Task PresenceStartsOnlyAfterClientDiscoveryAndIsRemovedOnShutdown()
    {
        var instance = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var gateway = new AgentGateway();
        await using var server = new AgentPipeServer(AgentEndpoint.ForProcess(instance), gateway);
        var connection = new DesktopConnection(instance);
        using var shutdown = new CancellationTokenSource();
        var presence = connection.RunPresenceAsync(shutdown.Token);
        Assert.Empty(gateway.McpPeers);
        connection.ClientConnected("Test MCP");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (gateway.McpPeers.Count == 0) await Task.Delay(10, timeout.Token);
            Assert.Equal("Test MCP", Assert.Single(gateway.McpPeers).Name);
        }
        finally { await shutdown.CancelAsync(); await presence; }
        Assert.Empty(gateway.McpPeers);
    }
}
