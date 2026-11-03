# Offline, Game-Agnostic Log Analysis

Decision date: 2026-10-28.

## Decision

The active product is an offline native WPF Windows application with
player-controlled graphics adjustments. Reuse local C# analysis, JSON records and
native PNG rendering without an HTTP server or browser. No accounts, cloud,
external API keys, database or NVIDIA MCP connection are needed.

The player explicitly selects a running process for read-only configuration and
PresentMon capture. Native dialogs import external logs and scalar JSON/XML/INI
settings; each capture is explicitly started and stopped for analysis. WPF also
provides the main window, process picker, keyboard commands and share-card preview.

## Product Contract

- Any game name can be entered. Compatibility is with supported log formats,
  not a hard-coded game list or NVIDIA's optimization catalog.
- Initially support NVIDIA performance CSV, CSV with MsBetweenPresents or
  FrameTime columns, and RDR2 Benchmark summaries as an optional adapter.
- Generic advice uses effect categories and relative quality tiers. Never invent
  a game's menu labels, current settings, hardware or exact performance gains.
- The player sets a target, imports independent logs, changes settings manually
  and imports subsequent logs. There is no fixed round count.
- Read hardware and supported game settings locally, or accept explicit player
  details for unsupported games, and snapshot the actual values with each log.
  Later edits must not silently relabel an older measurement.
- Average FPS and a usable low-FPS measurement must meet the target before
  declaring success. A P99-equivalent summary is not precise 1% Low.
- Once the target is met, offer to stop and save that tested configuration.
  Do not call it the global optimum. Start a new comparison to change the goal.
- Save an immutable local plan record and a locally generated PNG share card.
  Saving never writes the game's configuration.
- Use directly visible native controls for common game, FPS, round and export
  actions; keep the quality-tier choice in the existing native selector.

## Safety And Validation

The active application composition root must not construct game controllers,
write-capable configuration adapters, process guards, MCP clients or cloud advisors. Legacy
automation code remains only as an inactive reference, not part of the UI flow.
The desktop configuration providers may use read-only Windows, registry and
game-file inspection to populate actual settings; they must not launch games or
modify those sources. Player-selected structured settings imports are also read-only.

Test the actual parser, real log files, native coordinator, real WPF modal and
keyboard controls, JSON round trips and PNG exports. Verify fresh self-contained
packages retain bundled PresentMon and contain no browser assets. Preserve existing user data. Share cards contain explicitly entered
hardware/settings and measurements, not private source paths or API keys.

## Compatibility Limits

Game-agnostic log analysis does not guarantee every game or recording tool can
produce a compatible log. NVIDIA app's statistics overlay and optimal playable
settings are separate documented features. Do not treat optimization-list
membership as proof of a working capture, and do not promise universal capture.
Unsupported files must fail clearly without adding a fake round.
