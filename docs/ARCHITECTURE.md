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
                  |  SurgeLink.Core  |<-------|  SurgeLink.Terminal |   engine, no UI
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

`WorkspaceStore` is the single source of truth the UI renders from. It publishes an immutable
`WorkspaceSnapshot` (profiles, open tabs, active tab, search text) and every component reads it
through Reactor's `UseExternalStore`. Session state events from background threads are marshalled
with `IUiDispatcher.Post` before the snapshot is replaced.

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
- All `async` protocol APIs take a `CancellationToken` and must not throw on cancellation.

## Persistence and secrets

| Data | Location | Protection |
|---|---|---|
| Session profiles | `%LOCALAPPDATA%\SurgeLink\sessions.json` | plain (no secrets) |
| Credentials | `credentials.json` | DPAPI (`CurrentUser`) per-value |
| Trusted host keys | `known-hosts.json` | plain fingerprints (TOFU) |
| Settings | `settings.json` | plain |
| Logs | `logs/surge-yyyyMMdd.log` | plain |

All stores write atomically (temp file + replace) through `AtomicJsonFile`.

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
ProfileStore ──┤                        └ ProtocolCatalog ─ SshProtocolPlugin, TelnetProtocolPlugin, DemoProtocolPlugin
CredentialStore┘
HostKeyStore
Dispatcher, InteractionService, AppSettingsStore
```

Adding a protocol plugin is one line in `AppServices.Create`:

```csharp
protocols.Register(new SshProtocolPlugin());
protocols.Register(new TelnetProtocolPlugin());
protocols.Register(new DemoProtocolPlugin());
```

## Testing strategy

- `tests/SurgeLink.Terminal.Tests` — emulator state machine, ANSI/SGR, scrollback, resize,
  selection, input encoder, UTF-8 chunking, performance smoke test.
- `tests/SurgeLink.Protocols.Ssh.Tests` — /proc, df, ps parsers, path normalisation, option parsing.
- `tests/SurgeLink.Protocols.Telnet.Tests` — incremental IAC parsing, option negotiation, NVT/BINARY,
  terminal type, NAWS and TCP connection/close behaviour against local test servers.
- `tests/SurgeLink.Core.Tests` — stores, catalog, session manager behaviour with fake protocols.
- `tests/SurgeLink.Protocols.Conformance` — a protocol-agnostic conformance suite. Any protocol is
  validated by adding one `ProtocolFixture` subclass; it checks capability handles, session
  lifecycle, terminal/file-system/monitor behaviour and no-late-callback guarantees. Demo covers
  every capability; Telnet exercises the terminal-only subset against a loopback server.
- UI is verified through Windows UI Automation (see `docs/VERIFICATION.md`): the app is launched,
  driven with Invoke/Value patterns and its control tree is asserted, which also double-checks
  accessibility names.
