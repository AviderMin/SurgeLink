using System.Text;
using System.Text.Json;
using SurgeLink.Agent;
using SurgeLink.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--help"] or ["-h"] or [])
{
    Console.WriteLine("""
        SurgeLink CLI — observe and control shared desktop terminals

        surgelink-cli sessions list [--instance PID] [--json]
        surgelink-cli sessions info --session HANDLE [--instance PID] [--json]
        surgelink-cli sessions check --session HANDLE [--instance PID] [--json]
        surgelink-cli terminal screen --session HANDLE [--instance PID] [--json]
        surgelink-cli terminal output --session HANDLE [--cursor N] [--instance PID] [--json]
        surgelink-cli terminal send --session HANDLE --text TEXT [--submit] --revision N --operation-id ID --check-token TOKEN [--instance PID] [--json]
        surgelink-cli terminal key --session HANDLE --key Ctrl+C --revision N --operation-id ID --check-token TOKEN [--instance PID] [--json]
        surgelink-cli sessions activate --session HANDLE [--view terminal|files|monitor] [--instance PID] [--json]
        surgelink-cli monitor snapshot --session HANDLE [--instance PID] [--json]
        surgelink-cli files list --session HANDLE --path REMOTE_PATH [--instance PID] [--json]
        surgelink-cli files read --session HANDLE --path REMOTE_PATH [--instance PID] [--json]

        Open the robot button left of the four title-bar buttons, then allow reading or terminal control.
        Terminal input uses the visible PTY. Human typing takes over and revokes access.
        Before EACH new input, run sessions check. Verify server, session ID, login user and current screen.
        Pass its single-use checkToken and screen.inputRevision; checks expire after 2 minutes.
        The login user does not prove the effective shell user after su/nested SSH. Reuse exact arguments for retries.
        input_sent acknowledges input only; it does not report command completion or an exit code.
        Results are always JSON. Exit codes: 0 success, 2 usage, 3 unavailable,
        4 operation declined/failed, 130 cancelled. Execution policy is managed by your Agent application.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
AgentResponse response;
var exitCode = 0;
int? desktop = null;
try
{
    var parsed = CliArguments.Parse(args);
    desktop = AgentEndpoint.ResolveDesktopProcess(parsed.Instance);
    response = await new AgentClient(AgentEndpoint.ForProcess(desktop.Value)).SendAsync(parsed.Request, cancellation.Token);
    if (!response.Success) exitCode = 4;
}
catch (ArgumentException ex) { response = AgentResponse.Error("usage", ex.Message); exitCode = 2; }
catch (AgentDesktopDiscoveryException ex) { response = AgentResponse.Error(ex.ErrorCode, ex.Message); exitCode = 3; }
catch (OperationCanceledException)
{
    response = cancellation.IsCancellationRequested
        ? AgentResponse.Error("cancelled", "The local request was cancelled; remote terminal input may be uncertain. Observe before retrying and preserve the original operation ID.")
        : AgentResponse.Error("desktop_ipc_timeout", "SurgeLink did not respond before the request deadline. Observe uncertain terminal input before retrying with the exact same operation ID and arguments.");
    exitCode = cancellation.IsCancellationRequested ? 130 : 3;
}
catch (TimeoutException)
{
    response = AgentResponse.Error("desktop_ipc_timeout", $"Could not connect to desktop PID {desktop} within 3 seconds. Confirm that desktop is running and responsive, then retry. Do not treat this as permission being disabled; uncertain terminal input needs observation before an exact retry.");
    exitCode = 3;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or JsonException)
{
    response = AgentResponse.Error("desktop_ipc_unavailable", $"Could not communicate with desktop PID {desktop}. Confirm it is running and the CLI uses the same Windows account. No separate CLI switch exists; share sessions in the Agent panel after connecting. Uncertain terminal input must not be resent with a new operation ID.");
    exitCode = 3;
}
Console.WriteLine(JsonSerializer.Serialize(response, AgentJson.Options));
return exitCode;
