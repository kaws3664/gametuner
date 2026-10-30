using GameTuner.Core.Adapters.FirstLight;
using GameTuner.Core.Models;
using GameTuner.Core.Telemetry;

namespace GameTuner.Tests;

public sealed class FirstLightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrameSmithTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Frame_log_computes_average_and_p99()
    {
        var rows = Enumerable.Repeat(16.666, 99).Append(50d);
        var csv = "Application,MsBetweenPresents\n" + string.Join('\n', rows.Select(ms => $"007FirstLight.exe,{ms}"));
        var result = PerformanceLogReader.Parse("test.csv", csv);
        Assert.Equal(100, result.SampleCount);
        Assert.Equal(20, result.LowFps);
        Assert.Equal(16.666, result.FrameTimeP99Ms, 3);
        Assert.InRange(result.AverageFps, 58, 60);
        var withOnePercentSlowFrames = PerformanceLogReader.Parse("test.csv",
            "Application,MsBetweenPresents\n" + string.Join('\n',
                Enumerable.Repeat(16.666, 98).Concat(Enumerable.Repeat(50d, 2)).Select(ms => $"007FirstLight.exe,{ms}")));
        Assert.Equal(50, withOnePercentSlowFrames.FrameTimeP99Ms);
    }

    [Fact]
    public void Invalid_log_is_rejected_instead_of_fabricating_fps() =>
        Assert.Throws<InvalidDataException>(() => PerformanceLogReader.Parse("bad.csv", "no metrics here"));

    [Fact]
    public void Nvidia_performance_csv_parses_native_statistics_and_duration()
    {
        const string header = "Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%),";
        var csv = header + "\n0.000,14944,40,30,50,95\n" +
                  string.Join('\n', Enumerable.Range(1, 300).Select(index =>
                      $"{index},14944,60,54,50,98")) + "\n";

        var result = PerformanceLogReader.Parse("NVIDIA_App_Performance_Log_test.csv", csv);

        Assert.Equal(TimeSpan.FromSeconds(300), result.Duration);
        Assert.Equal(301, result.SampleCount);
        Assert.Equal(60, result.AverageFps, 3);
        Assert.Equal(54, result.LowFps, 3);
        Assert.False(result.IsFrameAccurate);
    }

    [Fact]
    public void Nvidia_latency_only_csv_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => PerformanceLogReader.Parse(
            "NVIDIA_App_Latency_Log_test.csv",
            "Timestamp (Elapsed time in seconds),PC + DisplayLatency(MSec),Mouse Latency(MSec),System Latency (MSec),"));

    [Fact]
    public void Real_nvidia_csv_is_supported_when_sample_is_supplied()
    {
        var path = Environment.GetEnvironmentVariable("GAMETUNER_NVIDIA_SAMPLE");
        if (string.IsNullOrWhiteSpace(path)) return;

        var result = PerformanceLogReader.Parse(path, File.ReadAllText(path));

        Assert.Equal(488, result.SampleCount);
        Assert.Equal(248.039, result.Duration.TotalSeconds, 3);
        Assert.InRange(result.AverageFps, 46, 47);
        Assert.False(result.IsFrameAccurate);
    }

    [Fact]
    public async Task Collector_reads_a_log_while_it_is_still_open_and_stops_at_target()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "NVIDIA_App_Performance_Log_live.csv");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low");
        await writer.WriteLineAsync("0,1,50,45");
        await writer.WriteLineAsync("0.5,1,60,54");
        await writer.WriteLineAsync("1,1,62,55");
        await writer.FlushAsync();

        using (var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(readStream))
            Assert.Equal(TimeSpan.FromSeconds(1), PerformanceLogReader.Parse(path, reader.ReadToEnd()).Duration);
        var discovered = NvidiaLogTelemetryCollector.FindNewLog(
            new LogCaptureOptions(_root, DateTimeOffset.Now.AddSeconds(-2)));
        Assert.NotNull(discovered);
        Assert.Equal(TimeSpan.FromSeconds(1), discovered.Duration);

        var collector = new NvidiaLogTelemetryCollector(new RunningGameGuard());
        var result = await collector.CollectAsync(
            new LogCaptureOptions(_root, DateTimeOffset.Now.AddSeconds(-2), TimeSpan.FromSeconds(1)),
            null,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
        Assert.Equal(61, result.AverageFps, 1);
        Assert.Equal(TelemetryPrecision.NvidiaAggregate, result.Precision);
    }

    private sealed class RunningGameGuard : GameTuner.Core.Infrastructure.IFirstLightProcessGuard
    {
        public bool IsRunning() => true;
        public Task WaitForStartAsync(IProgress<TimeSpan>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    [Fact]
    public void Log_discovery_ignores_latency_and_old_logs()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.Now;
        var performance = Path.Combine(_root, "NVIDIA_App_Performance_Log_test.csv");
        File.WriteAllText(performance,
            "Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low\n" +
            string.Join('\n', Enumerable.Range(0, 301).Select(index => $"{index},1,60,54")));
        File.WriteAllText(Path.Combine(_root, "NVIDIA_App_Latency_Log_test.csv"),
            "Timestamp (Elapsed time in seconds),PC + DisplayLatency(MSec),");
        File.SetLastWriteTimeUtc(performance, now.UtcDateTime.AddSeconds(-3));

        var found = NvidiaLogTelemetryCollector.FindNewLog(new LogCaptureOptions(_root, now.AddMinutes(-1)));

        Assert.NotNull(found);
        Assert.Equal(performance, found.SourcePath);
    }

    [Fact]
    public void Vram_pressure_lowers_texture_not_every_setting()
    {
        var result = FirstLightOptimizationPlanner.NextCandidate(
            Current(),
            new PerformanceMetrics { GpuMemoryUsedMb = 7800, GpuMemoryTotalMb = 8192 },
            new TuningGoal { TargetFps = 60, Mode = TuningMode.FrameRate });
        Assert.Contains("TextureResolution", result.SettingOverrides.Keys);
        Assert.DoesNotContain("ShadowQuality", result.SettingOverrides.Keys);
    }

    [Fact]
    public void Cpu_pressure_targets_simulation()
    {
        var metrics = new PerformanceMetrics
        {
            Bottleneck = BottleneckKind.Cpu,
            MaxCpuCoreUtilization = 98,
            GpuUtilization = 55,
            GpuMemoryUsedMb = 4000,
            GpuMemoryTotalMb = 8192
        };
        var result = FirstLightOptimizationPlanner.NextCandidate(Current(), metrics,
            new TuningGoal { TargetFps = 60 });
        Assert.Contains("SimulationQuality", result.SettingOverrides.Keys);
        Assert.DoesNotContain("TextureResolution", result.SettingOverrides.Keys);
    }

    [Fact]
    public void Unknown_or_out_of_range_registry_values_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => FirstLightSettingsCatalog.Validate(
            new Dictionary<string, string> { ["KntProfileSaveFile"] = "1" }));
        Assert.Throws<ArgumentException>(() => FirstLightSettingsCatalog.Validate(
            new Dictionary<string, string> { ["TextureResolution"] = "999" }));
    }

    private static Dictionary<string, string> Current() => new()
    {
        ["QualityProfile"] = "5", ["ShadowQuality"] = "2", ["TextureResolution"] = "2",
        ["TextureFilter"] = "4", ["MirrorQuality"] = "2", ["LevelOfDetail"] = "1",
        ["SimulationQuality"] = "1", ["ReflectionQuality"] = "1", ["AtmosphericQuality"] = "1",
        ["SSRQuality"] = "1", ["TerrainQuality"] = "1", ["VolumetricFogQuality"] = "1",
        ["VolumetricEffectsQuality"] = "1", ["GlobalIlluminationQuality"] = "1", ["SuperSampling"] = "1000"
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
