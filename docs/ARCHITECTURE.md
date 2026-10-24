# SurgeLink architecture

SurgeLink is a multi-protocol remote console. The design goal is that **a new protocol is a
new project plus one registration call**, and that no UI code ever references a protocol
implementation.

## Projects and dependency rules

```
            +-----------------------------+
            |        SurgeLink.App        |   WinUI 3 + Reactor (no XAML)
            |  shell, views, composition  |
            +--------------+--------------+
                           |
        +------------------+-------------------+
        |                                      |
+-------v--------+                   +---------v----------+
|  SurgeLink.    |                   |  SurgeLink.        |
|  Protocols.Ssh |                   |  Protocols.Demo    |   protocol plugins
+-------+--------+                   +---------+----------+
        |                                      |
        +------------------+-------------------+
                           |
                  +--------v---------+        +---------------------+
                  |  SurgeLink.Core  |        |  SurgeLink.Terminal |   independent engine, no UI
                  +------------------+        +---------------------+
```

Rules:

1. `SurgeLink.Core` references nothing but logging abstractions and DPAPI. It defines the
   contracts (`IProtocolPlugin`, `IProtocolSession`, `IRemoteTerminal`, `IRemoteFileSystem`,
   `IRemoteSystemMonitor`), the data models, the session manager and persistence.
2. `SurgeLink.Terminal` is a standalone emulator with no dependency on Core or UI: bytes in,
   cell grid out.
3. Protocols depend on Core only. They never reference WinUI or Reactor types.
4. `SurgeLink.App` depends on Core, Terminal and the protocol assemblies it wants to ship.
5. `SurgeLink.Agent` depends on Core and exposes scoped access to existing connections.
   `SurgeLink.Cli` and `SurgeLink.AgentHost` (MCP) call its current-user named pipe contract.
   The desktop provides terminal automation through `TerminalBridge`; the caller's Agent app
   owns execution policy. See [Agent CLI contract](AGENT-CLI.md).

## Connection lifecycle

```
UI (session editor, sidebar)
  └─ WorkspaceStore.OpenAsync(profile, ConnectOptions)
       └─ SessionManager.OpenAsync            <- resolves secrets from ICredentialStore
            └─ IProtocolPlugin.ConnectAsync   <- protocol builds transport, verifies host key
                 └─ IProtocolSession          <- capability handles: Terminal / FileSystem / Monitor
       └─ WorkspaceStore creates a WorkspaceTab + TerminalBridge + FileTransferQueue,
          starts the shell channel
```

`WorkspaceStore` is the single source of truth the UI renders from. It publishes a
`WorkspaceSnapshot` (profiles, open tabs, active tab, search text) and every component reads it
through Reactor's `UseExternalStore`. Session state events from background threads are marshalled
with `IUiDispatcher.Post` before the snapshot is replaced.
The snapshot record is replaced, but its `WorkspaceTab` instances contain mutable runtime
state. Do not treat a captured tab as a historical immutable value. Concurrent close callers
share one completion task; pre-close access drains finish before terminal/transport disposal.

## Capability model

`IProtocolSession` exposes optional handles:

```csharp
public interface IProtocolSession : IAsyncDisposable
{
    string SessionId { get; }
    ProtocolDescriptor Protocol { get; }
    ConnectionState State { get; }
    RemoteHostInfo HostInfo { get; }
    IRemoteTerminal? Terminal { get; }
    IRemoteFileSystem? FileSystem { get; }
    IRemoteSystemMonitor? Monitor { get; }
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    event EventHandler<string>? Faulted;
    Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken ct);
}
```

`ProtocolDescriptor.Capabilities` drives the UI: `SessionWorkspaceView` builds its Terminal /
Files / Monitor switcher from the flags, so a protocol that only supports a shell simply shows
one view.

## Terminal data path

```
remote ──bytes──> IRemoteTerminal.OutputReceived (background thread)
                     │
                     ├─> TerminalBridge: lock(Emulator.SyncRoot); Emulator.Feed(bytes)
                     │                   coalesce repaint onto the UI thread
                     └─> TerminalSurface (UI thread): reads cells, paints visible rows only
user input ──> TerminalSurface key handlers ──> TerminalInputEncoder ──> TerminalBridge.Send*
                     └─> IRemoteTerminal.WriteAsync
resize  ──> TerminalSurface measured cols/rows ──> TerminalBridge.ResizeAsync ──> PTY resize
```

The emulator owns the scrollback; `GetCell(row, column)` reads the *viewport* so the renderer
never has to know about history. `Version` and the coalesced `Invalidated` event keep repaints
proportional to output, not to keystrokes.

## Remote monitoring

`IRemoteSystemMonitor.SampleAsync` returns one `SystemSnapshot` per tick. The SSH implementation
detects the guest OS during connect (`RemoteOsDetector`) and picks a probe:

- **Linux**: one `sh` script per tick that cats `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`,
  `/proc/uptime`, `/proc/net/dev`, `/proc/diskstats` and runs `df -kP`.
- **Windows (OpenSSH guest)**: one PowerShell command per tick that queries CIM/Get-Counter for
  processor load, memory, per-core usage, volumes, network adapter statistics and processes.

Both probes return the same `SystemSnapshot`, so the dashboard is protocol and OS agnostic; network
rates are derived by the view from consecutive samples, while the probes report aggregate disk
read/write throughput directly. A protocol with a different idea of monitoring supplies its own
`IRemoteSystemMonitor` and the UI does not change.

The shell also pins a compact `SidebarMonitor` to the bottom of the session pane. It samples the
active tab's monitor for CPU/RAM gauges and network/disk throughput, so live performance stays
visible while reading the terminal or the file browser.

## Threading model

- One WinUI UI thread. `IUiDispatcher` is the only sanctioned way to touch UI state from
  background callbacks.
- Protocol I/O runs on background threads (SSH.NET blocking APIs are wrapped, never called on
  the UI thread).
- The terminal emulator is *not* internally locked: callers serialise on `SyncRoot`. The bridge
  is the only writer, the renderer the only reader.
- Async protocol APIs take a `CancellationToken` and normally propagate
  `OperationCanceledException`. Callers distinguish cancellation from transport failure.
  Cancellation cannot undo bytes already sent to a remote terminal; an uncertain input result
  requires reading current output rather than blindly replaying a command.

## Persistence and secrets

| Data | Location | Protection |
|---|---|---|
| Session profiles | `%LOCALAPPDATA%\SurgeLink\sessions.json` | plain (no secrets) |
| Credentials | `credentials.json` | DPAPI (`CurrentUser`) per-value |
| Trusted host keys | `known-hosts.json` | plain fingerprints (TOFU) |
| Settings | `settings.json` | plain |
| Logs | `logs/surge-yyyyMMdd.log` | plain |

Stores write unique temporary files, flush them and replace the destination through
`AtomicJsonFile`. Read/modify/write operations acquire a persistent sibling `.lock` file,
read the latest disk state and then publish the change. Lock acquisition has a five-second
limit. Do not remove a live lock file. Read-only loads tolerate malformed JSON for startup;
incremental mutations fail on malformed/unreadable data rather than overwrite it as an empty
store. Strict JSON parse failures are wrapped in `IOException`, retaining the original
`JsonException`, so UI persistence-error handlers can display them without an unhandled
exception. Back up and repair a corrupt file before retrying. `ISessionStore` exposes only
incremental Upsert/Delete operations; its unused whole-set Save API has been removed.

Remote writes stage into unique sibling files and commit through the protocol's rename
operation. The text editor retains the original encoding and raw-byte SHA-256 baseline,
revalidating before staging and before commit. Writers sharing one file-system instance are
serialized. SFTP does not provide server-side compare-and-swap: an external edit between the
last check and rename remains possible. Failed staging cleanup is best effort when disconnected.
Transfer queue shutdown waits up to ten seconds; transport disposal then interrupts remaining
I/O. This is a bounded shutdown policy, not a guarantee that all remote cleanup has completed.

SSH internal command capture drains stdout/stderr concurrently and rejects output above
8 MiB/2 MiB respectively. Limits apply to captured data; SSH.NET also owns transient receive
buffers. Large listings fail explicitly rather than returning incomplete directory contents.

## Host key verification

`ProtocolHostContext` hands a protocol the `IHostKeyStore` and a prompt callback. SSH computes the
SHA-256 fingerprint during the handshake, compares it with the store, and either proceeds, asks
the user (unknown or changed key) through the UI queue, or aborts with
`ProtocolException(ProtocolFailureKind.HostKey)`.

## Composition root

`AppServices.Initialize` runs from the `ReactorApp.Run(..., configure)` pre-mount callback on the UI
thread, then `AppServices.Create` builds the graph exactly once:

```
LoggerFactory ─┬─ ProtocolHostContext ─ SessionManager ─ WorkspaceStore
ProfileStore ──┤                        └ ProtocolCatalog ─ SshProtocolPlugin, TelnetProtocolPlugin, SerialProtocolPlugin, DemoProtocolPlugin
CredentialStore┘
HostKeyStore
Dispatcher, InteractionService, AppSettingsStore
```

Adding a protocol plugin is one line in `AppServices.Create`:

```csharp
protocols.Register(new SshProtocolPlugin());
protocols.Register(new TelnetProtocolPlugin());
protocols.Register(new SerialProtocolPlugin());
protocols.Register(new DemoProtocolPlugin());
```

## Testing strategy

- `tests/SurgeLink.Terminal.Tests` — emulator state machine, ANSI/SGR, scrollback, resize,
  selection, input encoder, UTF-8 chunking, performance smoke test.
- `tests/SurgeLink.Protocols.Ssh.Tests` — /proc, df, ps parsers, path normalisation, option parsing.
- `tests/SurgeLink.Protocols.Telnet.Tests` — incremental IAC parsing, option negotiation, NVT/BINARY,
  terminal type, NAWS and TCP connection/close behaviour against local test servers.
- `tests/SurgeLink.Protocols.Serial.Tests` — COM-port option validation, raw byte I/O, cancellation,
  open/write timeouts, unplug faults and deterministic teardown using a bounded fake port.
- `tests/SurgeLink.Core.Tests` — stores, catalog, session manager behaviour with fake protocols.
- `tests/SurgeLink.Protocols.Conformance` — a protocol-agnostic conformance suite. Any protocol is
  validated by adding one `ProtocolFixture` subclass; it checks capability handles, session
  lifecycle, terminal/file-system/monitor behaviour and no-late-callback guarantees. Demo covers
  every capability; Telnet uses a loopback server, and Serial uses a bounded fake port for the
  terminal-only subset. Real COM hardware requires a separate device smoke test.
- UI is verified through Windows UI Automation (see `docs/VERIFICATION.md`): the app is launched,
  driven with Invoke/Value patterns and its control tree is asserted, which also double-checks
  accessibility names.
