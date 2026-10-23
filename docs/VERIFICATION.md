# Building and verifying SurgeLink

## Build

```powershell
# whole solution
dotnet build SurgeLink.slnx -p:Platform=x64

# app only (the usual inner loop)
dotnet build src/SurgeLink.App/SurgeLink.App.csproj -c Debug -p:Platform=x64
```

The app is unpackaged (`WindowsPackageType=None`), so there is no MSIX deployment step: the
executable runs straight from `bin`. The Windows App SDK bootstrapper loads the installed
Windows App Runtime 2.x automatically.

## Tests

```powershell
dotnet test tests/SurgeLink.Terminal.Tests              # emulator
dotnet test tests/SurgeLink.Protocols.Ssh.Tests         # /proc, df, ps and Windows CIM parsers
dotnet test tests/SurgeLink.Core.Tests                  # stores, catalog, session manager
dotnet test tests/SurgeLink.Protocols.Conformance       # protocol conformance kit, Demo fixture
dotnet test tests/SurgeLink.Protocols.Telnet.Tests      # Telnet codec and loopback transport
dotnet test tests/SurgeLink.Protocols.Serial.Tests      # serial options and fake-device lifecycle
```

The full suite is offline; use actual test output for current counts.
Use `dotnet test SurgeLink.slnx -c Release -p:Platform=x64 -m:1` to run the entire
suite sequentially. Performance smoke tests have a 200 ms budget, so avoid running
builds or live monitoring sessions at the same time when measuring them.

Static repository and UI text checks:

```powershell
python tools/check-repository.py
python tools/check-ui-localization.py
dotnet list SurgeLink.slnx package --vulnerable --include-transitive
```

The UI text check covers literal `Str.T` / `Str.F` keys, duplicate Chinese keys and
format-placeholder parity. Dynamic keys, layout, keyboard access and theme contrast
still require runtime checks. The dependency vulnerability check requires network access.

Release tooling regression checks:

```powershell
python -m unittest discover -s tools/tests -p test_release_notes.py -v
pwsh -NoProfile -File tools/tests/test-release-tag.ps1
```

## Isolated builds (parallel agents / CI)

```powershell
& ".\tools\agent-build.ps1" -Project src/SurgeLink.App -Agent my-agent
& ".\tools\agent-build.ps1" -Project tests/SurgeLink.Terminal.Tests -Agent my-agent -Test
```

Builds are written to `artifacts/agent-build/<agent>` so several workstreams can build the same
project concurrently without fighting over `obj/`.

## Running the app and checking it headlessly

The scripts use **Windows UI Automation** and pixel statistics. Inspect captured
screenshots as well when checking visual layout.

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

# SFTP context-menu check: real right clicks, then assertions on the flyout items
& ".\tools\verify-sftp-menu.ps1"

# pixel statistics of a captured window (colour count, region samples)
& ".\tools\analyze-image.ps1" -Path artifacts/ui-baseline.png
```

A context menu is a native flyout and never shows up in a plain tree dump, so
`verify-sftp-menu.ps1` drives actual right clicks and then asserts the MenuItem
nodes. It covers a file row, the blank space below the last row and an empty
folder (where "Paste into this folder" has to be reachable).

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
the user's original settings bytes, or removes the temporary settings file if none existed.

**End-to-end smoke test (no SSH server required)**

1. Launch the app.
2. Invoke **Open demo session** — a tab opens, the title bar shows `demo-host`, the status bar
   shows *Connected*.
3. The terminal shows the ASCII banner; switch to **Files** (in-memory tree) and **Monitor**
   (synthetic metrics).
4. Dump the tree and assert on those strings.

Use isolated build folders and avoid concurrent UI runs: `uia-action.ps1` targets the
first running instance, and the localization smoke test changes local settings.

## Terminal scrollbar parity

The terminal ports Windows Terminal's scrollbar customizations onto the WinUI 3
template matching `Microsoft.WindowsAppSDK.WinUI 2.1.0`: 16 DIP width, zero expansion
delay, square track and the system's rounded thumb. The resource is embedded as
`Styling/WindowsTerminalScrollBar.xaml`, with its MIT notice shipped under `Licenses/`.
When upgrading WinUI, compare the template with the new SDK and Windows Terminal's
`src/cascadia/TerminalControl/TermControl.xaml` before updating the fork.

With enough terminal output to have scrollback, check the compact indicator, hover
over the scrollbar while focus is elsewhere, move back into the terminal, drag the
thumb, drag outside and release, and then re-enter. Expansion and contraction must
be owned by the native ScrollBar, without terminal-wide opacity or visual-state
overrides. Repeat with light and dark terminal palettes; the scrollbar theme follows
the terminal background independently of the shell theme. Also check the OS's
"Always show scrollbars" setting and high contrast on a suitable test machine.

## Verifying real SSH

Offline tests do not establish real SSH transport compatibility. Verify with:

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
## Windows 10 compatibility

Starting with mainline version 1.0.2.15, the minimum OS version is Windows 10 21H2
(build 19044) in the project, APPX manifest and Inno Setup installer. This merges
the compat-win10 branch's 1.0.2.14 configuration into master. The build still targets
SDK 22621; that SDK target does not set the minimum OS version.
Main-window Mica and editor-window MicaAlt are disabled on Windows 10.

Windows 10 21H2 execution remains pending; lowering the installation minimum does
not establish runtime compatibility. ARM64 requires testing on ARM64 hardware.

On a fully patched Windows 10 21H2 x64 machine, install the EXE or extract the
APPX distribution ZIP and run Install.ps1. Verify installation and launch,
light/dark/high-contrast backgrounds, title bar and resizing, an offline Demo
session across Terminal/Files/Monitor, file editor opening, upload/download
pickers, and a real SSH connection with file transfer. Check startup/crash logs.
Record the exact OS edition/build and results before claiming verified support.
