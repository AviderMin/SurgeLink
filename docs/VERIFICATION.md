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
dotnet test tests/SurgeLink.Agent.Tests                 # gateway, IPC, CLI and MCP contracts
```

The full suite is offline; use actual test output for current counts.
The SCP rename command test uses Git Bash on Windows or `/bin/sh` on Unix and skips if that
shell is unavailable. It checks existing-file/directory refusal and quoted path handling.
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

## Terminal search

Search is exercised through the normal UI Automation smoke test once the demo session is open:

1. Focus the terminal and press **Ctrl+Shift+F** (or use the context-menu **Find…** entry).
2. The find bar appears inside the terminal; type `SurgeLink` and check that the banner match is
   highlighted and the counter reads a concrete `1 of 1` position.
3. Press **Enter** repeatedly to wrap through matches; **Shift+Enter** goes backwards, **F3** and
   **Shift+F3** work while the terminal has focus. The view scrolls to an off-screen match.
4. Toggle **Aa** and confirm the match set changes for case-sensitive queries.
5. Press **Esc**; the find bar closes, highlights disappear, and terminal input resumes without
   sending the keystrokes to the shell.

Also check a long scrollback: run a command that produces many lines, search for an old line,
and confirm the viewport scrolls back to it while new output continues to arrive.

## Terminal resize and wide-character editing

- Type a long line, narrow the window and widen it again: the original tail is restored rather
  than permanently truncated. Repeat with scrollback lines visible above the viewport.
- Print `A中B`, return to the first column and send **CSI 2 P** (Delete characters). Every wide
  glyph must keep both halves together; the same invariant applies after CSI @, CSI X and CSI P
  on lines containing Chinese, Japanese or emoji characters.
- Narrow the window until a wide glyph is cut at the right edge, then widen it back: no orphan
  continuation cell may appear. Partially widening to a width where the next wide glyph would
  need a hidden continuation must render a blank at that edge, not a lone Wide cell.
- Narrow a line, erase to the end of the line, then widen it back: the erased text must not
  reappear from hidden columns. Repeat after printing, inserting or deleting characters on the
  active row; a partially visible wide glyph must be cleaned before the edit shifts cells.

## Tab activity and SSH advanced settings

- Open the demo twice, type a command in one tab, switch to the other: the background tab shows
  the activity dot; activating it clears the dot. Resizing the window or changing terminal
  settings must not show the dot.
- Open the session editor for SSH: the **Connection settings** section shows output encoding,
  terminal type, legacy algorithms, preferred key exchange and command timeout. Defaults must
  remain valid; an out-of-range timeout is rejected inline.

## Closing with blocked input

- With a slow or blocked terminal write, close the tab and confirm it completes without waiting for
  the remote transport to independently release the write. The transport's dispose path must run,
  and repeated close requests must share one completion rather than disposing it twice.
- Repeat while an Agent terminal operation is queued or in flight; cancellation must not leave a
  stuck close operation behind.

## Large directory copy

- Copy a flat directory containing at least 100 small files into a new destination. The destination
  parent should be listed once for the batch, not once per file; a single-path existence/stat check
  is the expected per-file probe. Existing destinations must still fail before commit.
- Repeat with nested and empty directories and verify permissions, bytes and empty directories are
  preserved.

## Verifying real SSH

Offline tests do not establish real SSH transport compatibility. Verify with:

- unit tests for every parser and path helper (`tests/SurgeLink.Protocols.Ssh.Tests`),
- manual verification against any reachable host — set up a profile in the session editor
  (password or private key) and check terminal, SFTP and monitor.

When testing host key handling, switch the server's key and confirm the app shows the
"host key changed" warning instead of connecting silently.

A repeatable real-SSH matrix should cover: password/key/keyboard-interactive login, host-key
change and rejection, terminal resize and TERM propagation, SFTP listing and upload/download,
SCP fallback when no SFTP subsystem is available, transfer cancellation, overwrite conflicts,
and reconnect after a dropped connection. Record the server implementation/version, network
latency and exact result; offline tests cannot prove these transport paths.

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
