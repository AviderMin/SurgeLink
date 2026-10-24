# Agent, CLI and shared terminal control

Agent setup and controls live behind the robot button immediately left of the four existing
title-bar actions (Alt+G). The five buttons share the Settings button's subtle
style, 32×32 hit target, 14px icons and 8px spacing. The non-modal Agent panel stays
above the workspace without resizing or remounting the terminal.

Tabs with terminal control enabled show a small robot after the session title,
before the native close button. Hovering identifies the session as controlled by
Agent; the accessible tab name includes that status. Read-only sessions show no
control indicator. Revocation, human takeover or disconnection removes it.

## Desktop access

1. Open a connection, or use the offline Demo.
2. Open the title-bar robot panel.
3. Choose **Allow reading** or **Control terminal** for the desired connection.
4. Copy the MCP configuration or CLI command from that same panel.
5. Use the panel's per-session Stop or Stop all actions to revoke access.

Reading exposes host facts, the visible screen, retained incremental terminal
output, metrics, directory listings and text files. Control additionally exposes
PTY text/key input and selecting that tab's terminal/files/monitor view. Agent
input selects the target terminal and returns its viewport to the live position,
so commands and output appear in the same terminal the user sees. No second SSH
shell, saved credentials or background exec channel is created.

Execution policy belongs to the external Agent application. After desktop access
is enabled, SurgeLink does not classify commands or prompt for individual commands
or file reads. The scope is local programs under the same Windows user, not an
authenticated individual Agent. Terminal commands have the remote account's
permissions. Per-client pairing remains future work.

This grant is not a remote path sandbox: file listing and bounded text reading can
access any path allowed by the remote account, including paths outside a project.
The standalone `AgentGateway` denies file access by default. The desktop explicitly
supplies the policy that permits reads for a connection after the user enables
Allow reading or Control terminal; revocation invalidates its handle. An injected
host policy is evaluated for every file operation and need not display a dialog.

Human typing, pasting or sending a terminal key immediately revokes that terminal's
Agent handle before queuing the human input. Writes are serialized, and an Agent
paste plus optional Enter is one transport write. Stop prevents further Agent
input; it does not terminate an already running remote command. The user can
send Ctrl+C deliberately in the terminal.

## Build and CLI

Build on Windows with .NET 10:

```powershell
dotnet build SurgeLink.slnx -c Debug -p:Platform=x64
```

The panel copies an absolute CLI path and explicit desktop PID. A source solution
build normally places the CLI in src/SurgeLink.Cli/bin/Debug/net10.0; a direct
project build with Platform=x64 adds x64 after bin. Release installers include
cli/surgelink-cli.exe and agent/surgelink-agent-host.exe, each self-contained.

Example after allowing terminal control in the desktop:

```powershell
$cli = '.\src\SurgeLink.Cli\bin\Debug\net10.0\surgelink-cli.exe'
& $cli sessions list --json
$sessionHandle = '<handle from data[].handle>'
$screen = & $cli terminal screen --session $sessionHandle --json | ConvertFrom-Json
$operation = [guid]::NewGuid().ToString('N')
$sendArguments = @(
    'terminal', 'send', '--session', $sessionHandle,
    '--text', 'echo hello', '--submit',
    '--revision', [string]$screen.data.inputRevision,
    '--operation-id', $operation, '--json'
)
& $cli @sendArguments

# Keep the pre-input cursor to read all output that may arrive during the write.
& $cli terminal output --session $sessionHandle --cursor $screen.data.outputCursor --json
```

To retry an uncertain transport request, reuse the exact sendArguments array.
Do not make a new operation ID merely because the response was lost. For a new key
or command, obtain a fresh screen/revision and use a fresh operation ID.

Other commands:

```text
surgelink-cli sessions list [--instance PID] [--json]
surgelink-cli sessions info --session HANDLE
surgelink-cli sessions activate --session HANDLE [--view terminal|files|monitor]
surgelink-cli terminal screen --session HANDLE
surgelink-cli terminal output --session HANDLE [--cursor N]
surgelink-cli terminal send --session HANDLE --text TEXT [--submit] --revision N --operation-id ID
surgelink-cli terminal key --session HANDLE --key Ctrl+C --revision N --operation-id ID
surgelink-cli monitor snapshot --session HANDLE
surgelink-cli files list --session HANDLE --path REMOTE_PATH
surgelink-cli files read --session HANDLE --path REMOTE_PATH
```

Every command accepts --instance PID and --json. Without --instance exactly one
desktop process named SurgeLink must be running. The CLI never launches the desktop.
Results are always one JSON envelope: success, data, errorCode, message. Help is plain
text. Exit codes: 0 success, 2 usage, 3 unavailable/transport timeout, 4 rejected or
failed operation, 130 Ctrl+C cancellation.

## MCP

Copy MCP configuration from the robot panel; it contains an absolute executable
path and the current desktop PID. A typical stdio client configuration is:

```json
{
  "mcpServers": {
    "surgelink": {
      "command": "C:\\Users\\<user>\\AppData\\Local\\Programs\\SurgeLink\\agent\\surgelink-agent-host.exe",
      "args": ["--instance", "12345"]
    }
  }
}
```

Use the actual path/PID. Refresh the configuration after restarting the desktop.
An empty args array enables automatic discovery when only one desktop is running.

The tools are list_sessions, get_session_info, read_terminal, read_terminal_output,
send_terminal_text, send_terminal_key, select_session, get_system_snapshot,
list_directory and read_text_file. The write tools and select_session require a
control grant. Read-only tool annotations are hints; gateway enforcement decides
which capabilities the shared handle permits.

Example send_terminal_text arguments:

```json
{
  "session": "<shared handle>",
  "text": "echo hello",
  "submit": true,
  "expectedInputRevision": 0,
  "operationId": "echo-hello-1"
}
```

Get expectedInputRevision from read_terminal immediately before input.
send_terminal_key uses key instead of text/submit. Supported keys include Enter,
Tab, Escape, Backspace, Space, arrows, Home, End, PageUp, PageDown, Insert, Delete,
F1–F12, letters/digits and Ctrl/Alt/Shift combinations. Encoding follows the current
terminal modes, including application cursor keys and bracketed paste.

Structured content and text both contain the same JSON envelope. Tool failures
set MCP isError. stdout carries only MCP messages. The SDK stays in AgentHost;
both entry points call AgentClient and the same desktop gateway.

## Input and observation contract

The additive JSON contract remains version 1. Old read-only clients remain valid.

- terminal.screen includes text, dimensions, cursor, terminal modes, viewportOffset,
  inputRevision, outputCursor, isClosed, truncated and source=screen. It reads the
  actual visible viewport, capped at 32,768 UTF-16 units.
- terminal.output takes an optional cursor and returns text, cursor, oldestCursor,
  latestCursor, gap and hasMore. Cursors count decoded UTF-16 units, not bytes or
  commands. Output retains ANSI escapes, decodes UTF-8 across incoming chunks,
  retains at most 131,072 units and pages at most 16,384 units without splitting
  surrogate pairs. A history gap is explicit; future/negative cursors fail.
- terminal.text and terminal.key require a control grant, expectedInputRevision
  and operationId. Text is capped at 16,384 units; submit defaults to false.
- Every human or Agent input changes inputRevision. A stale revision returns
  input_conflict before writing. Human input also revokes the desktop control grant.
- Operation IDs are 1–128 ASCII letters/digits/dots/dashes/underscores. Exact retries
  under the same shared handle replay the acknowledgement; different arguments
  with the same ID return operation_conflict. The gateway retains at most 1,024
  input records per handle without eviction; at that limit, stop and re-enable
  control for a fresh handle.
- A successful write returns status=input_sent, operationId, inputRevision,
  outputCursor, acceptedBytes, submitted and commandCompletionKnown=false.
  Its outputCursor is captured before the PTY write to avoid skipping a fast reply.
  This acknowledges a transport write, not remote completion or an exit code.
- If a write may have partially happened, input_outcome_unknown is remembered
  for that operation. Observe the terminal; do not automatically resend under a
  new ID. Terminal mode, shell prompts and incremental output remain observations,
  never a guaranteed command boundary.
- sessions.activate takes view=terminal/files/monitor and requires control access.
  Input also selects its target terminal automatically. Real terminal dimensions
  are managed by the visible UI, rather than an independent Agent resize.
- Files accept strict Unicode text up to 128 KiB of actual bytes. Directory results
  contain at most 500 entries and truncated; existing protocols still collect the
  full listing before this result bound. Paths and symlinks use server semantics,
  without a separate path sandbox.

Error codes additionally include control_required, invalid_arguments, input_conflict,
operation_conflict, operation_limit, input_outcome_unknown, terminal_unavailable and
invalid_cursor, alongside existing session_unavailable, unsupported, cancelled,
read_failed, version_mismatch, unknown_method, output_limit and operation_failed.

## Lifecycle and implementation

SurgeLink.Agent owns immutable DTOs, gateway, bounded framing and shared client.
SurgeLink.Cli and SurgeLink.AgentHost are adapters; neither owns SSH credentials or
references protocol implementations. ITerminalAutomation is implemented by the
existing TerminalBridge. The gateway invokes a UI dispatcher adapter to select
authorized tabs/views before writing.

Named pipes use CurrentUserOnly, first-instance ownership and a 1 MiB frame limit.
The current transport serves requests serially. Server requests have a cooperative
60s deadline and 2s reply budget; clients have a 3s connection timeout and 65s total
deadline. Ctrl+C stops the local CLI request; it does not itself send Ctrl+C to the
remote PTY or immediately cancel all work after a client disconnects.

Grants and input records are process-local and bound to one open connection.
Changing access mode renews the handle. Session closing removes/cancels grants and
drains outstanding operations before disposing the transport. An uncooperative
protocol call is drained instead of disposing resources beneath it.

The panel shows recent bounded activity without copying commands or terminal/file
payloads. Those payloads may contain secrets or malicious instructions. UI privacy
masking does not redact the API payload. SurgeLink does not contact an AI provider;
the external Agent application controls policy and any provider transmission.

## Verification

Run the full offline suite with:

```powershell
dotnet test SurgeLink.slnx -c Release -p:Platform=x64 -m:1
python tools/check-ui-localization.py
python tools/check-repository.py
```

Coverage includes actual CLI and SDK stdio-host processes, permission scopes,
operation replay/conflicts, ambiguous writes, ordered PTY input, manual takeover,
desktop tab selection, streaming Unicode/output gaps and unchanged protocol tests.
The robot placement, empty panel and Demo panel layout are inspected in the running
desktop. Real SSH/Telnet/serial device control and ARM64 remain separate integration
checks.
