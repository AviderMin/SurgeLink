# Adding a protocol

SurgeLink is built so that a new protocol is **one project + one registration line**. The shell
never learns about protocols; it only consumes capabilities.

Telnet is now implemented in `src/SurgeLink.Protocols.Telnet`; the examples below
describe that integration. For a new protocol, choose a new project name and id.
Set `ProtocolDescriptor.AuthenticatesInTerminal` when login is entered in the
terminal, so the session editor and connector skip connection credentials.

Local device transports (see `SurgeLink.Protocols.Serial`) also set
`UsesNetworkAddress = false`, supply an endpoint label and optional
`DiscoverEndpoints` callback, and declare `ConnectionOptions`. The session editor
renders these option descriptors without referencing the protocol assembly;
stable keys/choice values are persisted in `SessionProfile.Options`, while labels
are translated by the app. Serial stores its COM name in `Host`, leaves `Port`
zero and `Username` empty, and preserves all settings when editing/duplicating.
Its adapter uses [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports/10.0.12)
with a 200 ms read timeout and 2 s write timeout. An in-flight write retains its
serialization gate until it completes or times out. Open cancellation/timeout
observes and releases a late handle; it does not forcibly abort a driver open.

The Telnet wire implementation follows [RFC 854](https://www.rfc-editor.org/rfc/rfc854),
with [NAWS (RFC 1073)](https://www.rfc-editor.org/rfc/rfc1073) and
[terminal type (RFC 1091)](https://www.rfc-editor.org/rfc/rfc1091). Initial option
requests track pending acknowledgements; duplicate positive requests do not
restart negotiation. There are no user-driven option toggles during a session.

## 1. Create the project

```powershell
dotnet new classlib -n SurgeLink.Protocols.Telnet -o src/SurgeLink.Protocols.Telnet -f net10.0
dotnet sln add src/SurgeLink.Protocols.Telnet/SurgeLink.Protocols.Telnet.csproj
```

Reference Core (and any transport library) in the `.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\SurgeLink.Core\SurgeLink.Core.csproj" />
</ItemGroup>
```

If you need a new NuGet package, add its `PackageVersion` to `Directory.Packages.props` and a
version-less `PackageReference` in the project — versions are centrally managed.

## 2. Implement the plugin

```csharp
public sealed class TelnetProtocolPlugin : IProtocolPlugin
{
    public ProtocolDescriptor Descriptor { get; } = new(
        Id: "telnet",
        DisplayName: "Telnet",
        DefaultPort: 23,
        Capabilities: SessionCapabilities.Terminal)
    {
        Glyph = "\uE774",
        Description = "Plain text terminal over TCP.",
        AuthenticatesInTerminal = true,
    };

    public async Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request,
        ProtocolHostContext context,
        CancellationToken cancellationToken)
    {
        // See the real plugin for validation, timeout, cancellation and error mapping.
        var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(request.Host, request.Port == 0 ? 23 : request.Port, cancellationToken);
        return new TelnetSession(client, request.Host, context.CreateLogger<TelnetSession>());
    }
}
```

`ProtocolDescriptor` drives the UI:

| Capability | UI consequence |
|---|---|
| `Terminal` | terminal view + `IRemoteTerminal` required |
| `FileSystem` | Files tab + `IRemoteFileSystem` |
| `Monitor` | Monitor tab + `IRemoteSystemMonitor` |
| `None` | session still opens, shows an "unsupported" state |

## 3. Implement the session

`IProtocolSession` is a lifetime scope: it owns the transport and exposes the capability
handles. Keep it small — one file per capability usually works best.

```csharp
public sealed class TelnetSession : IProtocolSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("n");
    public ProtocolDescriptor Protocol => /* the descriptor above */;
    public ConnectionState State => _state;              // update + raise StateChanged
    public RemoteHostInfo HostInfo => _hostInfo;
    public IRemoteTerminal? Terminal => _terminal;
    public IRemoteFileSystem? FileSystem => null;        // capability not supported
    public IRemoteSystemMonitor? Monitor => null;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? Faulted;
    public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken ct) => Task.FromResult(_hostInfo);
    public ValueTask DisposeAsync() => ...;              // idempotent, closes transport
}
```

Checklist for a well-behaved protocol:

- [ ] `ConnectAsync` throws `ProtocolException` (with a `ProtocolFailureKind`) for expected failures;
      never leaks transport exceptions to the UI.
- [ ] Every background callback that changes state goes through the session's events, not by
      calling UI code.
- [ ] `DisposeAsync` is idempotent and stops all threads/channels.
- [ ] `Terminal.OutputReceived` delivers raw byte chunks from a background thread and is never
      called after disposal.
- [ ] `Terminal.ResizeAsync` propagates the PTY size.
- [ ] Host keys / certificates are verified through `ProtocolHostContext` (see the SSH plugin).
- [ ] Timeouts come from `ConnectionRequest.ConnectTimeout`; keep-alive from `KeepAliveInterval`.

## 4. Wire it up

Add the project reference to `src/SurgeLink.App/SurgeLink.App.csproj` and register the plugin in
`AppServices.Create` (one line):

```csharp
protocols.Register(new TelnetProtocolPlugin());
```

That is the whole change. The protocol appears in the session editor's protocol list, the
sidebar can save profiles for it, and the workspace shows exactly the capability tabs it
supports.

## 5. Test it

- Put format/parsing logic in `static` helpers so they can be unit-tested without a server
  (see `SurgeLink.Protocols.Ssh.Tests`).
- Add a `tests/SurgeLink.Protocols.Telnet.Tests` project for those helpers.
- For UI smoke testing, or when no server is available, write a demo-style plugin like
  `SurgeLink.Protocols.Demo`: it implements all three capabilities with in-memory data, which
  exercises the entire UI surface.

## 6. Prove it conforms

The repository ships a protocol conformance suite (`tests/SurgeLink.Protocols.Conformance`).
Enrolling a protocol is two steps:

```xml
<!-- in tests/SurgeLink.Protocols.Conformance/*.csproj -->
<ProjectReference Include="..\..\src\SurgeLink.Protocols.Telnet\SurgeLink.Protocols.Telnet.csproj" />
```

```csharp
public sealed class TelnetProtocolFixture : ProtocolFixture
{
    public override string Name => "telnet";
    public override IProtocolPlugin Plugin { get; } = new TelnetProtocolPlugin();
}
```

Network protocols must also override `CreateConnectionRequest` with a usable test
endpoint. The actual `TelnetProtocolFixture` starts a loopback server, so CI does
not require a remote Telnet service. Wire-format tests cover fragmented IAC
commands, NVT/BINARY data, terminal type, NAWS and negotiation loop prevention.

The fixture is discovered automatically. The suite verifies descriptor sanity, connect/dispose
lifecycle, capability handles matching the descriptor, terminal read/write/resize/close semantics,
file-system round trips and monitor snapshot ranges. Capabilities the protocol does not declare
produce no test cases, so a terminal-only protocol runs the relevant subset.

## Reference implementation

### Safe file publication

`OpenWriteAsync` is a low-level stream primitive. Application uploads, copies and editor saves
use `RemoteFileWrite.WriteAsync` to stage data inside a unique directory under the destination's
parent. Before opening the payload, it restricts that empty directory to 0700 when permissions
are supported. It flushes and closes the payload, preserves an existing regular file's Unix mode
(or the requested ordinary copy permissions), then calls `CommitWriteAsync`.
Implement `CommitWriteAsync` as an atomic same-filesystem replacement when `overwrite` is true.
When false, it must refuse an existing destination at commit time, including concurrent creation.
Never emulate replacement by deleting the destination first. The default implementation rejects
unsupported commits. Cleanup removes only the staging payload and its empty private directory;
it never recursively deletes a directory or deletes the final destination.

The SSH implementation uses POSIX rename for SFTP replacement and standard no-clobber SFTP
rename for new files. The SCP fallback uses `mv -fT` for replacement and `ln -T` followed by
staging-file removal for no-clobber publication. Servers lacking those commands/options or
hard-link support fail safely. Cancellation during server commit can have an uncertain outcome;
refresh the directory before retrying. No remote protocol can undo an already completed commit.

`src/SurgeLink.Protocols.Demo` is the smallest complete protocol in the repository:

| File | Shows |
|---|---|
| `DemoProtocolPlugin.cs` | descriptor, capability flags, connection error handling |
| `DemoSession.cs` | session lifetime, state events, host info |
| `DemoTerminal.cs` | background output pump, input handling, ANSI output |
| `DemoFileSystem.cs` | `IRemoteFileSystem` over an in-memory tree |
| `DemoMonitor.cs` | `IRemoteSystemMonitor` with synthetic counters |

`src/SurgeLink.Protocols.Ssh` is the production reference: real host key verification, three
authentication methods, a single-round-trip `/proc` probe and SFTP streams with progress.
