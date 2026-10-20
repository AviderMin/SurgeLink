# Adding a protocol

SurgeLink is built so that a new protocol is **one project + one registration line**. The shell
never learns about protocols; it only consumes capabilities.

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
        id: "telnet",
        displayName: "Telnet",
        defaultPort: 23,
        capabilities: SessionCapabilities.Terminal)
    {
        Glyph = "\uE774",
        Description = "Plain text terminal over TCP.",
    };

    public async Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request,
        ProtocolHostContext context,
        CancellationToken cancellationToken)
    {
        // build the transport, then return a session
        return new TelnetSession(request, context.CreateLogger<TelnetSession>());
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

The fixture is discovered automatically. The suite verifies descriptor sanity, connect/dispose
lifecycle, capability handles matching the descriptor, terminal read/write/resize/close semantics,
file-system round trips and monitor snapshot ranges. Capabilities the protocol does not declare
produce no test cases, so a terminal-only protocol runs the relevant subset.

## Reference implementation

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