using GameTuner.Core.Adapters;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;
using GameTuner.Core.Models;
using GameTuner.Core.Optimization;
using GameTuner.Core.Profiles;
using GameTuner.Core.Telemetry;

namespace GameTuner.Tests;

public sealed class GameTuningSessionControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameTunerSessionTests",
        Guid.NewGuid().ToString("N"));

    public GameTuningSessionControllerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task RunAsync_completes_three_rounds_and_applies_best_snapshot()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "system.xml"));
        paths.EnsureCreated();
        var config = new FakeConfig(_root);
        var nvidia = new FakeNvidia();
        var telemetry = new FakeTelemetry(
            Metrics(45, 35),
            Metrics(64, 57),
            Metrics(65, 58));
        var controller = new GameTuningSessionController(
            nvidia,
            config,
            telemetry,
            new ExitedGuard(),
            new TuningEngine(),
            new RuleBasedAiAdvisor(),
            new SessionStore(paths, new JsonFileStore()),
            new AppSettings
            {
                ExitWaitTimeoutSeconds = 1
            },
            paths);

        var session = await controller.RunAsync(new TuningGoal
        {
            TargetFps = 60,
            Mode = TuningMode.Balanced
        });

        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(3, session.Rounds.Count);
        Assert.NotNull(session.FinalCandidate);
        Assert.Equal(3, nvidia.PresetsApplied.Count);
        Assert.Equal(3, config.RestoreCalls.Count);
    }

    [Fact]
    public async Task RunAsync_stops_after_cpu_bound_measurement()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "system.xml"));
        paths.EnsureCreated();
        var controller = new GameTuningSessionController(
            new FakeNvidia(),
            new FakeConfig(_root),
            new FakeTelemetry(Metrics(52, 44, cpu: 96, maxCpu: 99, gpu: 58)),
            new ExitedGuard(),
            new TuningEngine(),
            new RuleBasedAiAdvisor(),
            new SessionStore(paths, new JsonFileStore()),
            new AppSettings
            {
                ExitWaitTimeoutSeconds = 1
            },
            paths);

        var session = await controller.RunAsync(new TuningGoal
        {
            TargetFps = 60,
            Mode = TuningMode.FrameRate
        });

        Assert.Single(session.Rounds);
        Assert.Contains("CPU 瓶颈", session.Message);
    }

    [Fact]
    public async Task RunAsync_auto_exits_game_between_rounds_when_user_does_not_quit()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "system.xml"));
        paths.EnsureCreated();
        var guard = new NeverExitingGuard();
        var controller = new GameTuningSessionController(
            new FakeNvidia(),
            new FakeConfig(_root),
            new FakeTelemetry(
                Metrics(45, 35),
                Metrics(64, 57),
                Metrics(65, 58)),
            guard,
            new TuningEngine(),
            new RuleBasedAiAdvisor(),
            new SessionStore(paths, new JsonFileStore()),
            new AppSettings
            {
                BenchmarkExitGraceSeconds = 1,
                ExitWaitTimeoutSeconds = 1
            },
            paths);

        var session = await controller.RunAsync(new TuningGoal
        {
            TargetFps = 60,
            Mode = TuningMode.Balanced
        });

        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(3, session.Rounds.Count);
        Assert.Equal(3, guard.StopCalls);
    }

    [Fact]
    public async Task RunAsync_manual_exit_mode_keeps_old_behavior_when_grace_is_zero()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "system.xml"));
        paths.EnsureCreated();
        var controller = new GameTuningSessionController(
            new FakeNvidia(),
            new FakeConfig(_root),
            new FakeTelemetry(
                Metrics(45, 35),
                Metrics(64, 57),
                Metrics(65, 58)),
            new ExitedGuard(),
            new TuningEngine(),
            new RuleBasedAiAdvisor(),
            new SessionStore(paths, new JsonFileStore()),
            new AppSettings
            {
                BenchmarkExitGraceSeconds = 0,
                ExitWaitTimeoutSeconds = 1
            },
            paths);

        var session = await controller.RunAsync(new TuningGoal
        {
            TargetFps = 60,
            Mode = TuningMode.Balanced
        });

        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(3, session.Rounds.Count);
    }

    private static PerformanceMetrics Metrics(
        double average,
        double low,
        double cpu = 55,
        double maxCpu = 65,
        double gpu = 96) =>
        new()
        {
            AverageFps = average,
            LowFps = low,
            GpuUtilization = gpu,
            CpuUtilization = cpu,
            MaxCpuCoreUtilization = maxCpu,
            GpuMemoryUsedMb = 5000,
            GpuMemoryTotalMb = 8192,
            Duration = TimeSpan.FromMinutes(2),
            Precision = TelemetryPrecision.FrameAccurate
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeNvidia : INvidiaAppService
    {
        public List<QualityPreset> PresetsApplied { get; } = [];

        public Task<IReadOnlyList<NvidiaApplication>> GetApplicationsAsync(
            string? game = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NvidiaApplication>>(
            [
                new NvidiaApplication(
                    "Red Dead Redemption 2",
                    true,
                    [
                        QualityPreset.Performance,
                        QualityPreset.Balanced,
                        QualityPreset.Recommended,
                        QualityPreset.Quality
                    ])
            ]);

        public Task SetGamePresetAsync(
            string game,
            QualityPreset preset,
            CancellationToken cancellationToken = default)
        {
            PresetsApplied.Add(preset);
            return Task.CompletedTask;
        }

        public Task LaunchGameAsync(string game, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsGameRunningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsOverlayAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeConfig(string root) : IGameProfileAdapter
    {
        private int _version;

        public List<string> RestoreCalls { get; } = [];

        public GameProfile Profile => GameProfiles.RedDeadRedemption2;

        public Task<GameConfigSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            _version++;
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shadowQuality"] = "kSettingLevel_Ultra",
                ["textureQuality"] = "kSettingLevel_Ultra",
                ["volumetricsQuality"] = "kSettingLevel_Ultra"
            };
            return Task.FromResult(new GameConfigSnapshot(
                Path.Combine(root, "system.xml"),
                $"<version>{_version}</version>",
                $"hash-{_version}",
                settings));
        }

        public Task<string> BackupAsync(
            string label,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(root, "original.xml"));

        public Task<GameConfigSnapshot> ApplyOverridesAsync(
            IReadOnlyDictionary<string, string> overrides,
            CancellationToken cancellationToken = default) =>
            ReadAsync(cancellationToken);

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
        {
            RestoreCalls.Add(backupPath);
            return Task.CompletedTask;
        }

        public TuningCandidate CreateFallbackCandidate(
            IReadOnlyDictionary<string, string> currentSettings,
            PerformanceMetrics metrics,
            TuningGoal goal) =>
            new()
            {
                Label = "XML 微调",
                SettingOverrides = new Dictionary<string, string>
                {
                    ["volumetricsQuality"] = "kSettingLevel_High"
                }
            };
    }

    private sealed class FakeTelemetry(params PerformanceMetrics[] results) : IGameTelemetryCollector
    {
        private int _index;

        public Task<PerformanceMetrics> CollectAsync(
            GameTelemetryOptions options,
            IProgress<PerformanceMetrics>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(results[Math.Min(_index++, results.Length - 1)]);
    }

    private sealed class ExitedGuard : IGameProcessGuard
    {
        public bool IsAnyRunning(IEnumerable<string> processNames) => false;
        public Task WaitForStartAsync(
            string processName,
            IProgress<TimeSpan>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(
            IEnumerable<string> processNames,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<bool> StopAsync(
            IEnumerable<string> processNames,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class NeverExitingGuard : IGameProcessGuard
    {
        public int StopCalls { get; private set; }

        public bool IsAnyRunning(IEnumerable<string> processNames) => false;
        public Task WaitForStartAsync(
            string processName,
            IProgress<TimeSpan>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(
            IEnumerable<string> processNames,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<bool> StopAsync(
            IEnumerable<string> processNames,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.FromResult(true);
        }
    }
}
