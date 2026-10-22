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
| **Files (SFTP / SCP fallback)** | Remote browser: breadcrumb navigation, sortable listing with permissions, upload/download with progress and cancel, rename, delete, new folder, permission editor, hidden-file toggle. Right-click to cut, copy, paste and inspect properties; Ctrl+X/C/V works within the current file view. Files and folders can be copied between remote folders; existing destinations are rejected and symbolic links are not followed when copying. Uses SFTP when available; falls back to SSH commands + SCP on servers without an SFTP subsystem. |
| **Monitor** | Remote dashboard: CPU gauge + per-core bars + history, memory/swap, load, uptime, disks, network throughput with sparklines, sortable top processes, pause/resume |
| **Protocols** | SSH (password, key, keyboard-interactive; TOFU host keys; Linux `/proc` **and Windows OpenSSH** monitoring) plus an offline **Demo** protocol for testing and as the reference implementation |
| **Shell** | Session sidebar with grouping/search/context menu, tabbed workspaces, capability-driven view switcher, settings, host-key/secret dialogs, crash log, light/dark/High Contrast, encrypted credential storage |
| **界面语言** | 简体中文 / English / 跟随系统（默认跟随 Windows；设置对话框可随时切换，无需重启） |

The UI is localized: every user-visible string goes through `Str.T("key", "English")` with the
Chinese copy in `Localization/Zh`. Switching the language in the Settings dialog rebuilds the shell immediately.

The app is deliberately **protocol agnostic**: the shell only talks to `IProtocolSession`
capabilities, so adding Telnet/RDP/Serial/VNC later means adding one project and one
registration line. See [docs/ADDING-A-PROTOCOL.md](docs/ADDING-A-PROTOCOL.md).

## Requirements

- Packaged release: Windows 11 22H2 (build 22621) or later, x64
- .NET 10 SDK
- The packaged release includes .NET and Windows App SDK; development builds require the matching Windows App Runtime.

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
  SurgeLink.Protocols.Ssh/     SSH implementation (shell channel, SFTP with SCP fallback, Linux + Windows monitor probes)
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

## Release and security

- [Packaging and Microsoft Store submission](packaging/README.md)
- [Privacy policy](PRIVACY.md)
- [Security and repository hygiene](SECURITY.md)
- [Pre-release source review](docs/RELEASE-REVIEW.md)
