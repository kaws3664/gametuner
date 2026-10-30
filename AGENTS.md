@C:\Users\Lycoris\.codex\RTK.md

## Verification

| Rule | Enforcement |
|---|---|
| Run tests and inspect native WPF screenshots before delivering UI changes. | `tests/verify-ui.ps1` + `tests/GameTuner.UiTests/WorkspaceTests.cs` |
| Never limit a comparison to a fixed number of logs. | `Fifth_and_sixth_log_still_work_and_each_round_keeps_its_actual_configuration` |
| Only actual recorded values and explicitly entered settings belong on share cards. | JSON round-trip and PNG tests in `WorkspaceTests.cs` |

UI tests must render the real application resources and exercise real bindings.
Keep test settings in isolated temporary directories. Do not launch games,
modify game profiles, or call cloud APIs during UI verification.
Label test-data screenshots and distinguish WPF render captures from desktop
input automation. Do not count opt-in integrations as verified unless enabled.

The active app is an offline, game-agnostic log analysis tool. It must not
construct game process guards, configuration adapters, MCP clients or cloud
advisors. Legacy optimization code is inactive reference only. Reuse the
analysis-only composition root, not the old automatic tuning controllers.

Read `docs/decisions-offline-log-analysis.md` before changing workflow scope.
