using System.Text.Json;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentAuditLogTests
{
    [Fact]
    public async Task GatewayRecordsNonHeartbeatRequestsAndExportsJsonLines()
    {
        var gateway = new AgentGateway();
        await gateway.ExecuteAsync(new AgentRequest("client.heartbeat", ClientId: "mcp-test"), CancellationToken.None);
        await gateway.ExecuteAsync(new AgentRequest("sessions.list"), CancellationToken.None);
        await gateway.ExecuteAsync(new AgentRequest("unknown.op"), CancellationToken.None);

        var entries = gateway.GetAuditLog();
        Assert.Equal(2, entries.Count);
        Assert.Equal("unknown", entries[0].Method);
        Assert.False(entries[0].Success);
        Assert.Equal("unknown_method", entries[0].OutcomeCode);

        Assert.Equal("sessions.list", entries[1].Method);
        Assert.True(entries[1].Success);
        Assert.Equal("ok", entries[1].OutcomeCode);

        var lines = gateway.ExportAuditLogJsonLines()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("sessions.list", first.RootElement.GetProperty("Method").GetString());
    }

    [Fact]
    public void AuditLogEnforcesCapacityLimit()
    {
        var log = new AgentAuditLog(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            log.Record(new AgentAuditEntry(DateTimeOffset.UtcNow, $"m{i}", null, null, null, true, "ok"));
        }

        var entries = log.GetEntries();
        Assert.Equal(3, entries.Count);
        Assert.Equal("m4", entries[0].Method);
        Assert.Equal("m2", entries[2].Method);
    }

    [Fact]
    public async Task AuditRecordsIncludeSanitizedLocalPathAndDestination()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var session = new AgentGatewayTests.TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, null, allowControl: true, allowFileRead: true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

        await gateway.ExecuteAsync(new("files.rename", handle, "/tmp/src", Destination: "/tmp/dst"), default);
        await gateway.ExecuteAsync(new("files.upload", handle, "/tmp/dst", LocalPath: "relative.txt"), default);

        var entries = gateway.GetAuditLog();
        Assert.Equal(2, entries.Count);
        Assert.Equal("files.upload", entries[0].Method);
        Assert.Equal("relative.txt", entries[0].LocalPath);
        Assert.Null(entries[0].Destination);
        Assert.Equal("files.rename", entries[1].Method);
        Assert.Equal("/tmp/src", entries[1].TargetPath);
        Assert.Equal("/tmp/dst", entries[1].Destination);
        Assert.Null(entries[1].LocalPath);
    }
}
