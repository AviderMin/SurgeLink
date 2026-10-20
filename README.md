# SurgeLink

A MobaXterm-style **multi-protocol terminal for Windows**, built with **WinUI 3** and
[**Microsoft.UI.Reactor**](https://github.com/microsoft/microsoft-ui-reactor) — the UI is
plain C# (no XAML), reconciled onto real WinUI 3 controls.

![SurgeLink main window](docs/images/main-window.png)

| Files (SFTP) | Remote monitor |
|---|---|
| ![SFTP file manager](docs/images/files-view.png) | ![Remote monitor dashboard](docs/images/monitor-view.png) |

## What it does

| Area | Status |
|---|---|
| **Terminal** | Own VT100/xterm engine: UTF-8, 16/256/true colour, bold/italic/underline/inverse/strike, wide CJK, scrollback, selection, copy/paste, resize, block/bar/underline cursor, 6 615-cell screen rendered with ~47 UI elements |
| **Files (SFTP)** | Remote browser: breadcrumb navigation, sortable listing with permissions, upload/download with progress and cancel, rename, delete, new folder, permission editor, hidden-file toggle |
| **Monitor** | Remote dashboard: CPU gauge + per-core bars + history, memory/swap, load, uptime, disks, network throughput with sparklines, sortable top processes, pause/resume |
| **Protocols** | SSH (password, key, keyboard-interactive; TOFU host keys; Linux `/proc` **and Windows OpenSSH** monitoring) plus an offline **Demo** protocol for testing and as the reference implementation |
| **Shell** | Session sidebar with grouping/search/context menu, tabbed workspaces, capability-driven view switcher, settings, host-key/secret dialogs, crash log, light/dark/High Contrast, encrypted credential storage |

The app is deliberately **protocol agnostic**: the shell only talks to `IProtocolSession`
capabilities, so adding Telnet/RDP/Serial/VNC later means adding one project and one
registration line. See [docs/ADDING-A-PROTOCOL.md](docs/ADDING-A-PROTOCOL.md).

## Requirements

- Windows 10 1809+ (Windows 11 recommended — Mica backdrop, WinUI 3 controls)
- .NET 10 SDK
- Windows App Runtime 2.x (installed with the app by the SDK bootstrapper)

## Build and run

```powershell
dotnet build SurgeLink.slnx -c Debug -p:Platform=x64
.\src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe
```

No SSH server needed to explore the app: click **Open demo session** and use the fake shell
(`help`, `ls`, `colors`), the in-memory SFTP browser and the synthetic monitor.

## Verify

```powershell
dotnet test                                        # 703 tests across 4 suites
& ".\tools\verify-app.ps1"                        # build + tests + headless UI end-to-end check
```

`verify-app.ps1` launches the app, drives it through Windows UI Automation (demo session →
terminal → files → monitor → session editor) and asserts on the accessibility tree, so it also
guards accessibility names. Current status: **25/25 checks pass**.

## Repository layout

```
src/
  SurgeLink.Core/              protocol-agnostic contracts: sessions, terminal/files/monitor
                               capabilities, profiles, credential + host key stores, session manager
  SurgeLink.Terminal/          VT/xterm emulation engine (parser, screen buffer, scrollback, input)
  SurgeLink.Protocols.Ssh/     SSH implementation (shell channel, SFTP, Linux + Windows monitor probes)
  SurgeLink.Protocols.Demo/    offline reference protocol (fake shell, in-memory files, synthetic metrics)
  SurgeLink.App/               WinUI 3 + Reactor application shell and views
tests/
  SurgeLink.Terminal.Tests/          273 engine tests
  SurgeLink.Protocols.Ssh.Tests/     305 parser/probe/option tests
  SurgeLink.Core.Tests/               95 store/catalog/session-manager tests
  SurgeLink.Protocols.Conformance/    30-test protocol conformance kit (Demo fixture)
tools/                         build, run, UI-automation and image-analysis helpers
docs/                          architecture, protocol guide, UI guidelines, verification
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — layers, dependency rules, data flow, threading model
- [Adding a protocol](docs/ADDING-A-PROTOCOL.md) — step-by-step guide, worked example, conformance enrollment
- [UI guidelines](docs/UI-GUIDELINES.md) — WinUI 3 + Reactor rules used across the app
- [Verification](docs/VERIFICATION.md) — how the app is built, run and verified (including headless UI checks)

## Development conventions

- `Directory.Packages.props` centralises package versions; `Directory.Build.props` centralises analyzers and nullability.
- No XAML, no MVVM frameworks, no third-party UI toolkits: React components + hooks only.
- Every project builds standalone; `dotnet test` covers the protocol-independent logic.
- Multi-agent build isolation: `tools/agent-build.ps1 -Agent <name>` writes to `artifacts/agent-build/<name>`.

## License

See [LICENSE](LICENSE).
