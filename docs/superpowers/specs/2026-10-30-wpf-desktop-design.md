# GameTuner Native WPF Desktop Design

Date: 2026-10-30

## Purpose

Replace the loopback HTTP server and browser workspace with a native WPF
desktop application. Preserve the offline, read-only game-analysis contract,
add player-controlled game-process selection, and make live capture available
for every comparison round.

## Product Constraints

- The application remains a single-user, offline Windows 10/11 x64 tool.
- It never launches or closes games, writes game settings, injects into game
  processes, uploads logs, or calls cloud AI or NVIDIA MCP.
- PresentMon is the only game-adjacent child process and tracks only the
  selected game PID. GameTuner stops only PresentMon processes that it starts.
- Every imported round stores the settings that were actually read or entered
  for that round. Later edits never silently rewrite another round.
- Unsupported games and GPUs remain fully usable through manual settings and
  compatible performance logs.

## Architecture

The WPF `MainWindow` is the application foreground and owns native dialogs.
`MainViewModel` continues to own goals, rounds, analysis, saved plans, and share
data. A small desktop session coordinator replaces `WorkspaceApi`: it connects
the view model to local configuration, process selection, and telemetry without
serializing a duplicate HTTP state object on every action.

The startup path creates `AppPaths`, the view model, configuration services,
telemetry, and `MainWindow`, then runs the normal WPF message loop. Closing the
main window stops and disposes owned capture processes. `LocalWebServer`,
browser static assets, URL keys, Origin checks, and browser polling are removed
from the active application after desktop parity is reached.

The implementation reuses the existing analysis, JSON, PNG, hardware, and
PresentMon code. Legacy automatic optimization controllers remain inactive.

## Desktop Interface

The visual direction is a calm professional instrument: warm neutral surfaces,
white panels, dark green primary actions, restrained orange warnings, and red
errors. It uses the existing Bahnschrift/Microsoft YaHei UI/Cascadia Mono font
roles rather than downloading fonts or adding a UI package.

The main window has a minimum size of 960 by 680 and opens at 1180 by 780.

- Header: product name, offline label, selected game/process, and capture state.
- Left workspace: target FPS, quality tier, process picker, log picker, capture
  controls, and current settings.
- Right workspace: average FPS, 1% Low, CPU/GPU values, round comparison,
  recommendation, warnings, save, and share actions.
- Process dialog: searchable process list with executable name, PID, and window
  title; refresh, select, and cancel actions.
- Share view: native preview and PNG export using the existing renderer.

A `ScrollViewer` protects overflow. Body text remains readable, interactive
controls have visible focus and disabled states, tab order follows visual
order, and every mouse action has a keyboard equivalent. Async actions disable
their initiating controls and expose an in-progress label. Motion is limited to
subtle state changes and is not required to understand status.

## Process Selection And Capture

A process service enumerates running processes and returns process name, PID,
and main-window title. Processes that exit during enumeration are skipped.
The dialog initially emphasizes processes with a visible main window while
allowing search across all readable processes. Access-denied metadata is shown
as unavailable rather than aborting enumeration.

Automatic recognition remains a convenience. A manually selected PID becomes
the capture authority until the player clears or changes it. The game name is
editable before the first round and defaults to the selected executable name.
Before each capture starts, the coordinator verifies that the PID still exists.

Each comparison round has an explicit capture lifecycle:

1. Select or confirm the game process.
2. Start a new PresentMon capture with a unique output path.
3. Play, then stop and analyze the current capture.
4. Consume that path once, clear it from the pending-capture slot, and retain
   the immutable result in the round.
5. Start another new capture for the next round without resetting prior rounds.

Selecting an external log remains available. Importing an external log stops an
active owned capture before analysis so no abandoned child process continues
in the background. Duplicate content is still rejected.

## Graphics-Settings Strategy

There is no supported Windows API that can read meaningful graphics settings
from every game. NVIDIA MCP support is a per-game integration, not a universal
settings interface. GameTuner therefore uses three honest levels of support.

### 1. Native Read-Only Adapters

Keep the RDR2 `system.xml` and 007 First Light registry readers. Each adapter
declares which fields it understands and reports its source. New games are
added individually only when their storage format and field meanings are
known. These adapters work independently of GPU vendor.

### 2. Player-Selected Structured Configuration

When no adapter exists, the player may choose a local JSON, XML, or INI file.
The reader imports only unambiguous scalar fields and preserves unknown raw
values with their original names. It does not invent friendly labels, quality
tiers, or menu meanings. Selecting a configuration file is optional and never
grants write access to GameTuner.

### 3. Manual Round Snapshot

The player can always enter the actual menu settings. This is the universal
fallback and works with every GPU vendor. The UI clearly distinguishes
adapter-read, file-imported, and player-entered values. Missing values remain
unknown and are excluded from evidence-dependent advice such as ray tracing.

Screenshot OCR, memory reading, injection, and a speculative universal plugin
framework are outside this iteration.

## Error Handling

- An exited or inaccessible selected process produces a recovery message and
  reopens process selection; it never starts another game process.
- PresentMon startup or finalization failure leaves existing rounds intact and
  keeps manual log import available.
- Unsupported or malformed logs and configuration files do not create rounds.
- Save and export use native dialogs and report failures beside the relevant
  action. No nested browser busy-state handoff remains.
- Closing the window during capture stops only the owned PresentMon session and
  does not discard previously saved plans.

## Required Bug Fixes During Migration

- Allow fresh live captures after the first round and never return an already
  consumed capture as a new round.
- Preserve positive frame times longer than ten seconds in average and 1% Low
  calculations, or fail with an explicit coverage error instead of silently
  dropping them.
- Pass the updated graphics settings back into recommendation generation so
  ray-tracing advice remains consistent after editing round details.

## Verification

Use test-driven development for behavior changes. Tests first reproduce the
bug or missing behavior, are observed failing for the expected reason, and then
pass against the minimal implementation.

- Core: long-frame accounting, recommendation updates, and all existing parser
  and planner cases.
- Process: enumeration filtering, metadata failures, manual PID authority, and
  exited-process recovery using fakes only at the operating-system boundary.
- Capture: two consecutive rounds use different paths, consumed paths are not
  reused, external import stops active capture, and no game is launched.
- Desktop: real view-model command tests, native window smoke tests, isolated
  save/load and PNG export, keyboard reachability, and WPF-rendered screenshots
  at 1180x780 and 960x680.
- Regression: run `tests/verify-ui.ps1` after updating its browser-specific
  checks to desktop checks. The final package remains self-contained and loads
  only its bundled PresentMon.

Tests use temporary data roots and do not launch games, modify real game
profiles, or call cloud APIs. Opt-in hardware integrations are reported
separately from verified offline behavior.

## Success Criteria

- A player can select any readable running game process, capture and analyze at
  least two distinct rounds, save/reload the plan, and export a PNG without
  opening a browser.
- A player with an unsupported game can still complete the same workflow using
  manual settings or a player-selected structured configuration file.
- All existing offline tests and new desktop regression tests pass, and the
  rendered desktop layouts show no clipped primary actions or unreadable text.
