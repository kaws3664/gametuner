using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GameTuner.App;
using GameTuner.App.Desktop;
using GameTuner.App.Processes;
using GameTuner.App.ViewModels;
using GameTuner.App.Web;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Telemetry;

namespace GameTuner.UiTests;

public sealed class PresentMonFactAttribute : FactAttribute
{
    public PresentMonFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMETUNER_RUN_PRESENTMON") != "1")
            Skip = "Set GAMETUNER_RUN_PRESENTMON=1 in an elevated test process to capture real native frames.";
    }
}

public sealed class CaptureFlowTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "GameTuner-CaptureFlow", Guid.NewGuid().ToString("N")));
    public CaptureFlowTests() => _paths.EnsureCreated();

    [Theory]
    [InlineData("MirrorsEdge")]
    [InlineData("OriginalSin2")]
    [InlineData("SteamWorldDig")]
    public void Game_names_containing_utility_substrings_are_not_rejected(string name)
    {
        var detected = new GameProcessService().DetectRunningGame([new GameWindowCandidate(42, name, name, 1)]);
        Assert.Equal(42, detected?.Id);
    }

    [Fact]
    public Task Capture_retries_detection_when_the_game_appears_after_startup() => Sta.RunAsync(async () =>
    {
        var processes = new MutableProcessService();
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, processes);
        var window = new MainWindow(session, paths: _paths);
        try
        {
            await session.InitializeAsync();
            Assert.Equal(0, session.SelectedProcessId);
            processes.Detected = CurrentProcess();
            await window.StartCaptureCommand.ExecuteAsync();
            Assert.True(capture.Snapshot().IsRecording, session.OperationStatus);
            Assert.Equal(Environment.ProcessId, Assert.Single(capture.StartedProcessIds));
            await window.StopAndAnalyzeCommand.ExecuteAsync();
            Assert.Single(session.ViewModel.Rounds);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(.35)]
    [InlineData(.59)]
    public async Task Startup_detects_a_single_windowed_game_without_a_player_entered_name(double coverage)
    {
        var processes = new MutableProcessService
        {
            Candidates =
            [
                new(12, "steam", "Steam", .9),
                new(42, "SomeGame", "Some Game", coverage)
            ]
        };
        using var session = CreateSession(new FakeCaptureService(_paths), processes);
        await session.InitializeAsync();
        Assert.Equal(42, session.SelectedProcessId);
        Assert.Equal("SomeGame", session.ViewModel.Game);
    }

    [Fact]
    public async Task Player_entered_game_name_resolves_a_windowed_process()
    {
        var processes = new MutableProcessService { Candidates = [new(42, "SomeGame", "Some Game", .35)] };
        using var session = CreateSession(new FakeCaptureService(_paths), processes);
        session.ViewModel.Game = "SomeGame.exe";
        await session.InitializeAsync();
        Assert.Equal(42, session.SelectedProcessId);
        Assert.Equal("SomeGame.exe", session.ViewModel.Game);
    }

    [Fact]
    public async Task Matching_multiple_windowed_processes_requires_player_selection()
    {
        var processes = new MutableProcessService
        {
            Candidates = [new(42, "SomeGame", "Some Game", .35), new(43, "SomeGame", "Some Game", .35)]
        };
        using var session = CreateSession(new FakeCaptureService(_paths), processes);
        session.ViewModel.Game = "SomeGame";
        await session.InitializeAsync();
        Assert.Equal(0, session.SelectedProcessId);
        Assert.Empty(session.ViewModel.Rounds);
    }

    [Fact]
    public async Task Startup_keeps_a_manual_selection_even_before_the_first_round()
    {
        var processes = new MutableProcessService { Detected = new(42, "AnotherGame", "Other") };
        using var session = CreateSession(new FakeCaptureService(_paths), processes);
        session.SelectProcess(CurrentProcess());
        var name = session.ViewModel.Game;
        await session.InitializeAsync();
        Assert.Equal(Environment.ProcessId, session.SelectedProcessId);
        Assert.Equal(name, session.ViewModel.Game);
    }

    [Fact]
    public async Task Finalized_capture_preserves_frame_columns_for_the_real_parser()
    {
        using var monitor = new LiveGameTelemetryMonitor(_paths);
        monitor.Start(Environment.ProcessId, "CsvFixture");
        var path = monitor.Snapshot().LogPath;
        File.WriteAllText(path, "Application,ProcessID,TimeInSeconds,MsBetweenPresents\n"
            + string.Join('\n', Enumerable.Range(0, 25).Select(index =>
                FormattableString.Invariant($"testhost,{Environment.ProcessId},{index * .02:0.00},20"))));
        Assert.Equal(path, await monitor.StopForAnalysisAsync());
        var lines = File.ReadAllLines(path);
        var columns = PerformanceLogReader.SplitCsv(lines[0]).Count;
        Assert.All(lines.Skip(1), line => Assert.Equal(columns, PerformanceLogReader.SplitCsv(line).Count));
        var report = await new LogAnalysisService().AnalyzeAsync(path, new() { TargetFps = 60 }, ManualQualityTier.Unknown);
        Assert.Equal(25, report.SampleCount);
        Assert.Equal(50, report.Metrics.AverageFps, 6);
    }

    [PresentMonFact]
    [Trait("Category", "PresentMonIntegration")]
    public Task Bundled_presentMon_records_real_frames_from_a_native_test_window()
    {
        if (Environment.GetEnvironmentVariable("GAMETUNER_RUN_PRESENTMON") != "1") return Task.CompletedTask;
        return Sta.RunAsync(async () =>
        {
            var panel = new Border();
            var window = new Window { Title = "GameTuner TEST DATA - native capture fixture", Width = 400, Height = 300, Content = panel };
            using var monitor = new LiveGameTelemetryMonitor(_paths);
            window.Show();
            try
            {
                monitor.Start(Environment.ProcessId, "NativeWpfFixture");
                var path = monitor.Snapshot().LogPath;
                for (var index = 0; index < 150; index++)
                {
                    panel.Background = new SolidColorBrush(Color.FromRgb((byte)(index % 255), 60, 120));
                    await Task.Delay(20);
                }
                var finalized = await monitor.StopForAnalysisAsync();
                Assert.True(finalized is not null, monitor.Snapshot().Status);
                Assert.Equal(path, finalized);
                var output = Environment.GetEnvironmentVariable("GAMETUNER_CAPTURE_ARTIFACTS");
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    File.Copy(path, Path.Combine(output, "native-wpf-real-presentmon.csv"), overwrite: true);
                }
                Assert.Contains(PerformanceLogReader.SplitCsv(File.ReadLines(path).First()),
                    column => column.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                var report = await new LogAnalysisService().AnalyzeAsync(path, new() { TargetFps = 60 }, ManualQualityTier.Unknown);
                Assert.True(report.SampleCount >= 10, monitor.Snapshot().Status);
                if (output is not null)
                {
                    File.WriteAllText(Path.Combine(output, "native-wpf-real-presentmon.txt"),
                        $"TEST DATA: native WPF window; no game launched.{Environment.NewLine}Samples={report.SampleCount}; FPS={report.Metrics.AverageFps:0.###}");
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public Task Native_window_analyzes_a_finished_capture_without_another_click() => Sta.RunAsync(async () =>
    {
        var capture = new CompletingCaptureService(_paths);
        using var session = CreateSession(capture);
        var window = new MainWindow(session, paths: _paths);
        session.SelectProcess(CurrentProcess());
        window.Show();
        try
        {
            await window.StartCaptureCommand.ExecuteAsync();
            Assert.True(session.IsRecording);
            Assert.False(window.StartCaptureCommand.CanExecute(null));
            capture.Complete();
            await WaitUntil(() => session.ViewModel.Rounds.Count > 0);
            Assert.Equal(40, Assert.Single(session.ViewModel.Rounds).Round.Report.Metrics.AverageFps);
            Assert.False(session.IsRecording);
            await session.StopAndAnalyzeAsync();
            Assert.Single(session.ViewModel.Rounds);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Native_window_refreshes_process_and_capture_text_bindings() => Sta.RunAsync(async () =>
    {
        using var session = CreateSession(new FakeCaptureService(_paths));
        var window = new MainWindow(session, paths: _paths);
        window.Show();
        try
        {
            var processText = FindBoundText(window, nameof(MainWindow.ProcessStatus));
            var captureText = FindBoundText(window, nameof(MainWindow.CaptureStatus));
            session.SelectProcess(CurrentProcess());
            await window.StartCaptureCommand.ExecuteAsync();
            Assert.Equal(session.ProcessStatus, processText.Text);
            Assert.Equal(session.CaptureStatus, captureText.Text);
            Assert.Contains("正在", captureText.Text);
            await window.StopAndAnalyzeCommand.ExecuteAsync();
            Assert.Equal(session.CaptureStatus, captureText.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task Failed_capture_completion_does_not_create_a_round_or_retry_forever()
    {
        var capture = new CompletingCaptureService(_paths);
        using var session = CreateSession(capture);
        session.SelectProcess(CurrentProcess());
        await session.StartCaptureAsync();
        File.Delete(capture.Snapshot().LogPath);
        capture.Complete();
        await session.RefreshAsync();
        Assert.Empty(session.ViewModel.Rounds);
        Assert.False(session.HasPendingCapture);
        Assert.Contains("没有可分析", session.OperationStatus);
        await session.RefreshAsync();
        Assert.Empty(session.ViewModel.Rounds);
    }

    [Fact]
    public async Task External_import_consumes_the_pending_capture_without_adding_it()
    {
        var capture = new CompletingCaptureService(_paths);
        using var session = CreateSession(capture);
        session.SelectProcess(CurrentProcess());
        await session.StartCaptureAsync();
        capture.Complete();
        var external = Path.Combine(_paths.Logs, "external.csv");
        File.WriteAllText(external, "Timestamp (Elapsed time in seconds),FPS,FPS 1(%) Low\n0,55,45\n300,55,45\n");
        await session.ImportExternalLogAsync(external);
        await session.RefreshAsync();
        Assert.Equal(external, Assert.Single(session.ViewModel.Rounds).Round.Report.SourcePath);
    }

    private DesktopSessionController CreateSession(ICaptureService capture, GameProcessService? processes = null) =>
        new(new MainViewModel(new LogAnalysisService(), new JsonFileStore(), _paths), new FakeConfigurationProvider(), capture, processes);

    private static GameProcessInfo CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.ProcessName, "Capture fixture");
    }

    private static async Task WaitUntil(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.True(completed(), "The native dispatcher did not complete the capture flow within five seconds.");
    }

    private static TextBlock FindBoundText(DependencyObject root, string path)
    {
        if (root is TextBlock text && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == path) return text;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindBoundTextOrNull(VisualTreeHelper.GetChild(root, index), path);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Missing native text binding: " + path);
    }

    private static TextBlock? FindBoundTextOrNull(DependencyObject root, string path)
    {
        if (root is TextBlock text && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == path) return text;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindBoundTextOrNull(VisualTreeHelper.GetChild(root, index), path);
            if (found is not null) return found;
        }
        return null;
    }

    private sealed class MutableProcessService : GameProcessService
    {
        public GameProcessInfo? Detected { get; set; }
        public IReadOnlyList<GameWindowCandidate> Candidates { get; init; } = [];
        public override GameProcessInfo? DetectRunningGame() => Detected ?? DetectRunningGame(Candidates);
        public override IReadOnlyList<GameProcessInfo> GetProcesses() =>
            Candidates.Select(candidate => new GameProcessInfo(candidate.Id, candidate.ProcessName, candidate.WindowTitle)).ToArray();
        public override IReadOnlyList<GameWindowCandidate> BuildCandidates(IReadOnlyList<GameProcessInfo> processes) => Candidates;
    }

    private sealed class CompletingCaptureService(AppPaths paths) : ICaptureService
    {
        private readonly FakeCaptureService _capture = new(paths);
        private bool _complete;
        public event EventHandler? StateChanged
        {
            add => _capture.StateChanged += value;
            remove => _capture.StateChanged -= value;
        }
        public void Start(int processId, string game) { _complete = false; _capture.Start(processId, game); }
        public void Complete() => _complete = true;
        public LiveTelemetryState Snapshot() => _capture.Snapshot() with { IsRecording = !_complete && _capture.Snapshot().IsRecording };
        public async Task<string?> StopForAnalysisAsync()
        {
            var path = await _capture.StopForAnalysisAsync();
            return path is not null && File.Exists(path) ? path : null;
        }
        public void Dispose() => _capture.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }
}
