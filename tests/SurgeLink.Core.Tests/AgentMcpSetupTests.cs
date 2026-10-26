using System.Text.Json;
using SurgeLink.App.Services;

namespace SurgeLink.Core.Tests;

public sealed class AgentMcpSetupTests
{
    [Fact]
    public void DefaultConfigurationHasAbsoluteCommandAndNoTransientInstance()
    {
        const string host = @"C:\Program Files\SurgeLink\agent\surgelink-agent-host.exe";
        using var config = JsonDocument.Parse(AgentMcpSetup.Configuration(host));
        var entry = config.RootElement.GetProperty("mcpServers").GetProperty("surgelink");
        Assert.Equal(host, entry.GetProperty("command").GetString());
        Assert.Empty(entry.GetProperty("args").EnumerateArray());
        using var pinned = JsonDocument.Parse(AgentMcpSetup.Configuration(host, 1234));
        Assert.Equal(new[] { "--instance", "1234" }, pinned.RootElement.GetProperty("mcpServers").GetProperty("surgelink")
            .GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());
    }

    [Fact]
    public void RegistrationCommandsKeepHostPathOneArgumentAndUseUserScope()
    {
        const string host = @"C:\Users\O'Brien\Surge Link\agent.exe";
        var codex = AgentMcpSetup.Registration(AgentMcpClient.Codex, host);
        Assert.Equal(@"codex mcp add surgelink -- 'C:\Users\O''Brien\Surge Link\agent.exe'", codex);
        var claude = AgentMcpSetup.Registration(AgentMcpClient.ClaudeCode, host, 1234);
        Assert.Equal(@"claude mcp add --transport stdio --scope user surgelink -- 'C:\Users\O''Brien\Surge Link\agent.exe' --instance 1234", claude);
    }
}
