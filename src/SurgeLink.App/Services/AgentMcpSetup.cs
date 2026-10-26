using System.Globalization;
using System.Text.Json;

namespace SurgeLink.App.Services;

public enum AgentMcpClient { Generic, Codex, ClaudeCode, Cursor }

/// <summary>Reusable user-level registration; no workspace path or transient PID by default.</summary>
public static class AgentMcpSetup
{
    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = true };

    public static string Configuration(string hostPath, int? instance = null) => JsonSerializer.Serialize(new
    {
        mcpServers = new { surgelink = new { command = hostPath, args = Arguments(instance) } },
    }, ConfigJson);

    public static string Registration(AgentMcpClient client, string hostPath, int? instance = null)
    {
        var command = client switch
        {
            AgentMcpClient.Codex => "codex mcp add surgelink -- ",
            AgentMcpClient.ClaudeCode => "claude mcp add --transport stdio --scope user surgelink -- ",
            _ => null,
        };
        if (command is null) return Configuration(hostPath, instance);
        return command + "'" + hostPath.Replace("'", "''", StringComparison.Ordinal) + "'"
            + (instance is int pid ? " --instance " + pid.ToString(CultureInfo.InvariantCulture) : "");
    }

    private static string[] Arguments(int? instance) => instance is int pid
        ? ["--instance", pid.ToString(CultureInfo.InvariantCulture)] : [];
}
