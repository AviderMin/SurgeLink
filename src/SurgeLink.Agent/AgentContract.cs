using System.Text.Json;
using System.Text.Json.Serialization;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Agent;

/// <summary>Versioned operations shared by CLI and MCP adapters.</summary>
public sealed record AgentRequest(string Method, string? Session = null, string? Path = null, int Version = 1,
    string? Text = null, bool Submit = false, string? Key = null, long? Cursor = null,
    long? ExpectedInputRevision = null, string? OperationId = null, string? View = null);

public sealed record AgentResponse(bool Success, JsonElement? Data = null, string? ErrorCode = null, string? Message = null)
{
    public static AgentResponse Ok<T>(T data) => new(true, JsonSerializer.SerializeToElement(data, AgentJson.Options));
    public static AgentResponse Error(string code, string message) => new(false, ErrorCode: code, Message: message);
}

public sealed record AgentSessionInfo(string Handle, string Label, string Protocol, RemoteHostInfo Host,
    bool Terminal, bool Files, bool Monitor, bool TerminalControl = false, bool IsActive = false);

public enum AgentAccessMode { None, Observe, Control }

public sealed record AgentFileAccess(string Label, string Operation, string Path);

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32,
    };
}

public static class AgentEndpoint
{
    // PID separates simultaneously running desktops. Pipe ACLs enforce the OS user boundary.
    public static string ForProcess(int processId) => $"SurgeLink.Agent.v1.{processId}";

    public static int ResolveDesktopProcess(int? instance = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SurgeLink desktop IPC requires Windows.");
        if (instance is int pid)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
            return pid;
        }
        var processes = System.Diagnostics.Process.GetProcessesByName("SurgeLink");
        try
        {
            if (processes.Length != 1)
                throw new IOException("Start SurgeLink first; when multiple desktops are running, specify --instance PID.");
            return processes[0].Id;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
