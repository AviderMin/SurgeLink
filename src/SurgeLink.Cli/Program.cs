using System.Text;
using System.Text.Json;
using SurgeLink.Agent;
using SurgeLink.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--help"] or ["-h"] or [])
{
    Console.WriteLine("""
        SurgeLink CLI — read existing, explicitly shared desktop sessions

        surgelink-cli sessions list [--instance PID] [--json]
        surgelink-cli sessions info --session HANDLE [--instance PID] [--json]
        surgelink-cli terminal screen --session HANDLE [--instance PID] [--json]
        surgelink-cli monitor snapshot --session HANDLE [--instance PID] [--json]
        surgelink-cli files list --session HANDLE --path REMOTE_PATH [--instance PID] [--json]
        surgelink-cli files read --session HANDLE --path REMOTE_PATH [--instance PID] [--json]

        Open SurgeLink and click 'Allow CLI access' in the session header first.
        File requests require an additional confirmation in the desktop (60s timeout).
        Results are always JSON. Exit codes: 0 success, 2 usage, 3 unavailable,
        4 operation declined/failed, 130 cancelled. No command execution or writes.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
AgentResponse response;
var exitCode = 0;
try
{
    var parsed = CliArguments.Parse(args);
    var instance = AgentEndpoint.ResolveDesktopProcess(parsed.Instance);
    response = await new AgentClient(AgentEndpoint.ForProcess(instance)).SendAsync(parsed.Request, cancellation.Token);
    if (!response.Success) exitCode = 4;
}
catch (ArgumentException ex) { response = AgentResponse.Error("usage", ex.Message); exitCode = 2; }
catch (OperationCanceledException)
{
    response = AgentResponse.Error(cancellation.IsCancellationRequested ? "cancelled" : "timeout", "The request was cancelled or expired.");
    exitCode = cancellation.IsCancellationRequested ? 130 : 3;
}
catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or PlatformNotSupportedException or JsonException)
{
    response = AgentResponse.Error("desktop_unavailable", "Cannot reach SurgeLink. Enable CLI access in the desktop and check --instance PID.");
    exitCode = 3;
}
Console.WriteLine(JsonSerializer.Serialize(response, AgentJson.Options));
return exitCode;
