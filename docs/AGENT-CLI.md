# Agent, CLI and shared terminal control

Agent setup and controls live behind the robot button immediately left of the four existing
title-bar actions (Alt+G). The five buttons share the Settings button's subtle
style, 32×32 hit target, 14px icons and 8px spacing. The non-modal Agent panel stays
above the workspace without resizing or remounting the terminal. The overlay host stays mounted and is hidden while closed, so asynchronous session/store updates cannot replace its component identity.

Tabs with terminal control enabled show a small robot after the session title,
before the native close button. Hovering identifies the session as controlled by
Agent; the accessible tab name includes that status. Read-only sessions show no
control indicator. Explicit Stop, disconnection or a mode change removes it; typing in a
controlled terminal pauses the Agent but keeps the indicator.

## Desktop access

1. Open a connection, or use the offline Demo.
2. Open the title-bar robot panel.
3. In **Connect with MCP**, choose your client and copy its user setup command
   or configuration. Register once in the client's user configuration so it is
   available across workspaces, then reconnect MCP in that client.
4. Choose **Allow reading**, **Allow files**, or **Control terminal** for the desired
   connection. Ask the Agent to use SurgeLink for your task. The panel shows MCP client
   presence separately from session access; connecting a client never grants a session.
5. Use the panel's per-session Stop or Stop all actions to revoke access. Typing in a
   controlled terminal only pauses the Agent; use **Resume Agent** to continue.

**Allow reading** exposes host facts, the visible screen, retained incremental
terminal output and metrics. **Allow files** adds read-only directory listings and
text-file reads. **Control terminal** adds PTY text/key input, selecting that tab's
terminal/files/monitor view, and mutating file operations (write, delete, rename,
mkdir, upload, download); it also includes file reads. Agent input selects the target
terminal and returns its viewport to the live position, so commands and output appear
in the same terminal the user sees. No second SSH shell, saved credentials or
background exec channel is created.

Execution policy belongs to the external Agent application. After desktop access
is enabled, SurgeLink does not classify commands or prompt for individual commands
or file reads. The scope is local programs under the same Windows user, not an
authenticated individual Agent. Terminal commands have the remote account's
permissions. Per-client pairing remains future work.

This grant is not a remote path sandbox: file listing and bounded text reading can
access any path allowed by the remote account, including paths outside a project.
The standalone `AgentGateway` denies file access by default. The desktop explicitly
supplies the policy that permits reads for a connection after the user enables
**Allow files** or Control terminal; revocation invalidates its handle. An injected
host policy is evaluated for every file operation and need not display a dialog.

A Control grant expires after 30 minutes without Agent control activity (terminal
input, view selection or a file mutation). It then falls back to **Allow files** when
that was part of the grant, otherwise to read-only terminal/monitor access. Reads
do not extend the control lease; re-enable Control in the panel to write again.
Control expiry does not clear a human pause: if the user has taken the terminal,
reads stay paused until **Resume Agent** is used. Control activity is renewed only
after the operation passes its authorization checks; a queued or approval-pending
request that outlives the lease returns `control_expired` instead of executing.

Human typing or sending a terminal key pauses that terminal's Agent operations
before queuing the human input. The control grant and its handle remain valid,
but every session-level operation on that handle returns `user_operating` until
the user resumes Agent control in the panel. The Agent must then stop all
operations, must not retry or re-acquire the session, and waits for the user.
Writes are serialized, and an Agent paste plus optional Enter is one transport
write. Only explicit **Stop** prevents further Agent input; it does not terminate
an already running remote command. The user can send Ctrl+C deliberately in the
terminal.

Pasting reads the clipboard asynchronously. The pause happens when the clipboard
read completes and the resulting input is queued; an Agent write may finish while
that read is pending. Use Stop first when clipboard access must not overlap Agent input.

### Pause, resume and Stop

Typing in a controlled terminal is a soft pause, not a revocation. The desktop
sets `userOperating=true` for that session in `sessions.list`, returns
`user_operating` for every other operation on the handle, and keeps the control
grant. When the user finishes at the keyboard, **Resume Agent** in the session
card clears the pause without issuing a new handle or new check token; the same
handle resumes working. In the panel, the paused session's **Control terminal**
button is shown as **Resume Agent** for this action. **Stop** (per session) and
**Stop all** remain hard revocations: they invalidate handles and drain in-flight
operations. Closing the session, replacing its terminal or dropping the connection
also revokes.

## Mandatory Check Sessions before terminal input

Every **new** text/key operation must first call `check_sessions` (MCP) or
`sessions check --session HANDLE` (CLI). Inspect `session.target.server`, `port`,
`sessionId`, `username`, `connectionState`, active state and `screen` against the
intended target. The target identity comes from the open connection, not a subsequently
edited saved profile. `usernameSource=configured_login` is the connection login;
`effectiveShellUser=null` deliberately makes no claim about identity after `su`,
nested SSH or entering containers. The check sends no probing command into the PTY.

The server returns a random `checkToken`, `expiresUtc` and `screen.inputRevision`.
Pass that token as `checkToken` / `--check-token` and the revision with the input.
A token expires after two minutes and is consumed by one new input. A newer check
replaces the older check for that handle. Revocation/reconnection, an input revision
change, a screen/output/mode change, or a change in the target's active state makes the old check unusable.
Scrolling the history (`viewportOffset`) and output that leaves the visible screen
unchanged are not such changes: the guard compares screen text, cursor position,
terminal modes, closed/truncated state and input revision, so reading older output
does not force a re-check.
The gateway rejects missing/stale checks with `session_check_required` before writing;
the message distinguishes an expired token, an input revision change, a screen
change and an active-state change. `brief=true` / `--brief` keeps the target and
token but returns only the last few screen lines instead of the full viewport.
Exact retries of an already recorded operation retain the original token/arguments
and return the stored acknowledgement, even after its token expires.

Screen checks are repeated after tab activation and under the desktop screen lock
when reserving input. A remote PTY can still change after that reservation; checks
do not prove remote command semantics. A write with an uncertain outcome blocks new
input on that handle until the human inspects the terminal and resumes Agent control.

This enforces the check step, not an Agent's understanding of the target. It does
not establish shell readiness or command completion, nor replace the external
Agent application's execution policy. Older write clients need the updated CLI/MCP
host; restart the MCP connection so its tool schemas/instructions refresh. Read-only
clients continue to work.

## Build and CLI

Build on Windows with .NET 10:

```powershell
dotnet build SurgeLink.slnx -c Debug -p:Platform=x64
```

The panel's **Copy Agent instructions** action copies a localized, ready-to-paste
workflow with an absolute CLI path. It tells the Agent to execute `sessions list`
first, select and check the intended session (using `--brief`/`--minimal` on
follow-up checks), prefer `terminal exec` for standard POSIX commands (or
`terminal send` with `terminal output`/`wait` for interactive flows), and read
large log tails via `files read --tail-bytes`. By default the CLI auto-discovers the only running desktop; if
several instances are running, `surgelink-cli desktops list` reports their PIDs
and the instructions explain how to pass `--instance PID`. Commands use local
Windows PowerShell and work from any workspace on the same computer and Windows
account; neither changing directory nor adding the CLI to PATH is necessary.
The Agent must have a local command execution tool with access to the executable.
This text guides the external Agent; it cannot force that application to invoke
tools. Copy fresh instructions after restarting SurgeLink if an explicit
`--instance` binding was used; automatic discovery survives a restart.

CLI calls return output and exit; external Agents should capture stdout/stderr
and the exit code without opening an interactive terminal window. Direct Windows
process launchers should disable shell execution, redirect both output streams
and suppress console-window creation. For example, with Python's standard library:

```python
import subprocess

result = subprocess.run(
    [r"C:\absolute\path\to\surgelink-cli.exe", "--help"],
    shell=False,
    capture_output=True,
    encoding="utf-8",
    creationflags=subprocess.CREATE_NO_WINDOW,
)
print(result.stdout)
print(result.stderr)
print(result.returncode)
```

Use the actual copied executable path, with arguments passed separately and no
extra quotes embedded in the path. For session operations, include the copied
`--instance PID`. In .NET, set `UseShellExecute=false`, `CreateNoWindow=true`,
`RedirectStandardOutput=true` and `RedirectStandardError=true`, and drain both
streams concurrently. A Windows Terminal popup reporting `0x800700e8` is a
launch/console error; retry read-only `--help` through captured process execution
to diagnose it. This popup does not establish whether any requested terminal
input was delivered; preserve the operation ID and arguments for input retries.

A source solution
build normally places the CLI in src/SurgeLink.Cli/bin/Debug/net10.0; a direct
project build with Platform=x64 adds x64 after bin. Release installers place
surgelink-cli.exe and surgelink-agent-host.exe in the application folder next to
SurgeLink.exe. All three are self-contained but share one copy of the .NET 10
runtime; installing them into separate folders would ship that runtime three
times (about 154 MB of identical files).

Example after allowing terminal control in the desktop:

```powershell
$cli = '.\src\SurgeLink.Cli\bin\Debug\net10.0\surgelink-cli.exe'
& $cli sessions list --json
$sessionHandle = '<handle from data[].handle>'
$check = & $cli sessions check --session $sessionHandle --json | ConvertFrom-Json
if (-not $check.success) { throw $check.message }
$check.data.session.target # Verify this is the intended server/session/login.
$check.data.screen.text    # Inspect current terminal context before continuing.
$screen = $check.data.screen
$operation = [guid]::NewGuid().ToString('N')
$sendArguments = @(
    'terminal', 'send', '--session', $sessionHandle,
    '--text', 'echo hello', '--submit',
    '--revision', [string]$screen.inputRevision,
    '--operation-id', $operation, '--check-token', $check.data.checkToken, '--json'
)
& $cli @sendArguments

# Keep the pre-input cursor to read all output that may arrive during the write.
& $cli terminal output --session $sessionHandle --cursor $screen.outputCursor --json
```

To retry an uncertain transport request, reuse the exact sendArguments array.
Do not make a new operation ID merely because the response was lost. For a new key
or command, run Check Sessions again and use a fresh operation ID.

Other commands:

```text
surgelink-cli sessions list [--instance PID] [--json]
surgelink-cli sessions info --session HANDLE
surgelink-cli sessions check --session HANDLE [--brief|--minimal]
surgelink-cli sessions activate --session HANDLE [--view terminal|files|monitor]
surgelink-cli terminal screen --session HANDLE [--tail-rows N] [--strip-ansi]
surgelink-cli terminal output --session HANDLE [--cursor N] [--max-bytes N] [--max-lines N] [--strip-ansi]
surgelink-cli terminal wait --session HANDLE [--cursor N] [--idle-ms 500] [--timeout-ms 30000] [--max-bytes N] [--max-lines N] [--strip-ansi]
surgelink-cli terminal send --session HANDLE (--text TEXT | --text-file FILE | --text-stdin) [--submit] --revision N --operation-id ID --check-token TOKEN
surgelink-cli terminal exec --session HANDLE (--text TEXT | --text-file FILE | --text-stdin) --revision N --operation-id ID --check-token TOKEN [--timeout-ms 30000] [--max-bytes N] [--max-lines N] [--strip-ansi]
surgelink-cli terminal key --session HANDLE --key Ctrl+C --revision N --operation-id ID --check-token TOKEN
surgelink-cli monitor snapshot --session HANDLE
surgelink-cli files list --session HANDLE --path REMOTE_PATH [--offset N] [--limit N]
surgelink-cli files read --session HANDLE --path REMOTE_PATH ([--offset N] [--max-bytes N] | [--tail-bytes N])
surgelink-cli files stat --session HANDLE --path REMOTE_PATH
surgelink-cli files write --session HANDLE --path REMOTE_PATH (--text TEXT | --text-file FILE | --text-stdin) [--overwrite]
surgelink-cli files delete --session HANDLE --path REMOTE_PATH [--recursive]
surgelink-cli files rename --session HANDLE --path REMOTE_PATH --destination NEW_PATH
surgelink-cli files mkdir --session HANDLE --path REMOTE_PATH
surgelink-cli files upload --session HANDLE --path REMOTE_PATH --local-path LOCAL_FILE [--overwrite]
surgelink-cli files download --session HANDLE --path REMOTE_PATH --local-path LOCAL_FILE [--overwrite]
surgelink-cli desktops list [--json]
surgelink-cli doctor [--json]
```

Every command accepts --instance PID and --json. Without --instance exactly one
desktop process named SurgeLink must be running. The CLI never launches the desktop.
Results are always one JSON envelope: success, data, errorCode, message. Help is plain
text. Exit codes: 0 success, 2 usage, 3 unavailable/transport timeout, 4 rejected or
failed operation, 130 Ctrl+C cancellation.

## MCP (primary Agent integration)

The panel provides setup guidance for Codex, Claude Code, Cursor and generic MCP
clients. The configuration contains an absolute Host path and defaults to automatic
desktop discovery, with no PID or workspace-specific working directory:

```json
{
  "mcpServers": {
    "surgelink": {
      "command": "C:\\Users\\<user>\\AppData\\Local\\Programs\\SurgeLink\\surgelink-agent-host.exe",
      "args": []
    }
  }
}
```

Use the actual path copied from your installation. Register in user configuration
once; desktop restarts do not require configuration changes while that executable
path stays the same. Reconnect MCP after restarting the desktop so the client
launches a fresh Host. The Host resolves the running desktop again on each operation.
Session grants and handles still expire on restart: share the desired sessions
again and call `list_sessions` to obtain current handles.

Starting with 1.0.3.12, the bundled MCP Host lives next to `SurgeLink.exe`.
After upgrading from a version with an `agent` subfolder, copy the current
configuration from the Agent panel and update the existing `surgelink` entry.

The bundled helpers follow desktop lifetime. A Host bound with `--instance` or
`select_desktop` exits when that exact desktop process exits, including when the
MCP client keeps stdin open. Switching selection while the desktop is still
running moves that lifetime binding to the new selection. In automatic mode,
the Host stays running to advertise tools even if no desktop is running yet and
connects when one starts; once it has observed a running desktop, it exits when
no SurgeLink desktops remain. CLI requests cancel and exit if their target desktop exits.
Closing one desktop does not terminate helpers bound to another desktop, and
no external MCP client or unrelated process is terminated.

Automatic discovery requires exactly one running desktop. The Host distinguishes
`desktop_not_running`, `desktop_ambiguous`, `desktop_ipc_unavailable`,
`desktop_ipc_timeout` and `desktop_instance_expired`; these are connection
states, not missing session permissions. With several desktops, call
`list_desktops`, inspect reachability and shared-session counts, then use
`select_desktop` with the intended `processId`. This changes routing for the
current MCP connection only and never grants session access. To bind permanently
from the intended desktop's panel, select **Bind to this desktop** and copy setup
again. This adds `--instance PID`; that PID is valid only for this run. After a
restart, refresh the explicit binding or use the default automatic configuration
before reconnecting MCP. A reused PID is checked by
process start time and must be explicitly selected again. Selecting without
`processId` clears a manual or obsolete startup binding for this MCP connection;
it does not edit the saved client configuration. Updating an existing client entry should replace only the `surgelink`
entry, preserving all other MCP servers and client settings.

User-level registration commands generated by the panel (local PowerShell):

```powershell
codex mcp add surgelink -- 'C:\absolute\path\to\surgelink-agent-host.exe'
claude mcp add --transport stdio --scope user surgelink -- 'C:\absolute\path\to\surgelink-agent-host.exe'
```

Codex uses its user MCP configuration; Claude Code requires the explicit user
scope. Cursor users merge the JSON `surgelink` entry into `mcpServers` in
`~/.cursor/mcp.json`. Generic clients should use their user-level local stdio
settings. The panel generates setup text; it does not execute these commands or
edit external client configuration automatically. Client restart/reconnection may
be necessary before newly configured tools appear. Client setup references:
[Codex](https://developers.openai.com/codex/mcp/),
[Claude Code](https://code.claude.com/docs/en/mcp),
[Cursor](https://prod.cursor.com/help/customization/mcp).

The desktop IPC starts with the application even before sharing. `list_sessions`
returns a successful empty list until a session is authorized; all session
operations still require an active grant. The MCP Host can advertise tools while
the desktop is offline and reconnect when it becomes available.

After a client discovers or calls tools, its Host sends a bounded presence
heartbeat every 15 seconds. The panel shows recent clients and their self-reported
names. Normal Host shutdown removes presence; after an unclean exit it expires
after 90 seconds (panel refreshes every five seconds). Up to 32 active presence
records are retained. Heartbeats are not added to activity history and never grant
access. This is an advisory connection indicator, not per-client authentication,
pairing or a guarantee that the external Agent will choose to call a tool.

The tools are list_desktops, select_desktop, list_sessions, get_session_info,
check_sessions, read_terminal, read_terminal_output, wait_terminal_output,
run_terminal_command, send_terminal_text, send_terminal_key, select_session, get_system_snapshot,
list_directory, read_text_file, stat_path, write_text_file, delete_path,
rename_path, create_directory, upload_file and download_file. The file read tools
(list_directory, read_text_file, stat_path) require a file-read grant; the write
tools and select_session require a control grant. Read-only tool annotations are hints;
gateway enforcement decides which capabilities the shared handle permits.

Example send_terminal_text arguments:

```json
{
  "session": "<shared handle>",
  "text": "echo hello",
  "submit": true,
  "expectedInputRevision": 0,
  "operationId": "echo-hello-1",
  "checkToken": "<token from check_sessions>"
}
```

Call check_sessions immediately before each new input. Verify the target and screen,
then pass its checkToken and screen.inputRevision as expectedInputRevision.
send_terminal_key uses key instead of text/submit. Supported keys include Enter,
Tab, Escape, Backspace, Space, arrows, Home, End, PageUp, PageDown, Insert, Delete,
F1–F12, letters/digits and Ctrl/Alt/Shift combinations. Encoding follows the current
terminal modes, including application cursor keys and bracketed paste.

Structured content and text both contain the same JSON envelope. Tool failures
set MCP isError. stdout carries only MCP messages. The SDK stays in AgentHost;
both entry points call AgentClient and the same desktop gateway.

## Input and observation contract

The additive JSON contract remains version 1. Old read-only clients remain valid.
Internal `client.heartbeat` / `client.disconnect` requests carry bounded `clientId`
and `clientName` metadata for presence only; they are not MCP tools or capabilities.

- terminal.screen includes text, dimensions, cursor, terminal modes, viewportOffset,
  inputRevision, outputCursor, isClosed, truncated and source=screen. It reads the
  actual visible viewport, capped at 32,768 UTF-16 units. `viewportOffset` and
  `outputCursor` are informational: both are excluded from the input guard so a
  scroll or invisible output cannot invalidate a fresh check.
- terminal.output takes an optional cursor and returns text, cursor, oldestCursor,
  latestCursor, gap and hasMore. Cursors count decoded UTF-16 units, not bytes or
  commands. Output retains ANSI escapes, decodes UTF-8 across incoming chunks,
  retains at most 131,072 units and pages at most 16,384 units without splitting
  surrogate pairs. A history gap is explicit; future/negative cursors fail.
  `maxBytes` and `maxLines` bound the returned page from its start. `cursor` points
  just after the characters actually returned, so successive calls page through
  the output without repeating or skipping it. With `stripAnsi`, the limits count
  visible text and an escape sequence is never split across pages; a page may
  exceed the byte budget by at most one code point. `stripAnsi` removes escape
  sequences for easier reading.
- terminal.wait polls incremental output from the supplied cursor and returns
  when output has been quiet for `idleMilliseconds` or `timeoutMilliseconds`
  expires. It returns text, cursor, timing, `idle`, `timedOut`, a short screen
  tail and `commandCompletionKnown=false`. The text is the first bounded part of
  the accumulated output and `cursor` continues after it; when `hasMore` is true,
  call terminal.output with that cursor to read the remainder. Quiet output is an
  observation, not a shell prompt detector or an exit code; inspect the returned
  screen/prompt and verify the actual result before reporting completion. Leading
  PTY echo of the just-sent command is removed from the returned text, but only an
  occurrence that ends its own line counts as echo, so the same characters inside
  real output are preserved.
- terminal.exec submits the command and its completion marker together as one POSIX
  shell input line. The shell parses the marker before the command can read stdin. It waits
  for that sentinel up to `timeoutMilliseconds` (capped at 50,000 ms to fit
  inside the 60s IPC frame deadline; quiet gaps do not end the wait). When the
  sentinel is observed it returns `status=executed`, `exitCode`,
  `commandCompletionKnown=true`, `timedOut=false`, `output` and a 4-line `screen`
  tail. If the deadline expires without the sentinel, it returns
  `status=timed_out`, `timedOut=true`, `commandCompletionKnown=false`,
  `exitCode=null`, any output collected so far (`output.cursor` to continue via
  `terminal.output` / `terminal.wait`) and the current `screen` tail without
  locking the session. Pass a small `timeoutMilliseconds` for commands that
  prompt for input. The returned output keeps the end of the stream so the
  final result is available when `maxBytes` truncates a long response. Terminal
  wait and exec cap the returned output at 128 KiB. If capture stops at a gap or
  reaches its limit before seeing the marker, exec returns `status=output_incomplete`
  and a cursor for reading additional retained output. It cannot recover history
  already discarded by the terminal.
  Trailing newlines are trimmed before building the shell input. A wrapped line
  exceeding 4,000 UTF-8 bytes is rejected before any write. Completion is best effort
  for POSIX bash/sh; non-POSIX shells time out without an exit code.
- sessions.check supports `brief=true` (compact screen) and `minimal=true`
  (compact screen plus only handle, label, target, control state and fileRead instead of
  the full host/session metadata). Both are opt-in; the full check remains the
  default.
- terminal.screen supports `tailRows` and `stripAnsi` for a compact view. These
  options change only the returned text; the full snapshot is still used for the
  check token and input revision.
- terminal.text and terminal.key require a control grant, expectedInputRevision
  from sessions.check, its single-use checkToken and operationId. Text is capped at
  16,384 units; submit defaults to false.
- Every human or Agent input changes inputRevision. A stale revision returns
  session_check_required when it invalidates a check, or input_conflict if it races
  with the final PTY write. Human input also pauses the desktop control grant:
  subsequent operations return user_operating until the user resumes Agent control.
  A file policy decision is re-checked against the pause after it returns, so an
  approval that lands while the user is typing cannot execute behind them.
  The same rule applies to control expiry: approval is re-checked after it returns,
  and a permission-denied file operation is rejected before the confirmation dialog.
  Only explicit Stop revokes the grant.
- New terminal inputs and file changes share a limit of 60 operations per minute
  per grant (one-minute token bucket). A throttled terminal request returns
  input_rate_limited before consuming its check token. Exact operation-ID replays
  do not consume a new permit.
- Operation IDs are 1–128 ASCII letters/digits/dots/dashes/underscores. Exact retries
  under the same shared handle replay the acknowledgement; different arguments
  with the same ID return operation_conflict. The gateway retains at most 1,024
  input records or 16 MiB of serialized responses per handle, whichever is
  reached first, so a long session with large outputs can evict old records.
  Evicted IDs are no longer replay-protected; when exact retry protection matters,
  stop and re-enable control for a fresh handle.
- A successful write returns status=input_sent, operationId, inputRevision,
  outputCursor, acceptedBytes, submitted and commandCompletionKnown=false.
  Its outputCursor is captured before the PTY write to avoid skipping a fast reply.
  This acknowledges a transport write, not remote completion or an exit code.
- If a PTY write may have been partial, input_outcome_unknown is remembered for
  that operation. The grant blocks further terminal inputs and file changes until
  the user renews control. Observe the terminal before continuing.
- sessions.activate takes view=terminal/files/monitor and requires control access.
  Input also selects its target terminal automatically. Real terminal dimensions
  are managed by the visible UI, rather than an independent Agent resize.
- Files accept strict Unicode text up to 128 KiB of actual bytes. Directory results
  contain at most 500 entries per page; `offset`/`limit` page through the listing.
  `files.read` also accepts `offset` and `maxBytes` (up to 512 KiB) to read a
  bounded page of a larger file, or `tailBytes` (up to 512 KiB, mutually exclusive
  with `offset`) to read the end of a large log directly. Pages start and end on UTF-8 character
  boundaries; `maxBytes` may be exceeded by at most one code point, and an
  unaligned `offset` or `tailBytes` start is aligned forward with `skippedBytes` reporting the bytes
  skipped. An offset that points inside the final character returns an empty EOF
  page instead of a decode failure. `nextOffset` is the aligned position for the
  next page (equal to the file length after `tailBytes`, enabling subsequent tail paging). Paths and symlinks use server semantics, without a separate path
  sandbox.
- `files.stat` returns metadata without reading content. `files.write` stages
  complete UTF-8 text and commits atomically where the protocol supports it;
  existing files are protected unless `overwrite` is true. `files.delete`,
  `files.rename`, `files.mkdir`, `files.upload` and `files.download` are also
  available. Local upload/download paths refer to the Windows machine running
  SurgeLink and each such operation requires an explicit confirmation in the
  desktop window that shows the requested local path; declining returns
  `denied`. Downloads stage beside the local destination and publish only after
  the remote read and source close complete. If the requested name is taken,
  `overwrite=true` allows a numbered sibling such as `demo (1).txt`. The response
  gives the actual `localPath`. Existing files stay intact, including files that
  appear during transfer. Mutating file operations
  require terminal-control access; read-only sharing cannot write or delete.

Error codes additionally include user_operating, session_check_required, session_context_unavailable,
control_required, file_access_required, control_expired, input_rate_limited,
invalid_arguments, input_conflict, operation_conflict, operation_limit, input_outcome_unknown,
terminal_unavailable, invalid_cursor, write_failed, local_file_not_found, local_file_exists,
desktop_not_running, desktop_ambiguous, desktop_ipc_unavailable,
desktop_ipc_timeout and desktop_instance_expired, alongside existing session_unavailable, unsupported, cancelled,
read_failed, version_mismatch, unknown_method, output_limit and operation_failed.

## Lifecycle and implementation

SurgeLink.Agent owns immutable DTOs, gateway, bounded framing and shared client.
SurgeLink.Cli and SurgeLink.AgentHost are adapters; neither owns SSH credentials or
references protocol implementations. ITerminalAutomation is implemented by the
existing TerminalBridge. The gateway invokes a UI dispatcher adapter to select
authorized tabs/views before writing.

Named pipes use CurrentUserOnly, first-instance endpoint ownership and a 1 MiB
frame limit. Up to 64 independent connections share one endpoint. Slow file or
monitor calls therefore do not prevent other connections from listing sessions or
sending heartbeats. The gateway still serializes operations for an individual
shared session, including terminal writes. The MCP Host keeps terminal input and
desktop selection ordered; ordinary reads are concurrent, and presence uses a
separate request path. Server requests have a cooperative 60s deadline and a 10s
reply budget that starts after the response is serialized; clients have a 3s connection timeout and 65s total deadline. Ctrl+C stops the local CLI request; it does not itself send Ctrl+C to the
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
operation replay/conflicts, ambiguous writes, ordered PTY input, human pause and resume,
desktop tab selection, concurrent IPC during slow reads, presence during slow
requests, input/selection ordering, explicit multi-desktop recovery and PID-reuse
identity checks, streaming Unicode/output gaps and unchanged protocol tests.
The robot placement, empty panel and Demo panel layout are inspected in the running
desktop. Real SSH/Telnet/serial device control and ARM64 remain separate integration
checks.
