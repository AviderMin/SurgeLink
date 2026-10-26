using System.Text.Json;
using System.Text.Json.Serialization;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Agent;

/// <summary>Versioned operations shared by CLI and MCP adapters.</summary>
public sealed record AgentRequest(string Method, string? Session = null, string? Path = null, int Version = 1,
    string? Text = null, bool Submit = false, string? Key = null, long? Cursor = null,
    long? ExpectedInputRevision = null, string? OperationId = null, string? View = null, string? CheckToken = null,
    string? ClientId = null, string? ClientName = null, int? DesktopProcessId = null);

public sealed record AgentResponse(bool Success, JsonElement? Data = null, string? ErrorCode = null, string? Message = null)
{
    public static AgentResponse Ok<T>(T data) => new(true, JsonSerializer.SerializeToElement(data, AgentJson.Options));
    public static AgentResponse Error(string code, string message) => new(false, ErrorCode: code, Message: message);
}

public sealed record AgentSessionInfo(string Handle, string Label, string Protocol, RemoteHostInfo Host,
    bool Terminal, bool Files, bool Monitor, bool TerminalControl = false, bool IsActive = false,
    AgentSessionTarget? Target = null);

/// <summary>Connection identity captured when desktop access is granted; username is the configured login, not a probed effective shell user.</summary>
public sealed record AgentSessionTarget(string SessionId, string Server, int Port, string Username);

public enum AgentAccessMode { None, Observe, Control }

/// <summary>Advisory MCP presence, not an authenticated client identity or an access grant.</summary>
public sealed record AgentMcpPeer(string Id, string Name, DateTimeOffset LastSeenUtc);

public sealed record AgentFileAccess(string Label, string Operation, string Path);

public sealed record AgentDesktopProcess(int ProcessId, string? ExecutablePath, DateTimeOffset? StartedUtc = null);

public sealed class AgentDesktopDiscoveryException(string errorCode, string message, IReadOnlyList<int> processIds) : IOException(message)
{
    public string ErrorCode { get; } = errorCode;
    public IReadOnlyList<int> ProcessIds { get; } = processIds;
}

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

    public static IReadOnlyList<AgentDesktopProcess> DiscoverDesktopProcesses()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SurgeLink desktop IPC requires Windows.");
        var processes = System.Diagnostics.Process.GetProcessesByName("SurgeLink");
        try
        {
            return processes.Select(process =>
            {
                DateTimeOffset? startedUtc = null;
                try { startedUtc = process.StartTime.ToUniversalTime(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                return new AgentDesktopProcess(process.Id, path, startedUtc);
            }).OrderBy(p => p.ProcessId).ToArray();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static int ResolveDesktopProcess(int? instance = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SurgeLink desktop IPC requires Windows.");
        if (instance is int pid)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
            return pid;
        }
        return SelectDesktopProcess(DiscoverDesktopProcesses().Select(p => p.ProcessId).ToArray());
    }

    /// <summary>Never silently choose between multiple desktop instances.</summary>
    public static int SelectDesktopProcess(IReadOnlyList<int> processIds) => processIds.Count switch
    {
        0 => throw new AgentDesktopDiscoveryException("desktop_not_running", "Start SurgeLink, then retry list_sessions. No desktop process was found.", processIds),
        1 => processIds[0],
        _ => throw new AgentDesktopDiscoveryException("desktop_ambiguous", "Several SurgeLink desktops are running. Call list_desktops, then select_desktop with the intended processId. Do not guess or inspect saved credentials/configuration files.", processIds),
    };
}
