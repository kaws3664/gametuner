# Native WPF Desktop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the browser workspace with a native WPF application that supports manual game-process selection, repeatable per-round capture, honest graphics-settings import, and the existing offline analysis/save/share workflow.

**Architecture:** Keep `MainViewModel`, core analysis, JSON plans, PNG rendering, and `LiveGameTelemetryMonitor`. Replace HTTP orchestration with a small desktop coordinator and native dialogs. Add operating-system boundary services for process enumeration and structured settings input; do not add a plugin framework or cloud dependency.

**Tech Stack:** .NET 9, WPF/XAML, C#, xUnit, Intel PresentMon 2.3.1, existing `System.Text.Json`/`XDocument` parsers.

**Spec:** `docs/superpowers/specs/2026-10-30-wpf-desktop-design.md`

## Global Constraints

- Offline Windows 10/11 x64; never launch/kill games, write game settings, inject, upload logs, or call NVIDIA MCP/cloud AI.
- Preserve existing user plans and the inactive legacy optimization code.
- Test with isolated temporary data roots; no real games or cloud APIs in verification.
- Use TDD: observe each new test fail for the expected reason before writing the implementation.
- Keep changes surgical and use existing visual tokens and project patterns.

---

### Task 1: Lock Down Core Regression Behavior

**Files:**
- Modify: `src/GameTuner.Core/Analysis/LogAnalysisService.cs`
- Modify: `src/GameTuner.App/ViewModels/MainViewModel.cs`
- Test: `tests/GameTuner.Tests/LogAnalysisTests.cs`
- Test: `tests/GameTuner.UiTests/WorkspaceTests.cs` (temporary regression home before desktop tests replace it)

**Interfaces:**
- Consumes: `LogAnalysisService.AnalyzeAsync`, `ManualRecommendationPlanner.Recommend`.
- Produces: long positive frame times remain in `PerformanceMetrics`; updated graphics settings remain recommendation evidence.

- [ ] **Step 1: Write the failing long-frame test**

```csharp
[Fact]
public async Task Frame_log_keeps_real_stalls_longer_than_ten_seconds()
{
    var frames = Enumerable.Repeat(10d, 100).Append(15000d);
    var report = await _service.AnalyzeAsync(
        Write("long-stall.csv", "FrameTime\n" + string.Join('\n', frames)),
        new TuningGoal(), ManualQualityTier.High);
    Assert.Equal(1000 / frames.Average(), report.Metrics.AverageFps, 5);
    Assert.Equal(1000 / 15000d, report.Metrics.LowFps, 5);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run: `dotnet test tests/GameTuner.Tests/GameTuner.Tests.csproj --filter Frame_log_keeps_real_stalls_longer_than_ten_seconds`

Expected: FAIL because the 15000 ms frame is discarded and the reported low FPS is 100.

- [ ] **Step 3: Keep valid long frames**

Change only the frame filter to reject non-finite and non-positive values:

```csharp
if (!double.IsFinite(frame) || frame <= 0) continue;
```

- [ ] **Step 4: Write the failing recommendation-update test**

Import an NVIDIA log with `GraphicsSettings = "光线追踪：开启"` and GPU utilization 97, call `update`, then assert that the selected round still recommends closing ray tracing:

```csharp
Assert.Contains(state.Rounds.Single().Suggestions,
    item => item.Setting == "光线追踪" && item.SuggestedValue.Contains("关闭"));
```

- [ ] **Step 5: Run the update test and verify RED, then pass current settings**

Expected RED: generic GPU advice replaces the ray-tracing item. Change the recommendation call to:

```csharp
var recommendation = ManualRecommendationPlanner.Recommend(
    previous.Report.Metrics, _session.Goal, configuration.Tier,
    configuration.GraphicsSettings);
```

- [ ] **Step 6: Run focused tests and the full core suite**

Run: `dotnet test tests/GameTuner.Tests/GameTuner.Tests.csproj --filter "Frame_log_keeps_real_stalls_longer_than_ten_seconds|Frame_log_computes_low_from_slowest_one_percent"`

Then run the UI update test and `dotnet test tests/GameTuner.Tests/GameTuner.Tests.csproj`.

Expected: all selected tests PASS with no parser regressions.

---

### Task 2: Add Manual Process Selection

**Files:**
- Create: `src/GameTuner.App/Processes/GameProcessInfo.cs`
- Create: `src/GameTuner.App/Processes/GameProcessService.cs`
- Create: `src/GameTuner.App/Processes/ProcessPickerViewModel.cs`
- Test: `tests/GameTuner.UiTests/ProcessPickerTests.cs`

**Interfaces:**
- Produces: `record GameProcessInfo(int Id, string ProcessName, string WindowTitle)`.
- Produces: `IReadOnlyList<GameProcessInfo> GameProcessService.GetProcesses()`.
- Produces: `ProcessPickerViewModel.Filter`, `Processes`, `SelectedProcess`, and `Refresh()`.

- [ ] **Step 1: Write failing picker tests**

Use a picker constructor that accepts `Func<IReadOnlyList<GameProcessInfo>>`:

```csharp
[Fact]
public void Filter_matches_process_pid_and_window_title_without_losing_selection_capability()
{
    var picker = new ProcessPickerViewModel(() =>
    [
        new(42, "mygame", "My Game"),
        new(77, "other", "Editor")
    ]);
    picker.Filter = "mygame";
    Assert.Contains(picker.Processes, item => item.Id == 42);
    picker.Filter = "77";
    Assert.Contains(picker.Processes, item => item.Id == 77);
    picker.Filter = "editor";
    Assert.DoesNotContain(picker.Processes, item => item.Id == 42);
}
```

Also test that the picker exposes visible-window processes first and preserves
the unfiltered snapshot after `Refresh()`.

- [ ] **Step 2: Run picker tests and verify RED**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter ProcessPicker`

Expected: build/test failure because the process types do not exist.

- [ ] **Step 3: Implement the minimal process service and picker**

`GameProcessService.GetProcesses()` enumerates `Process.GetProcesses()`, reads `Id`, `ProcessName`, and `MainWindowTitle` inside per-process exception handling, disposes each `Process`, sorts entries with a nonempty window title first, then by process name. `ProcessPickerViewModel` stores the unfiltered snapshot and exposes a case-insensitive filtered `ObservableCollection<GameProcessInfo>`.

- [ ] **Step 4: Run picker tests and verify GREEN**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter ProcessPicker`

Expected: all process picker tests PASS; no actual game is started.

---

### Task 3: Make Capture Repeatable And PID-Driven

**Files:**
- Create: `src/GameTuner.App/Desktop/DesktopSessionController.cs`
- Create: `src/GameTuner.App/Desktop/ICaptureService.cs`
- Modify: `src/GameTuner.App/Web/LiveGameTelemetryMonitor.cs`
- Modify: `src/GameTuner.App/Web/LocalConfigurationProvider.cs`
- Test: `tests/GameTuner.UiTests/DesktopSessionTests.cs`

**Interfaces:**
- Consumes: `GameProcessInfo`, `MainViewModel`, `ILocalConfigurationProvider`.
- Produces: `DesktopSessionController(MainViewModel, ILocalConfigurationProvider, ICaptureService)`, `SelectProcess(GameProcessInfo)`, `void StartCapture()`, `StopAndAnalyzeAsync()`, `ImportExternalLogAsync()`, `SelectedProcessId`.
- Produces: `ICaptureService.Start(int, string)`, `ICaptureService.StopForAnalysisAsync()`, and `ICaptureService.Snapshot()`, implemented by `LiveGameTelemetryMonitor`.
- Changes: `ILocalConfigurationProvider.ReadAsync(GameProcessInfo? process, string game, bool includeDesktopResolution, string logDirectory)` accepts the selected process instead of running a two-name auto-detection hardcode.

- [ ] **Step 1: Write failing consecutive-capture tests**

Use a fake capture boundary around the existing monitor and temporary log files:

```csharp
[Fact]
public async Task Two_rounds_consume_two_new_capture_paths()
{
    var root = Path.Combine(Path.GetTempPath(), "GameTuner-DesktopTests", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(root);
    paths.EnsureCreated();
    var capture = new FakeCaptureService(paths);
    using var session = new DesktopSessionController(
        new MainViewModel(new LogAnalysisService(), new JsonFileStore(), paths),
        new FakeConfigurationProvider(), capture);
    session.SelectProcess(new GameProcessInfo(42, "fakegame", "Fake Game"));
    session.StartCapture();
    await session.StopAndAnalyzeAsync();
    session.StartCapture();
    await session.StopAndAnalyzeAsync();
    Assert.Equal(2, session.ViewModel.Rounds.Count);
    Assert.Equal(2, capture.StartedPaths.Distinct().Count());
}
```

Define `FakeCaptureService` in the test file. Its `Start` method writes a new
two-sample NVIDIA CSV with a unique path and distinct FPS on every call;
`StopForAnalysisAsync` returns that path once and then returns null. Define
`FakeConfigurationProvider` with empty hardware/settings and the selected PID.
Use the fake through a direct reference to `capture`, not by adding test-only
members to production code:

```csharp
Assert.Equal(2, capture.StartedPaths.Distinct().Count());
```

Add tests that an exited selected PID is rejected, manual selection overrides automatic recognition, and importing an external log stops active capture before parsing.

- [ ] **Step 2: Run desktop-session tests and verify RED**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter DesktopSession`

Expected: failure because no desktop coordinator or repeatable capture contract exists.

- [ ] **Step 3: Implement explicit capture ownership**

Move only orchestration from `WorkspaceApi` into `DesktopSessionController`. Keep `MainViewModel` responsible for analysis. Add a pending-path guard to the monitor:

```csharp
public async Task<string?> StopForAnalysisAsync()
{
    Task? task;
    string path;
    lock (_sync)
    {
        task = _captureTask;
        path = _logPath;
        _logPath = "";
        if (_isRecording) _status = "正在整理本轮监测日志…";
        if (_stopping is not null && !_stopping.IsCancellationRequested) _stopping.Cancel();
    }
    if (task is not null) await task;
    lock (_sync)
    {
        if (string.Equals(_logPath, path, StringComparison.OrdinalIgnoreCase)) _logPath = "";
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }
}
```

The path is cleared only after `CaptureAsync` finishes finalizing that file.
Start each new round with a new path and never gate `Start` on
`Rounds.Count == 0`.

- [ ] **Step 4: Route configuration through the selected PID**

Replace `FindRunningGame()` with a provider input containing PID, executable name, and display game name. Automatic detection may suggest a process only before manual selection. Read-only RDR2/007 adapters continue to use the effective game name; no adapter gets write access.

- [ ] **Step 5: Run capture and existing monitor tests**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter "DesktopSession|PresentMon_monitor"`

Expected: consecutive paths differ, old paths are not reused, external import stops capture, all tests PASS.

---

### Task 4: Add Honest Structured Settings Import

**Files:**
- Create: `src/GameTuner.App/Settings/StructuredSettingsReader.cs`
- Modify: `src/GameTuner.App/Desktop/DesktopSessionController.cs`
- Test: `tests/GameTuner.UiTests/StructuredSettingsReaderTests.cs`

**Interfaces:**
- Produces: `Task<string> StructuredSettingsReader.ReadAsync(string path)` returning display-ready lines and preserving raw setting names.
- Consumes: native settings-file dialog and current-round `GraphicsSettings`.

- [ ] **Step 1: Write failing JSON/XML/INI tests**

```csharp
[Theory]
[InlineData("settings.json", "{\"rayTracing\":true,\"textureQuality\":\"high\"}", "textureQuality：high")]
[InlineData("settings.ini", "[graphics]\nrayTracing=on", "rayTracing：on")]
[InlineData("settings.xml", "<settings><rayTracing>on</rayTracing></settings>", "rayTracing：on")]
public async Task Reads_scalar_settings_without_inventing_quality_labels(
    string name, string content, string expected)
{
    var root = Path.Combine(Path.GetTempPath(), "GameTuner-SettingsTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, name);
    File.WriteAllText(path, content);
    var text = await new StructuredSettingsReader().ReadAsync(path);
    Assert.Contains(expected, text);
}
```

Add rejection tests for nested JSON objects/arrays, duplicate INI keys with conflicting values, malformed XML, files over 1 MB, and non-`.json`/`.xml`/`.ini` extensions.

- [ ] **Step 2: Run reader tests and verify RED**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter StructuredSettingsReader`

Expected: failure because the reader does not exist.

- [ ] **Step 3: Implement scalar-only readers**

Use `JsonDocument`, `XDocument`, and line-based INI parsing. JSON accepts only root scalar properties. XML accepts leaf elements and attributes with unique names. INI accepts unique `key=value` entries and includes section names in display context. Return original key names and scalar values joined by newlines; never translate `high`, `on`, or unknown keys into inferred game settings.

- [ ] **Step 4: Connect explicit import to the current snapshot**

The coordinator sets `GraphicsSettings` only after a successful read, sets source text to the selected file name, and marks it player-selected rather than adapter-read. Import does not modify the source file or an older round until the player explicitly updates that round.

- [ ] **Step 5: Run reader and coordinator tests**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter "StructuredSettingsReader|DesktopSession"`

Expected: valid scalar files PASS, ambiguous input fails clearly, source bytes remain unchanged.

---

### Task 5: Build The Native Desktop Workspace

**Files:**
- Create: `src/GameTuner.App/MainWindow.xaml`
- Create: `src/GameTuner.App/MainWindow.xaml.cs`
- Create: `src/GameTuner.App/ProcessPickerWindow.xaml`
- Create: `src/GameTuner.App/ProcessPickerWindow.xaml.cs`
- Modify: `src/GameTuner.App/Program.cs`
- Modify: `src/GameTuner.App/App.xaml`
- Test: `tests/GameTuner.UiTests/DesktopWindowTests.cs`

**Interfaces:**
- Consumes: `DesktopSessionController`, `ProcessPickerViewModel`, existing commands and share renderer.
- Produces: a single native foreground window with no HTTP server or browser startup.

- [ ] **Step 1: Write failing desktop-window tests**

Instantiate `MainWindow` on the test STA thread with a real
`DesktopSessionController` backed by `FakeCaptureService` and
`FakeConfigurationProvider`, then assert:

```csharp
Assert.Equal("GameTuner · 离线性能工作台", window.Title);
Assert.NotNull(window.FindName("ProcessButton"));
Assert.NotNull(window.FindName("CaptureButton"));
Assert.NotNull(window.FindName("RoundsList"));
Assert.NotNull(window.FindName("ExportButton"));
Assert.Equal(960, window.MinWidth);
Assert.Equal(680, window.MinHeight);
```

Add a test that closing the window disposes `FakeCaptureService` exactly once
and never starts a listener or browser. Keep the fake as a direct test
reference; no test-only production properties are needed.

- [ ] **Step 2: Run window tests and verify RED**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter DesktopWindow`

Expected: failure because the native windows do not exist.

- [ ] **Step 3: Implement the WPF views and startup**

Create the layout specified in the design doc using existing brushes/fonts and `ScrollViewer`. Bind commands to controller/view-model methods. `Program.Main` creates the WPF application, paths, coordinator, and `MainWindow`, then calls `app.Run(window)`. Keep `--data-root` for isolated tests and remove `--port`/`--no-browser` only after callers are migrated.

The process dialog has a search box, refresh button, list, select button, and cancel button. Double-click and Enter select; Escape cancels. The main window uses Ctrl+S for save and Ctrl+O for log selection through `InputBindings`.

- [ ] **Step 4: Run desktop tests and verify GREEN**

Run: `dotnet test tests/GameTuner.UiTests/GameTuner.UiTests.csproj --filter "DesktopWindow|ProcessPicker|DesktopSession"`

Expected: native controls exist, close disposes once, commands are reachable, and no HTTP listener starts.

---

### Task 6: Remove The Browser Transport And Update Packaging

**Files:**
- Delete: `src/GameTuner.App/Web/LocalWebServer.cs`
- Delete: `src/GameTuner.App/Web/WorkspaceApi.cs`
- Delete: `src/GameTuner.App/Web/WorkspaceRequests.cs`
- Delete: `src/GameTuner.App/Web/WorkspaceState.cs`
- Delete: `src/GameTuner.App/Web/DesktopDialogs.cs` after native dialogs reach parity
- Delete: `src/GameTuner.App/WebApp/app.js`
- Delete: `src/GameTuner.App/WebApp/app.css`
- Delete: `src/GameTuner.App/WebApp/index.html`
- Modify: `src/GameTuner.App/GameTuner.App.csproj`
- Modify: `scripts/publish.ps1`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `tests/verify-ui.ps1`

**Interfaces:**
- Consumes: native coordinator and desktop tests from Tasks 3-5.
- Produces: self-contained WPF release without `WebApp`, listener, URL key, or browser-specific verification claims.

- [ ] **Step 1: Add failing packaging assertions**

Assert the built output does not contain `WebApp`, the app project has no `WebApp/**` content include, and the README contains no instruction to open a loopback URL.

- [ ] **Step 2: Run assertions and verify RED**

Run the new packaging test or PowerShell assertion against the current tree.

Expected: FAIL because browser assets and instructions remain.

- [ ] **Step 3: Remove only the obsolete transport**

Move `LiveGameTelemetryMonitor` and local configuration files to neutral `Telemetry`/`Configuration` namespaces only if needed for clarity; otherwise retain paths to minimize churn. Remove browser files and content includes after all native tests pass. Update docs to describe WPF, process selection, three-level settings support, and native verification.

- [ ] **Step 4: Publish and inspect the package**

Run: `./scripts/publish.ps1`

Expected: release builds, `GameTuner.App.exe` starts as WPF, bundled `Tools/PresentMon/PresentMon.exe` exists, no `WebApp` directory exists, and no browser opens.

---

### Task 7: Visual Verification And Full Regression

**Files:**
- Modify only files needed to fix verified regressions.
- Create generated artifacts under: `artifacts/desktop-verification/`

**Interfaces:**
- Consumes: complete native app and test suite.
- Produces: test evidence, desktop screenshots, and final release verification.

- [ ] **Step 1: Run all automated tests**

Run: `./tests/verify-ui.ps1 -SkipRestore`

Expected: build has zero warnings/errors; all core and desktop tests pass; TRX counters confirm nonzero passes and zero failures.

- [ ] **Step 2: Exercise the real workflow with test data**

Select a test-owned process, capture/import two distinct supported logs, update settings, save/reload a plan, and export a PNG. Use an isolated `--data-root`; do not launch a game or touch real profiles.

Expected: two distinct rounds remain intact, no duplicate old capture is consumed, and native dialogs complete their requested actions.

- [ ] **Step 3: Render and inspect both desktop sizes**

Capture 1180x780 and 960x680 windows plus the process picker into `artifacts/desktop-verification`. Inspect actual images for clipped actions, text overlap, contrast, focus indication, and overflow behavior. Fix only observed issues and rerun affected tests.

- [ ] **Step 4: Run final release verification**

Run: `./scripts/publish.ps1`

Expected: all tests remain green, release is self-contained, only bundled PresentMon is used, and final documentation matches behavior. Report any real-game/NVIDIA integration that was not exercised instead of claiming it was verified.
