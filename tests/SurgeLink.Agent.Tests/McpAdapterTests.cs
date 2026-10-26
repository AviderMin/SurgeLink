using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SurgeLink.AgentHost;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class McpAdapterTests
{
    [Fact]
    public async Task InvalidToolArgumentsCannotReachGateway()
    {
        var adapter = new McpAdapter((_, _) => throw new InvalidOperationException("Must not forward invalid requests"));
        var missing = await adapter.CallAsync(new CallToolRequestParams { Name = "read_text_file" }, default);
        Assert.True(missing.IsError);
        var extra = await adapter.CallAsync(new CallToolRequestParams
        {
            Name = "list_sessions", Arguments = new Dictionary<string, JsonElement> { ["command"] = JsonSerializer.SerializeToElement("anything") },
        }, default);
        Assert.True(extra.IsError);
        Assert.Equal("invalid_arguments", extra.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task StdioHostDiscoversToolsAndUsesSameDesktopGrantAsCli()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), () => "hello from desktop");
        var instance = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        await using var server = new AgentPipeServer(AgentEndpoint.ForProcess(instance), gateway);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "SurgeLink test",
            Command = "dotnet",
            Arguments = [typeof(McpAdapter).Assembly.Location, "--instance", instance.ToString()],
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(11, tools.Count);
        Assert.Contains(tools, t => t.Name == "check_sessions");
        Assert.Equal("Check Sessions", McpAdapter.Tools.Single(t => t.Name == "check_sessions").Title);
        Assert.Contains(tools, t => t.Name == "read_terminal");
        while (gateway.McpPeers.Count == 0) await Task.Delay(10, timeout.Token);
        Assert.Single(gateway.McpPeers);
        var list = await client.CallToolAsync("list_sessions", cancellationToken: timeout.Token);
        Assert.NotEqual(true, list.IsError);
        Assert.Equal(handle, Assert.Single(list.StructuredContent!.Value.GetProperty("data").EnumerateArray()).GetProperty("handle").GetString());
        var screen = await client.CallToolAsync("read_terminal", new Dictionary<string, object?> { ["session"] = handle }, cancellationToken: timeout.Token);
        Assert.Equal("hello from desktop", screen.StructuredContent!.Value.GetProperty("data").GetProperty("text").GetString());
        await gateway.RevokeAsync("tab");
        var expired = await client.CallToolAsync("read_terminal", new Dictionary<string, object?> { ["session"] = handle }, cancellationToken: timeout.Token);
        Assert.True(expired.IsError);
        Assert.Equal("session_unavailable", expired.StructuredContent!.Value.GetProperty("errorCode").GetString());
        await client.DisposeAsync();
        while (gateway.McpPeers.Count != 0) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task StdioHostWritesThroughPtyCapabilityAndReplaysWithoutDuplicateInput()
    {
        var terminal = new TerminalControlTests.TestAutomation();
        var gateway = new AgentGateway();
        var handle = TerminalControlTests.GrantControl(gateway, terminal);
        var instance = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        await using var desktop = new AgentPipeServer(AgentEndpoint.ForProcess(instance), gateway);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "SurgeLink control test", Command = "dotnet",
            Arguments = [typeof(McpAdapter).Assembly.Location, "--instance", instance.ToString()],
        }), cancellationToken: timeout.Token);
        var check = await client.CallToolAsync("check_sessions", new Dictionary<string, object?> { ["session"] = handle }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, check.IsError);
        var checkToken = check.StructuredContent!.Value.GetProperty("data").GetProperty("checkToken").GetString();
        var arguments = new Dictionary<string, object?>
        {
            ["session"] = handle, ["text"] = "echo test", ["submit"] = true,
            ["expectedInputRevision"] = 0L, ["operationId"] = "mcp-write",
            ["checkToken"] = checkToken,
        };
        var sent = await client.CallToolAsync("send_terminal_text", arguments, cancellationToken: timeout.Token);
        Assert.NotEqual(true, sent.IsError);
        Assert.Equal("input_sent", sent.StructuredContent!.Value.GetProperty("data").GetProperty("status").GetString());
        Assert.NotEqual(true, (await client.CallToolAsync("send_terminal_text", arguments, cancellationToken: timeout.Token)).IsError);
        Assert.Equal(1, terminal.Writes);
        arguments["expectedInputRevision"] = "wrong-type";
        Assert.True((await client.CallToolAsync("send_terminal_text", arguments, cancellationToken: timeout.Token)).IsError);
        Assert.Equal(1, terminal.Writes);
    }
}
