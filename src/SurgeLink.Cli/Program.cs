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
        surgelink-cli sessions check --session HANDLE [--brief|--minimal] [--instance PID] [--json]
        surgelink-cli terminal screen --session HANDLE [--tail-rows N] [--strip-ansi] [--instance PID] [--json]
        surgelink-cli terminal output --session HANDLE [--cursor N] [--max-bytes N] [--max-lines N] [--strip-ansi] [--instance PID] [--json]
        surgelink-cli terminal wait --session HANDLE [--cursor N] [--idle-ms 500] [--timeout-ms 30000] [--max-bytes N] [--max-lines N] [--strip-ansi] [--instance PID] [--json]
        surgelink-cli terminal send --session HANDLE (--text TEXT | --text-file FILE | --text-stdin) [--submit] --revision N --operation-id ID --check-token TOKEN [--instance PID] [--json]
        surgelink-cli terminal exec --session HANDLE (--text TEXT | --text-file FILE | --text-stdin) --revision N --operation-id ID --check-token TOKEN [--timeout-ms 30000] [--max-bytes N] [--max-lines N] [--strip-ansi] [--instance PID] [--json]
        surgelink-cli terminal key --session HANDLE --key Ctrl+C --revision N --operation-id ID --check-token TOKEN [--instance PID] [--json]
        surgelink-cli sessions activate --session HANDLE [--view terminal|files|monitor] [--instance PID] [--json]
        surgelink-cli monitor snapshot --session HANDLE [--instance PID] [--json]
        surgelink-cli files list --session HANDLE --path REMOTE_PATH [--offset N] [--limit N] [--instance PID] [--json]
        surgelink-cli files read --session HANDLE --path REMOTE_PATH ([--offset N] [--max-bytes N] | [--tail-bytes N]) [--instance PID] [--json]
        surgelink-cli files stat --session HANDLE --path REMOTE_PATH [--instance PID] [--json]
        surgelink-cli files write --session HANDLE --path REMOTE_PATH (--text TEXT | --text-file FILE | --text-stdin) [--overwrite] [--instance PID] [--json]
        surgelink-cli files delete --session HANDLE --path REMOTE_PATH [--recursive] [--instance PID] [--json]
        surgelink-cli files rename --session HANDLE --path REMOTE_PATH --destination NEW_PATH [--instance PID] [--json]
        surgelink-cli files mkdir --session HANDLE --path REMOTE_PATH [--instance PID] [--json]
        surgelink-cli files upload --session HANDLE --path REMOTE_PATH --local-path LOCAL_FILE [--overwrite] [--instance PID] [--json]
        surgelink-cli files download --session HANDLE --path REMOTE_PATH --local-path LOCAL_FILE [--overwrite] [--instance PID] [--json]
          For downloads, --overwrite allows a numbered copy if the local name is taken. Check the returned localPath.
        surgelink-cli desktops list [--json]
        surgelink-cli doctor [--json]

        Open the robot button left of the four title-bar buttons, then allow reading or terminal control.
        Terminal input uses the visible PTY. Human typing pauses the Agent and returns user_operating:
        stop all operations, do not retry or re-acquire the session, and wait for the user to resume control.
        Stop in the desktop panel revokes access.
        Before EACH new input, run sessions check. Verify server, session ID, login user and current screen.
        Pass its single-use checkToken and screen.inputRevision; checks expire after 2 minutes.
        The login user does not prove the effective shell user after su/nested SSH. Reuse exact arguments for retries.
        input_sent acknowledges input only; it does not report command completion or an exit code.
        terminal wait returns when output has been quiet for --idle-ms, which is an observation, not a command boundary.
        terminal exec appends a POSIX shell sentinel and waits up to --timeout-ms (max 50000), returning exitCode when observed or status=timed_out with screen tail and output.cursor to continue reading.
        terminal exec rejects wrapped lines over 4000 UTF-8 bytes before writing. If output capture stops before completion, status=output_incomplete leaves the outcome unknown.
        SURGELINK_INSTANCE can supply the desktop PID when --instance is omitted.
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
    if (args.Length >= 2 && args[0] == "desktops" && args[1] == "list")
    {
        if (args.Length > 3 || args.Length == 3 && args[2] != "--json")
            throw new ArgumentException("Usage: surgelink-cli desktops list [--json]");
        var desktops = AgentEndpoint.DiscoverDesktopProcesses();
        Console.WriteLine(JsonSerializer.Serialize(AgentResponse.Ok(new
        {
            desktops,
            instruction = "Pass the intended processId with --instance when several SurgeLink desktops are running. Listing never grants session access.",
        }), AgentJson.Options));
        return 0;
    }
    if (args is ["doctor"] or ["doctor", "--json"])
    {
        var host = FindMcpHost();
        Console.WriteLine(JsonSerializer.Serialize(AgentResponse.Ok(new
        {
            cliPath = Environment.ProcessPath,
            mcpHostPath = host,
            mcpHostExists = host is not null && File.Exists(host),
            environmentInstance = ParseEnvironmentInstance(throwOnInvalid: false),
            desktops = AgentEndpoint.DiscoverDesktopProcesses(),
            instruction = "The desktop must be running and the CLI must use the same Windows account. If mcpHostExists is false, rebuild or reinstall SurgeLink.AgentHost.",
        }), AgentJson.Options));
        return 0;
    }

    var parsed = CliArguments.Parse(args);
    var request = parsed.Request;
    const int maxTextInputBytes = 1024 * 1024;
    if (parsed.TextFile is { } textFile)
    {
        request = request with { Text = await ReadBoundedTextFileAsync(textFile, maxTextInputBytes, cancellation.Token) };
    }
    if (parsed.TextFromStdin)
    {
        request = request with { Text = await ReadBoundedStdinAsync(maxTextInputBytes, cancellation.Token) };
    }
    if (request.Method == "files.write" && request.Text is { } writeText && System.Text.Encoding.UTF8.GetByteCount(writeText) > 900_000)
        throw new ArgumentException("files.write text is limited to 900000 UTF-8 bytes per request; split the file into smaller writes.");
    var instance = parsed.Instance ?? ParseEnvironmentInstance(throwOnInvalid: true);
    desktop = AgentEndpoint.ResolveDesktopProcess(instance);
    response = await AgentProcessLifetime.RunAsync(desktop.Value,
        ct => new AgentClient(AgentEndpoint.ForProcess(desktop.Value)).SendAsync(request, ct), cancellation.Token);
    if (!response.Success) exitCode = 4;
}
catch (ArgumentException ex) { response = AgentResponse.Error("usage", ex.Message); exitCode = 2; }
catch (AgentDesktopDiscoveryException ex) { response = AgentResponse.Error(ex.ErrorCode, ex.Message); exitCode = 3; }
catch (AgentFrameTooLargeException ex) { response = AgentResponse.Error("request_too_large", ex.Message); exitCode = 2; }
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
catch (UnauthorizedAccessException)
{
    response = AgentResponse.Error("desktop_ipc_unavailable", $"Access to desktop PID {desktop}'s named pipe was denied. Confirm the CLI uses the same Windows account and is not blocked by a restricted process sandbox (or connect through the unsandboxed MCP Host instead). Uncertain terminal input must not be resent with a new operation ID.");
    exitCode = 3;
}
catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or JsonException)
{
    response = AgentResponse.Error("desktop_ipc_unavailable", $"Could not communicate with desktop PID {desktop}. Confirm it is running and the CLI uses the same Windows account. No separate CLI switch exists; share sessions in the Agent panel after connecting. Uncertain terminal input must not be resent with a new operation ID.");
    exitCode = 3;
}
Console.WriteLine(JsonSerializer.Serialize(response, AgentJson.Options));
return exitCode;

static int? ParseEnvironmentInstance(bool throwOnInvalid)
{
    var raw = Environment.GetEnvironmentVariable("SURGELINK_INSTANCE");
    if (string.IsNullOrWhiteSpace(raw)) return null;
    if (int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0)
        return pid;
    if (throwOnInvalid) throw new ArgumentException("SURGELINK_INSTANCE must be a positive desktop process ID.");
    return null;
}

static async Task<string> ReadBoundedTextFileAsync(string path, int maxBytes, CancellationToken ct)
{
    try
    {
        if (!File.Exists(path)) throw new ArgumentException($"Text file not found: {path}");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.CanSeek && stream.Length > maxBytes)
            throw new ArgumentException($"Text file exceeds the {maxBytes} byte limit.");
        return await ReadBoundedUtf8Async(stream, maxBytes, "Text file", ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
    {
        throw new ArgumentException($"Could not read text file '{path}': {ex.Message}", ex);
    }
}

static async Task<string> ReadBoundedStdinAsync(int maxBytes, CancellationToken ct)
{
    try
    {
        if (Console.IsInputRedirected)
        {
            using var stream = Console.OpenStandardInput();
            return await ReadBoundedUtf8Async(stream, maxBytes, "Standard input", ct).ConfigureAwait(false);
        }

        var builder = new StringBuilder();
        var buffer = new char[4096];
        var byteCount = 0;
        while (true)
        {
            var read = await Console.In.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            byteCount += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (byteCount > maxBytes)
                throw new ArgumentException($"Standard input exceeds the {maxBytes} byte limit.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
    {
        throw new ArgumentException($"Could not read standard input: {ex.Message}", ex);
    }
}

static async Task<string> ReadBoundedUtf8Async(Stream stream, int maxBytes, string source, CancellationToken ct)
{
    using var memory = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0) break;
        if (memory.Length + read > maxBytes)
            throw new ArgumentException($"{source} exceeds the {maxBytes} byte limit.");
        memory.Write(buffer, 0, read);
    }
    var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
    if (bytes.StartsWith("\xEF\xBB\xBF"u8)) bytes = bytes[3..];
    return new UTF8Encoding(false, true).GetString(bytes);
}

static string? FindMcpHost()
{
    // Installers place the MCP host beside the CLI so both share the desktop's
    // single .NET runtime. Older layouts kept it in a sibling "agent" folder.
    var merged = Path.Combine(AppContext.BaseDirectory, "surgelink-agent-host.exe");
    if (File.Exists(merged)) return merged;
    var installed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "agent", "surgelink-agent-host.exe"));
    if (File.Exists(installed)) return installed;
    for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
    {
        if (!File.Exists(Path.Combine(root.FullName, "SurgeLink.slnx"))) continue;
        foreach (var relative in new[]
                 {
                     Path.Combine("src", "SurgeLink.AgentHost", "bin", "x64", "Debug", "net10.0", "surgelink-agent-host.exe"),
                     Path.Combine("src", "SurgeLink.AgentHost", "bin", "Debug", "net10.0", "surgelink-agent-host.exe"),
                     Path.Combine("src", "SurgeLink.AgentHost", "bin", "x64", "Release", "net10.0", "surgelink-agent-host.exe"),
                     Path.Combine("src", "SurgeLink.AgentHost", "bin", "Release", "net10.0", "surgelink-agent-host.exe"),
                 })
        {
            var candidate = Path.Combine(root.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        break;
    }
    return null;
}
