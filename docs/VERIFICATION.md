# Building and verifying SurgeLink

## Build

```powershell
# whole solution
dotnet build SurgeLink.sln -p:Platform=x64

# app only (the usual inner loop)
dotnet build src/SurgeLink.App/SurgeLink.App.csproj -c Debug -p:Platform=x64
```

The app is unpackaged (`WindowsPackageType=None`), so there is no MSIX deployment step: the
executable runs straight from `bin`. The Windows App SDK bootstrapper loads the installed
Windows App Runtime 2.x automatically.

## Tests

```powershell
dotnet test tests/SurgeLink.Terminal.Tests              # emulator (273 tests)
dotnet test tests/SurgeLink.Protocols.Ssh.Tests         # /proc, df, ps and Windows CIM parsers (305 tests)
dotnet test tests/SurgeLink.Core.Tests                  # stores, catalog, session manager (95 tests)
dotnet test tests/SurgeLink.Protocols.Conformance       # protocol conformance kit, Demo fixture (30 tests)
```

The full suite is offline and runs in a few seconds.

## Isolated builds (parallel agents / CI)

```powershell
& ".\tools\agent-build.ps1" -Project src/SurgeLink.App -Agent my-agent
& ".\tools\agent-build.ps1" -Project tests/SurgeLink.Terminal.Tests -Agent my-agent -Test
```

Builds are written to `artifacts/agent-build/<agent>` so several workstreams can build the same
project concurrently without fighting over `obj/`.

## Running the app and checking it headlessly

Nobody on this project can look at screenshots, so verification is done through
**Windows UI Automation** plus pixel statistics.

```powershell
# one-command end-to-end check: build, launch, drive the demo session through
# Terminal/Files/Monitor, open the session editor and assert on the UIA tree
& ".\tools\verify-app.ps1"

# build, launch, dump the control tree + PNG, then stop
& ".\tools\run-app.ps1" -Tag baseline

# work against a running instance
& ".\tools\uia-action.ps1" -Name "Open demo session" -Type Button
& ".\tools\uia-action.ps1" -Name "Filter sessions" -SetValue -Value "prod"

# dump the tree of whatever is running
$p = Get-Process SurgeLink | Select-Object -First 1
& ".\tools\dump-uia.ps1" -ProcessId $p.Id -MaxDepth 10 | Out-File artifacts/uia.txt

# pixel statistics of a captured window (colour count, region samples)
& ".\tools\analyze-image.ps1" -Path artifacts/ui-baseline.png
```

**What good looks like**

- `dump-uia.ps1` output contains the expected `ControlType.*` nodes with the expected names
  (window title, tab headers, toolbar buttons, card titles, list rows…).
- No `ControlType.Text name='Render error: ...'` node — that is Reactor's fallback when a
  component throws during render (the most common UI regression in this project).
- Terminal content appears as text inside the terminal surface's automation peer.

**Localization**

`tools/verify-app.ps1` runs two phases against the same build: it forces `settings.json` to
`{"language":1}` (English) and drives the demo session end to end, then relaunches with
`{"language":2}` (Chinese) and asserts Chinese text is present in the accessibility tree
(no "Render error", key wording such as 会话/设置/新建). Afterwards it restores
`{"language":0}` so a manual run follows the Windows UI language again.

**End-to-end smoke test (no SSH server required)**

1. Launch the app.
2. Invoke **Open demo session** — a tab opens, the title bar shows `demo-host`, the status bar
   shows *Connected*.
3. The terminal shows the ASCII banner; switch to **Files** (in-memory tree) and **Monitor**
   (synthetic metrics).
4. Dump the tree and assert on those strings.

Verification runs are safe to do in parallel: the run scripts only stop instances launched from
their own artifacts folder (`artifacts/agent-build/<agent>`), and `uia-action.ps1` targets the
first running instance.

## Verifying real SSH

The development machine has no SSH server and no admin rights to install one, so the SSH
transport is covered by:

- unit tests for every parser and path helper (`tests/SurgeLink.Protocols.Ssh.Tests`),
- manual verification against any reachable host — set up a profile in the session editor
  (password or private key) and check terminal, SFTP and monitor.

When testing host key handling, switch the server's key and confirm the app shows the
"host key changed" warning instead of connecting silently.

## Logs and on-disk state

```
%LOCALAPPDATA%\SurgeLink\         sessions.json, credentials.json, known-hosts.json, settings.json
%LOCALAPPDATA%\SurgeLink\logs\    one log file per day
```

Deleting the folder resets the app completely (including saved credentials and trusted keys).