# Agent CLI v1

SurgeLink exposes existing desktop connections through a local, read-only CLI.
`SurgeLink.Agent` owns versioned DTOs, the gateway, framed named-pipe transport and
shared client. `SurgeLink.Cli` is a thin command adapter. `SurgeLink.AgentHost` exposes
the same operations over MCP stdio using the official C# SDK. It calls `AgentClient`
directly, without spawning the CLI or loading SSH implementations.

## Try it

Build the desktop and CLI with .NET 10 on Windows:

```powershell
dotnet build SurgeLink.slnx -c Debug -p:Platform=x64
```

Start the desktop, open an SSH or offline Demo connection, then select **Allow CLI
access** in that session's header and read the consent dialog. Hover over the button
to see the desktop process ID. In a separate PowerShell window:

```powershell
$cli = '.\src\SurgeLink.Cli\bin\Debug\net10.0\surgelink-cli.exe'
& $cli sessions list --json
# Copy a handle from data[].handle into $sessionHandle.
$sessionHandle = '<handle>'
& $cli sessions info --session $sessionHandle --json
& $cli terminal screen --session $sessionHandle --json
& $cli monitor snapshot --session $sessionHandle --json
& $cli files list --session $sessionHandle --path /home/demo --json
& $cli files read --session $sessionHandle --path /home/demo/demo.txt --json
```

Both file commands require separate desktop confirmation. For multiple running
desktops, add `--instance <desktop-process-id>`. Without this option exactly one
process named `SurgeLink` must be running. The CLI does not launch the desktop.
Release packaging includes the self-contained executable at `cli/surgelink-cli.exe`
under the install/package directory; it is not automatically added to PATH.

If building the CLI project directly with `-p:Platform=x64`, its executable is under
`bin/x64/Debug/net10.0/`. Solution builds may map portable projects to AnyCPU instead.

## Connect an MCP client

Release packaging includes `agent/surgelink-agent-host.exe`, separately from the CLI
and desktop dependencies. For development, publish it into a known directory:

```powershell
dotnet publish src/SurgeLink.AgentHost -c Release -r win-x64 --self-contained true -o artifacts/agent-host
```

Configure a client supporting local MCP stdio, using the actual absolute executable
path. A typical client configuration is:

```json
{
  "mcpServers": {
    "surgelink": {
      "command": "D:\\Projects\\Github\\SurgeLink\\artifacts\\agent-host\\surgelink-agent-host.exe",
      "args": []
    }
  }
}
```

For multiple desktop instances use `"args": ["--instance", "12345"]`, replacing the
PID with the one displayed by the desktop. Open a connection and allow CLI access in
the desktop before asking the agent to use it. Configure the client's tool timeout
to at least 70 seconds if it normally times out sooner, to allow desktop file approval.
MCP does not open connections or approve desktop dialogs on the user's behalf.

The tools are `list_sessions`, `get_session_info`, `read_terminal`,
`get_system_snapshot`, `list_directory`, `read_text_file`. All except `list_sessions`
require a `session` handle; the file tools also require `path`. Results contain the
same JSON envelope as the CLI in both text and structured content; failures set
MCP `isError`. Read-only annotations are hints; enforcement stays in the gateway.
stdout contains only MCP messages. The SDK dependency is isolated in AgentHost;
the central Logging.Abstractions patch is updated to 10.0.13 to satisfy SDK 2.3.0.

## Contract

Requests use contract `version: 1`, `method`, optional `session` and optional `path`.
Methods: `sessions.list`, `sessions.info`, `terminal.screen`, `monitor.snapshot`,
`files.list`, `files.read`. Unknown methods/versions are rejected.

Every command emits one JSON response with `success`, `data`, `errorCode`, `message`.
`--json` is accepted for explicit scripts; JSON is also the default. Help is plain text.
The terminal result identifies `source: "screen"`: it is the current emulator viewport,
not command history or evidence of command completion. It is capped at 32,768 UTF-16
code units with a `truncated` flag. File reads accept strict Unicode text up to 128 KiB
of actual bytes; larger/binary files fail. Directory results contain at most 500
entries and a `truncated` flag (the existing protocol still collects the full listing).

Exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 2 | Invalid command or arguments |
| 3 | Desktop unavailable, transport failure or client timeout |
| 4 | Gateway rejected the request or operation failed |
| 130 | Local Ctrl+C cancellation |

Gateway errors include `session_unavailable`, `version_mismatch`, `unknown_method`,
`unsupported`, `invalid_path`, `denied`, `cancelled`, `read_failed`, `operation_failed`
and `output_limit`. CLI errors include `usage`, `desktop_unavailable`, `timeout` and
`cancelled`. Remote error text and payloads are not included in operation logs.

## Access and lifecycle

- Each desktop listens at `SurgeLink.Agent.v1.<PID>` after first consent. The pipe
  uses `CurrentUserOnly`, first-instance ownership, a 1 MiB frame bound and a serial
  request loop. It remains available with an empty session list after all grants
  are revoked. There is no TCP listener or credential store in the CLI.
- The trust boundary is the Windows user, **not individual AI applications**.
  Every local program under that user can discover/use shared handles. This v1
  does not claim client pairing or per-agent identity. Do not share a connection
  if this scope is too broad; per-client pairing is a later extension.
- Random session handles are process-local and bound to one open tab/connection.
  Revocation removes the handle, cancels pending work and drains active operations.
  `WorkspaceStore.SessionClosing` performs that drain before disposing protocol
  resources. New connections and re-sharing get new handles; grants are not persisted.
- File approval binds to the exact requested operation and path for one call.
  Paths follow the protocol's normal semantics, including symlinks; there is no
  claim of a remote path sandbox. Rejection performs no remote file I/O.
- Requests have a cooperative 60-second server deadline (including approval),
  a 2-second reply budget, a 3-second client connection timeout and a 65-second overall client deadline.
  Revocation/teardown waits for an uncooperative protocol call to finish instead
  of disposing resources beneath it. Ctrl+C stops the local client; because v1
  has one request per connection, it does not immediately cancel remote work.
- Terminal/file contents are untrusted data and can contain secrets or malicious
  instructions. Agent integrations must treat them as observations, not authority.
  UI privacy masking does not redact these payloads. No model provider is contacted
  by SurgeLink itself.

## Validation and next steps

```powershell
dotnet test tests/SurgeLink.Agent.Tests -c Release -p:Platform=x64
dotnet test tests/SurgeLink.Core.Tests -c Release -p:Platform=x64
```

Tests cover invisible unshared sessions, expired handles, rejection without file
I/O, per-request approval, revocation/draining, actual-byte limits, malformed frames,
named-pipe round trips, CLI argument validation, the real CLI executable and an SDK
MCP client launching the real stdio host, discovering tools and reading/revoking a
shared session through IPC. Desktop consent and real SSH remain separate runtime
checks; the new button's layout was inspected in a running offline Demo session.

Verification on 2026-10-24: solution Debug build passed with zero warnings/errors;
all 978 Release tests passed (36 Agent/CLI/MCP, 230 Core, 58 conformance, 32 serial,
321 SSH, 16 Telnet, 285 terminal). Repository and UI-localization checks reported
zero findings. `tools/package-exe.ps1 -PublishOnly` successfully produced an x64
self-contained desktop payload containing both CLI and MCP executables. Actual
installer/APPX installation, ARM64 devices, desktop consent interaction and a real
SSH-backed Agent workflow were not exercised in this run.

Next: per-client pairing and an activity panel.
Command execution needs an independent, bounded SSH exec capability, operation IDs
and cancellation/outcome semantics. This version exposes no shell execution, terminal
input, file mutations, local file paths or connection creation.
