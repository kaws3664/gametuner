using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using GameTuner.App.Desktop;
using GameTuner.App.Processes;
using GameTuner.App.ViewModels;
using GameTuner.App.Web;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;

namespace GameTuner.UiTests;

public sealed class DesktopSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameTuner-DesktopTests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public DesktopSessionTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    [Fact]
    public async Task Two_rounds_consume_two_new_capture_paths()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out _);
        session.SelectProcess(CurrentProcess());

        session.StartCapture();
        await session.StopAndAnalyzeAsync();
        session.StartCapture();
        await session.StopAndAnalyzeAsync();

        Assert.Equal(2, session.ViewModel.Rounds.Count);
        Assert.Equal(2, capture.StartedPaths.Distinct().Count());
        Assert.Equal(capture.StartedPaths,
            session.ViewModel.Rounds.Select(round => round.Round.Report.SourcePath).ToArray());
        Assert.NotEqual(session.ViewModel.Rounds[0].Round.Report.Metrics.AverageFps,
            session.ViewModel.Rounds[1].Round.Report.Metrics.AverageFps);
    }

    [Fact]
    public async Task Stop_without_a_pending_capture_does_not_reuse_a_consumed_path()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out _);
        session.SelectProcess(CurrentProcess());
        session.StartCapture();
        await session.StopAndAnalyzeAsync();

        await session.StopAndAnalyzeAsync();

        Assert.Single(session.ViewModel.Rounds);
        Assert.Equal(2, capture.StopCallCount);
    }

    [Fact]
    public async Task Concurrent_monitor_stops_return_a_finalized_path_only_once()
    {
        using var monitor = new LiveGameTelemetryMonitor(_paths);
        monitor.Start(Environment.ProcessId, "DesktopSessionConcurrency");
        Assert.DoesNotContain("启动失败", monitor.Snapshot().Status);
        var capturePath = monitor.Snapshot().LogPath;
        Assert.False(string.IsNullOrEmpty(capturePath));
        WriteLog(capturePath, 60, Environment.ProcessId);

        var firstStop = monitor.StopForAnalysisAsync();
        Assert.False(firstStop.IsCompleted, "The fixture must keep the first stop active before the second begins.");
        var secondStop = monitor.StopForAnalysisAsync();
        var consumedPaths = await Task.WhenAll(firstStop, secondStop);

        var path = Assert.Single(consumedPaths, path => path is not null);
        Assert.True(File.Exists(path));
        Assert.Empty(monitor.Snapshot().LogPath);
    }

    [Fact]
    public async Task Same_process_restart_waits_for_stop_and_creates_a_new_capture()
    {
        using var monitor = new LiveGameTelemetryMonitor(_paths);
        monitor.Start(Environment.ProcessId, "BeforeRestart");
        var firstPath = monitor.Snapshot().LogPath;
        WriteLog(firstPath, 60, Environment.ProcessId);
        var stop = monitor.StopForAnalysisAsync();
        Assert.False(stop.IsCompleted, "The fixture must restart while the first stop is active.");
        var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restart = Task.Run(() =>
        {
            restartEntered.SetResult();
            monitor.Start(Environment.ProcessId, "AfterRestart");
        });
        await restartEntered.Task;

        var consumedPath = await stop;
        await restart;

        Assert.Equal(firstPath, consumedPath);
        var secondPath = monitor.Snapshot().LogPath;
        Assert.False(string.IsNullOrEmpty(secondPath));
        Assert.NotEqual(firstPath, secondPath);
        Assert.True(monitor.Snapshot().IsRecording);
        WriteLog(secondPath, 65, Environment.ProcessId);
        Assert.Equal(secondPath, await monitor.StopForAnalysisAsync());
    }

    [Fact]
    public async Task Same_process_restart_completes_on_a_single_thread_synchronization_context()
    {
        var existing = PresentMonProcessIds();
        using var monitor = new LiveGameTelemetryMonitor(_paths);
        using var context = new SingleThreadSynchronizationContext();
        var secondPath = "";
        try
        {
            context.Execute(() =>
            {
                monitor.Start(Environment.ProcessId, "ContextBefore");
                WriteLog(monitor.Snapshot().LogPath, 60, Environment.ProcessId);
                var stop = monitor.StopForAnalysisAsync();
                Assert.False(stop.IsCompleted);
                monitor.Start(Environment.ProcessId, "ContextAfter");
                secondPath = monitor.Snapshot().LogPath;
            });

            var completed = await Task.WhenAny(context.Completion, Task.Delay(5000));
            if (completed != context.Completion)
            {
                await CleanupBlockedContext(context, monitor, existing);
                Assert.Fail("The same-thread restart deadlocked against a stop continuation.");
            }
            await context.Completion;

            Assert.False(string.IsNullOrEmpty(secondPath));
            Assert.True(monitor.Snapshot().IsRecording);
            WriteLog(secondPath, 65, Environment.ProcessId);
            var cleanup = monitor.StopForAnalysisAsync();
            if (await Task.WhenAny(cleanup, Task.Delay(5000)) != cleanup)
            {
                await CleanupBlockedContext(context, monitor, existing);
                Assert.Fail("The final capture cleanup did not complete.");
            }
            Assert.Equal(secondPath, await cleanup);
        }
        finally
        {
            context.BeginExternalPump();
            KillNewPresentMonProcesses(existing);
        }
    }

    [Fact]
    public async Task Concurrent_initial_starts_own_only_one_presentMon_process()
    {
        var existing = PresentMonProcessIds();
        using var monitor = new LiveGameTelemetryMonitor(_paths);
        try
        {
            const int callerCount = 12;
            using var barrier = new Barrier(callerCount);
            var starts = Enumerable.Range(0, callerCount).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                monitor.Start(Environment.ProcessId, "ConcurrentStarts");
            })).ToArray();

            await Task.WhenAll(starts);

            var owned = PresentMonProcessIds().Except(existing).ToArray();
            var process = Assert.Single(owned);
            Assert.Contains(process, PresentMonProcessIds());
            Assert.True(monitor.Snapshot().IsRecording);
        }
        finally
        {
            await monitor.StopForAnalysisAsync();
            KillNewPresentMonProcesses(existing);
        }
    }

    [Fact]
    public void Exited_selected_process_is_rejected_before_capture_starts()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out _);
        session.SelectProcess(new GameProcessInfo(int.MaxValue, "exitedgame", "Exited Game"));

        Assert.Throws<InvalidOperationException>(() => session.StartCapture());

        Assert.Empty(capture.StartedPaths);
    }

    [Fact]
    public async Task Manual_selection_is_the_capture_and_configuration_authority()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out var configuration);
        configuration.Snapshot = new LocalConfigurationSnapshot
        {
            DetectedGame = "Red Dead Redemption 2",
            ProcessId = 777
        };
        var selected = CurrentProcess();

        session.SelectProcess(selected);
        session.StartCapture();
        await session.StopAndAnalyzeAsync();

        Assert.Equal(selected.Id, Assert.Single(capture.StartedProcessIds));
        Assert.Equal(selected, configuration.LastProcess);
        Assert.Equal(selected.ProcessName, session.ViewModel.Game);
    }

    [Fact]
    public async Task External_import_stops_active_capture_and_parses_the_selected_file()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out _);
        session.SelectProcess(CurrentProcess());
        session.StartCapture();
        var capturedPath = Assert.Single(capture.StartedPaths);
        var externalPath = Path.Combine(_paths.Logs, "external.csv");
        WriteLog(externalPath, 55, Environment.ProcessId);

        await session.ImportExternalLogAsync(externalPath);

        Assert.Equal(1, capture.StopCallCount);
        Assert.False(capture.Snapshot().IsRecording);
        var round = Assert.Single(session.ViewModel.Rounds);
        Assert.Equal(externalPath, round.Round.Report.SourcePath);
        Assert.NotEqual(capturedPath, round.Round.Report.SourcePath);
    }

    [Fact]
    public async Task Settings_import_is_read_only_and_waits_for_explicit_round_update()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out _);
        session.SelectProcess(CurrentProcess());
        session.ViewModel.GraphicsSettings = "Original measured settings";
        session.StartCapture();
        await session.StopAndAnalyzeAsync();
        var originalRound = Assert.Single(session.ViewModel.Rounds).Round;
        var path = Path.Combine(_root, "player-settings.json");
        File.WriteAllText(path, "{\"rayTracing\":true,\"textureQuality\":\"high\"}");
        var sourceBytes = await File.ReadAllBytesAsync(path);

        await session.ImportGraphicsSettingsAsync(path);

        Assert.Equal(string.Join(Environment.NewLine, "rayTracing：true", "textureQuality：high"),
            session.ViewModel.GraphicsSettings);
        Assert.Equal("player-settings.json", session.ConfigurationSource);
        Assert.Contains("玩家", session.ConfigurationStatus);
        Assert.False(session.GraphicsReadOnly);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("Original measured settings", originalRound.Configuration.GraphicsSettings);
        Assert.Equal("Original measured settings",
            Assert.Single(session.ViewModel.Rounds).Round.Configuration.GraphicsSettings);

        await session.ViewModel.UpdateDetailsAsync();

        Assert.Equal(session.ViewModel.GraphicsSettings,
            Assert.Single(session.ViewModel.Rounds).Round.Configuration.GraphicsSettings);
    }

    [Fact]
    public async Task Player_selected_settings_survive_adapter_refresh_and_snapshot_the_next_round()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out var configuration);
        configuration.Snapshot = new LocalConfigurationSnapshot
        {
            GraphicsSettings = "adapter：read",
            HasGameSettings = true,
            Source = "Fake adapter",
            Status = "Adapter read complete"
        };
        session.SelectProcess(CurrentProcess());
        var path = Path.Combine(_root, "player-settings.xml");
        File.WriteAllText(path, "<settings><rayTracing>on</rayTracing></settings>");

        await session.ImportGraphicsSettingsAsync(path);
        session.StartCapture();
        await session.StopAndAnalyzeAsync();

        Assert.Equal("rayTracing：on", session.ViewModel.GraphicsSettings);
        Assert.Equal("rayTracing：on",
            Assert.Single(session.ViewModel.Rounds).Round.Configuration.GraphicsSettings);
        Assert.Equal("player-settings.xml", session.ConfigurationSource);
        Assert.False(session.GraphicsReadOnly);
    }

    [Fact]
    public async Task Failed_settings_import_keeps_the_current_snapshot_and_source_unchanged()
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out var configuration);
        configuration.Snapshot = new LocalConfigurationSnapshot
        {
            GraphicsSettings = "adapter：read",
            HasGameSettings = true,
            Source = "Fake adapter",
            Status = "Adapter read complete"
        };
        session.SelectProcess(CurrentProcess());
        session.StartCapture();
        await session.StopAndAnalyzeAsync();
        var originalRound = Assert.Single(session.ViewModel.Rounds).Round;
        var path = Path.Combine(_root, "ambiguous.json");
        File.WriteAllText(path, "{\"graphics\":{\"rayTracing\":true}}");
        var sourceBytes = await File.ReadAllBytesAsync(path);

        await session.ImportGraphicsSettingsAsync(path);

        Assert.StartsWith("操作未完成：", session.OperationStatus);
        Assert.False(session.IsBusy);
        Assert.Equal("adapter：read", session.ViewModel.GraphicsSettings);
        Assert.Equal("Fake adapter", session.ConfigurationSource);
        Assert.Equal("Adapter read complete", session.ConfigurationStatus);
        Assert.True(session.GraphicsReadOnly);
        Assert.Equal("adapter：read", originalRound.Configuration.GraphicsSettings);
        Assert.Single(session.ViewModel.Rounds);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void Settings_import_uses_the_session_gate_and_restores_ui_affinity() => Sta.Run(async () =>
    {
        var capture = new FakeCaptureService(_paths);
        using var session = CreateSession(capture, out _);
        var path = Path.Combine(_root, "dispatcher-settings.json");
        var payload = new string('x', 128 * 1024);
        File.WriteAllText(path, "{\"quality\":\"" + payload + "\"}");
        var observedDispatcher = false;
        var observedBusy = false;
        session.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(session.ViewModel.GraphicsSettings)) return;
            observedDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher.CheckAccess();
            observedBusy = session.IsBusy;
        };

        await Task.Run(() => session.ImportGraphicsSettingsAsync(path));

        Assert.True(observedDispatcher, "Settings notifications must be raised on the owning WPF dispatcher.");
        Assert.True(observedBusy, "Settings import must run inside the shared session lifecycle.");
        Assert.False(session.IsBusy);
    });

    private DesktopSessionController CreateSession(
        FakeCaptureService capture, out FakeConfigurationProvider configuration)
    {
        configuration = new FakeConfigurationProvider();
        return new DesktopSessionController(
            new MainViewModel(new LogAnalysisService(), new JsonFileStore(), _paths),
            configuration, capture);
    }

    private static GameProcessInfo CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return new GameProcessInfo(process.Id, process.ProcessName, "Desktop capture test");
    }

    private static void WriteLog(string path, double fps, int processId)
    {
        File.WriteAllText(path, FormattableString.Invariant(
            $"Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)\n0,{processId},{fps},{fps - 10},55,97\n300,{processId},{fps},{fps - 10},55,97\n"));
    }

    private static int[] PresentMonProcessIds() =>
        Process.GetProcessesByName("PresentMon").Select(process =>
        {
            using (process) return process.Id;
        }).ToArray();

    private static void KillNewPresentMonProcesses(int[] existing)
    {
        foreach (var processId in PresentMonProcessIds().Except(existing))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill();
            }
            catch (ArgumentException) { }
        }
    }

    private static async Task CleanupBlockedContext(
        SingleThreadSynchronizationContext context, LiveGameTelemetryMonitor monitor, int[] existing)
    {
        context.BeginExternalPump();
        KillNewPresentMonProcesses(existing);
        await PumpUntil(context, () => context.Completion.IsCompleted);
        var cleanup = monitor.StopForAnalysisAsync();
        await PumpUntil(context, () => cleanup.IsCompleted);
        KillNewPresentMonProcesses(existing);
    }

    private static async Task PumpUntil(SingleThreadSynchronizationContext context, Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            context.DrainOne(25);
            await Task.Yield();
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _actions = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private volatile bool _externalPump;
        private volatile bool _stopped;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "GameTuner UI context test" };
            _thread.Start();
        }

        public Task Completion => _completion.Task;

        public void Execute(Action action) => Post(_ =>
        {
            try
            {
                action();
                _completion.TrySetResult();
            }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }, null);

        public override void Post(SendOrPostCallback callback, object? state) =>
            _actions.Add(() => callback(state));

        public void BeginExternalPump() => _externalPump = true;

        public void DrainOne(int millisecondsTimeout)
        {
            if (_actions.TryTake(out var action, millisecondsTimeout)) action();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            while (!_stopped)
            {
                if (_externalPump)
                {
                    Thread.Sleep(10);
                    continue;
                }
                if (_actions.TryTake(out var action, 100)) action();
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _actions.CompleteAdding();
            _thread.Join(1000);
            _actions.Dispose();
        }
    }

    public void Dispose()
    {
        // Windows releases a closed file handle asynchronously: the settings-import
        // test writes and reads a 128 KB file, and indexing or antivirus can still hold
        // it for a moment after the assertions pass. Retry the cleanup instead of
        // turning a passing test into a spurious failure.
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal sealed class FakeCaptureService(AppPaths paths) : ICaptureService
{
    private string _pendingPath = "";
    private bool _isRecording;

    public List<string> StartedPaths { get; } = [];
    public List<int> StartedProcessIds { get; } = [];
    public int StopCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public void Start(int processId, string game)
    {
        var fps = 40 + StartedPaths.Count * 5;
        var path = Path.Combine(paths.Captures, $"capture-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, FormattableString.Invariant(
            $"Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)\n0,{processId},{fps},{fps - 10},55,97\n300,{processId},{fps},{fps - 10},55,97\n"));
        StartedPaths.Add(path);
        StartedProcessIds.Add(processId);
        _pendingPath = path;
        _isRecording = true;
    }

    public Task<string?> StopForAnalysisAsync()
    {
        StopCallCount++;
        _isRecording = false;
        var path = _pendingPath;
        _pendingPath = "";
        return Task.FromResult<string?>(path.Length == 0 ? null : path);
    }

    public LiveTelemetryState Snapshot() => new()
    {
        IsRecording = _isRecording,
        LogPath = _pendingPath
    };

    public void Dispose()
    {
        DisposeCallCount++;
        _isRecording = false;
    }
}

internal sealed class FakeConfigurationProvider : ILocalConfigurationProvider
{
    public LocalConfigurationSnapshot Snapshot { get; set; } = new();
    public GameProcessInfo? LastProcess { get; private set; }

    public Task<LocalConfigurationSnapshot> ReadAsync(
        GameProcessInfo? process, string game, bool includeDesktopResolution, string logDirectory)
    {
        LastProcess = process;
        return Task.FromResult(Snapshot);
    }
}
