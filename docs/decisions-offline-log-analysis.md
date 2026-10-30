# Offline, Game-Agnostic Log Analysis

Decision date: 2026-10-28.

## Decision

The user selected an offline Windows desktop application with player-controlled
graphics adjustments. Reuse the existing WPF application, local C# analysis,
JSON records and native PNG rendering. No accounts, cloud, API keys, database,
server or NVIDIA MCP connection are needed.

Alternatives considered: a local browser UI adds a server/packaging layer; a new
desktop framework adds migration work without improving the read-only workflow.
Reuse is the simplest maintainable choice.

## Product Contract

- Any game name can be entered. Compatibility is with supported log formats,
  not a hard-coded game list or NVIDIA's optimization catalog.
- Initially support NVIDIA performance CSV, CSV with MsBetweenPresents or
  FrameTime columns, and RDR2 Benchmark summaries as an optional adapter.
- Generic advice uses effect categories and relative quality tiers. Never invent
  a game's menu labels, current settings, hardware or exact performance gains.
- The player sets a target, imports independent logs, changes settings manually
  and imports subsequent logs. There is no fixed round count.
- Record the actual player-entered graphics and hardware details with each log.
  Later edits must not silently relabel an older measurement.
- Average FPS and a usable low-FPS measurement must meet the target before
  declaring success. A P99-equivalent summary is not precise 1% Low.
- Once the target is met, offer to stop and save that tested configuration.
  Do not call it the global optimum. Start a new comparison to change the goal.
- Save an immutable local plan record and a locally generated PNG share card.
  Saving never writes the game's configuration.
- Use directly visible buttons for common game, tier, FPS, round and export
  actions, rather than dropdown lists.

## Safety And Validation

The active application composition root must not construct game controllers,
configuration adapters, process guards, MCP clients or cloud advisors. Legacy
automation code remains only as an inactive reference, not part of the UI flow.

Test the actual parser, real log files, WPF controls, JSON round trips and PNG
exports. Preserve existing user data. Share cards contain explicitly entered
hardware/settings and measurements, not private source paths or API keys.

## Compatibility Limits

Game-agnostic log analysis does not guarantee every game or recording tool can
produce a compatible log. NVIDIA app's statistics overlay and optimal playable
settings are separate documented features. Do not treat optimization-list
membership as proof of a working capture, and do not promise universal capture.
Unsupported files must fail clearly without adding a fake round.
