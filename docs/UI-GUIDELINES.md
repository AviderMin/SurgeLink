# UI guidelines (WinUI 3 + Reactor)

The app must look and feel like a first-party Windows 11 application. These rules are enforced
in review.

## Component model

- A view is a Reactor `Component` (or `Component<TProps>`) whose `Render()` returns an element
  tree. **No XAML views, no bindings, no MVVM.** The vendored native scrollbar
  template in `Styling/WindowsTerminalScrollBar.xaml` is a control-template exception:
  it ports Windows Terminal's WinUI styling while keeping interaction in the native control.
- Props are immutable `record`s. A new state change produces new props; equality decides whether
  a subtree re-renders.
- Hooks (`UseState`, `UseReducer`, `UseEffect`, `UseMemo`, `UseExternalStore`…) must be called
  unconditionally and in the same order every render. Never inside `if`/`for`.
- Dynamic lists need stable keys: `.WithKey(item.Id)`, otherwise reconciliation remounts rows.
- Application state lives in `WorkspaceStore` / `IAppSettingsStore` and is read with
  `UseExternalStore(store.Subscribe, () => store.Snapshot)`. Local, ephemeral state uses
  `UseState`/`UseReducer`.
- Split files at roughly 300 lines. A view is a composition of small render helpers or small
  components, not one giant `Render()`.

## Layout and spacing

- Work on the 4 px grid: `AppTheme.SpacingXs = 4`, `S = 8`, `M = 12`, `L = 16`, `XL = 24`.
- Standard page padding is 24; card padding 16; compact toolbar padding 12.
- Use `Grid` for the window skeleton, `FlexRow`/`FlexColumn` for linear layout, and
  `ScrollView`/`ListView` for content that can overflow. Avoid nested `ScrollViewer`s.
- Corner radii are in device-independent pixels (DIP): use `AppTheme.CardRadius`
  (8) for standalone cards, and `AppTheme.ControlRadius` (4) for custom controls.
  Reactor `Card(...)` already supplies the canonical 8 DIP radius.
- Keep native buttons, inputs, list rows, `ContentDialog`, `MenuFlyout` and tooltips
  on their WinUI templates: controls use 4 DIP; dialogs/flyouts use 8 DIP; tooltips
  use 4 DIP. Do not override all controls to a single radius.
- Window outlines belong to Windows/DWM. A maximized or snapped window has square
  corners; do not simulate a window outline by rounding the root layout.
- Flush sidebar/status-bar edges, dividers, chart canvases and adjoining surfaces
  may remain square. Round detached cards, not every layout container.
- For custom progress bars, draw a continuous rounded track behind the fill;
  avoid joining two independently rounded segments that expose a seam.

Geometry reference: [Windows geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry).
- Never hard-code window sizes inside views; the shell owns window geometry.

## Typography

Use the WinUI type ramp factories instead of setting sizes by hand:
`Display`, `TitleLarge`, `Title`, `Subtitle`, `BodyLarge`, `Body`, `BodyStrong`, `Caption` —
optionally with `HeadingLevel(...)` so screen readers get a proper structure.

Monospace text uses `AppTheme.Monospace` (Cascadia Mono / Consolas fallback).

## Colour and theme

- Use `Theme.*` brushes (`Theme.PrimaryText`, `Theme.SecondaryText`, `Theme.CardBackground`,
  `Theme.CardStroke`, `Theme.LayerFill`, `Theme.Accent`, `Theme.DividerStroke`…) — never literal
  hex strings for chrome. The only exception is the terminal palette, which is user data.
- The app supports light, dark and High Contrast automatically when tokens are used.
- Respect the user's theme setting (`AppSettings.Theme`); it is applied at the window root.
- Separators are `Theme.DividerStroke` at 1 px; avoid heavy borders.

## Interaction

- Every interactive control has an `AutomationName` (and an `AutomationId` for shell regions);
  the verification harness and screen readers depend on it.
- Buttons are verbs ("Connect", "Upload"), state changes are announced.
- Provide keyboard access for everything reachable by mouse: Tab order, access keys on menus,
  `Ctrl+…` accelerators for global actions.
- Destructive actions ask for confirmation and explain the consequence in the dialog body.
- Long operations show progress and stay cancellable; failures surface as `InfoBar` with a retry
  path, not as blocking dialogs.
- Empty states explain what the user can do next and offer the primary action inline.

## Performance

- Repaint on data changes, not on every frame. The terminal uses a coalesced `Invalidated`
  event and paints only visible rows.
- Do not create one UI element per data cell/item when the count is unbounded; pool visuals or
  virtualise (`ItemsRepeater`, `VirtualList`, `ListView`).
- Keep `Render()` cheap: precompute with `UseMemo`, keep element trees under a few hundred nodes
  for interactive surfaces.
- Observe `SizeChanged` rather than polling layout.

## Localization (zh-CN / en-US)

- Every user-visible string is written as `Str.T("area.key", "English text")` (or `Str.F` when it has
  placeholders). The English copy stays at the call site; the Chinese copy lives in
  `Localization/Zh/ZhShell.cs`, `ZhData.cs` (SFTP/monitor) or `ZhTerminal.cs` (terminal).
- Never build user text with `string.Format` using a literal — use `Str.F("...", "{0} items", n)` so
  Chinese can reorder placeholders.
- Do not localize: `AutomationId`, protocol ids, host names, file names/paths, units
  (B/KB/MB/GB, %, MB/s), log lines.
- `AutomationName` may be localized (it is read by screen readers); harnesses match on
  `AutomationId` when a stable hook is needed.
- The UI language follows `AppSettings.Language` (System/English/ChineseSimplified). A change bumps
  `LocalizationService.Revision`; the shell remounts its tree with `.WithKey("locale-" + revision)`
  so every string is re-evaluated without restarting the app or dropping sessions.

## Error handling

- Expected failures (bad credentials, missing permissions, unavailable capability) are presented
  as user language, not stack traces.
- A view whose capability is `null` (protocol does not support it) renders an explanatory empty
  state — it must never crash.
- All async work in a view cancels on unmount.
