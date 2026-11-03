@C:\Users\Lycoris\.codex\RTK.md

## Verification

| Rule | Enforcement |
|---|---|
| PresentMon capture needs an elevated process; the manifest must keep `requireAdministrator`. A plain run fails with "access denied" and writes no log, which reads as "no game frame". | `Elevation_is_requested_so_presentMon_can_start_a_trace_session` |
| Every brush `App.xaml.cs` swaps at runtime must be reached through `DynamicResource`. A `StaticResource` brush keeps its light colour and leaks into dark mode. | `Theme_brushes_are_never_referenced_statically_so_dark_mode_has_no_leak` |
| Run tests and inspect native WPF renders before delivering UI changes. | `tests/verify-ui.ps1` + `tests/GameTuner.UiTests/DesktopWindowTests.cs` |
| Never limit a comparison to a fixed number of logs. | `Sixth_log_and_round_selection_keep_their_actual_configuration` |
| Only actual recorded values and explicitly entered settings belong on share cards. | JSON round-trip and PNG tests in `NativeWorkspaceTests.cs` |
| Release packages must be self-contained and load only the bundled PresentMon, never RTSS/NVIDIA App installations. | `PresentMon_monitor_starts_and_stops_cleanly_without_launching_a_game` + `scripts/publish.ps1` |

UI tests must exercise the native coordinator, real WPF windows, dialogs and
keyboard bindings. Inspect actual native renders at both required window sizes.
Keep test settings in isolated temporary directories. Do not launch games,
modify game profiles, or call cloud APIs during UI verification.
Label test-data screenshots and distinguish native window renders from share-card
PNG rendering. Do not count opt-in integrations as verified unless enabled.

The active app is an offline, game-agnostic log analysis tool. It must not
construct game process guards, configuration adapters, MCP clients or cloud
advisors. Legacy optimization code is inactive reference only. Reuse the
analysis-only composition root, not the old automatic tuning controllers.

Read `docs/decisions-offline-log-analysis.md` before changing workflow scope.
