using System.Globalization;
using GameTuner.Core.Analysis;
using GameTuner.Core.Models;

namespace GameTuner.Tests;

public sealed class LogAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrameSmith-AnalysisTests", Guid.NewGuid().ToString("N"));
    private readonly LogAnalysisService _service = new();
    public LogAnalysisTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Nvidia_log_uses_elapsed_intervals_and_hardware_from_the_same_file_without_writing()
    {
        var path = Write("any-game.csv", """
            Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)
            100,77,40,30,30,94
            110,77,40,30,30,94
            140,77,80,60,70,98
            """);
        var before = File.ReadAllBytes(path);
        var report = await _service.AnalyzeAsync(path, new TuningGoal { TargetFps = 90 }, ManualQualityTier.High);
        Assert.Equal(70, report.Metrics.AverageFps);
        Assert.Equal(52.5, report.Metrics.LowFps);
        Assert.Equal(60, report.Metrics.CpuUtilization);
        Assert.Equal(97, report.Metrics.GpuUtilization);
        Assert.Equal(TimeSpan.FromSeconds(40), report.Metrics.Duration);
        Assert.Equal(BottleneckKind.Gpu, report.Metrics.Bottleneck);
        Assert.Equal(ManualQualityTier.Medium, report.Recommendation.SuggestedTier);
        Assert.Equal(0, report.Metrics.FrameTimeP99Ms);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Missing_low_fps_does_not_get_invented_or_mark_target_as_met()
    {
        var path = Write("fps-only.csv", """
            Timestamp (Elapsed time in seconds),FPS,GPU1 Utilization(%)
            0,75,95
            300,75,95
            """);
        var goal = new TuningGoal();
        var report = await _service.AnalyzeAsync(path, goal, ManualQualityTier.High);
        Assert.Equal(0, report.Metrics.LowFps);
        Assert.False(new AnalysisRound(1, report, new PlayerConfiguration()).MeetsTarget(goal));
        Assert.Contains(report.Warnings, warning => warning.Contains("\u4f4e\u5e27"));
        Assert.Null(report.Recommendation.SuggestedTier);
    }

    [Fact]
    public async Task Incomplete_low_coverage_is_reported_unknown_instead_of_selectively_averaged()
    {
        var path = Write("partial.csv", """
            Timestamp (Elapsed time in seconds),FPS,FPS 1(%) Low
            0,75,65
            10,75,N/A
            300,75,65
            """);
        var report = await _service.AnalyzeAsync(path, new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(75, report.Metrics.AverageFps);
        Assert.Equal(0, report.Metrics.LowFps);
        Assert.Equal(BottleneckKind.Unknown, report.Metrics.Bottleneck);
    }

    [Fact]
    public async Task Unknown_current_tier_never_invents_a_specific_current_or_target_preset()
    {
        var report = await _service.AnalyzeAsync(Write("unknown-game.csv", Nvidia(40, 30)),
            new TuningGoal(), ManualQualityTier.Unknown);
        Assert.Null(report.Recommendation.SuggestedTier);
        Assert.NotEmpty(report.Recommendation.Settings);
        Assert.Equal(0, report.Metrics.GpuMemoryTotalMb);
        Assert.NotEqual(BottleneckKind.Vram, report.Metrics.Bottleneck);
    }

    [Fact]
    public async Task Cpu_bottleneck_keeps_whole_preset_and_texture_quality()
    {
        var report = await _service.AnalyzeAsync(Write("cpu.csv", Nvidia(40, 30, gpu: 65, cpu: 95)),
            new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(BottleneckKind.Cpu, report.Metrics.Bottleneck);
        Assert.Null(report.Recommendation.SuggestedTier);
        Assert.Contains(report.Recommendation.Settings, setting => setting.Setting.Contains("\u6a21\u62df"));
        Assert.Contains(report.Recommendation.Settings, setting =>
            setting.Setting.Contains("\u7eb9\u7406") && setting.SuggestedValue.Contains("\u4fdd\u6301"));
    }

    [Fact]
    public async Task Recorded_vram_pressure_prioritizes_texture_but_not_unrecorded_vram()
    {
        var path = Write("vram.csv", """
            Timestamp (Elapsed time in seconds),FPS,FPS 1(%) Low,GPU1 Utilization(%),GPU1 Memory Used(MB),GPU1 Memory Total(MB)
            0,40,30,94,7800,8192
            300,40,30,94,7900,8192
            """);
        var report = await _service.AnalyzeAsync(path, new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(BottleneckKind.Vram, report.Metrics.Bottleneck);
        Assert.Equal(7900, report.Metrics.GpuMemoryUsedMb);
        Assert.Contains("\u7eb9\u7406", report.Recommendation.Settings[0].Setting);
    }

    [Theory]
    [InlineData("Timestamp (Elapsed time in seconds),FPS\n0,60\n0,60\n")]
    [InlineData("Timestamp (Elapsed time in seconds),FPS\n20,60\n10,60\n")]
    [InlineData("Timestamp (Elapsed time in seconds),PID,FPS\n0,1,60\n300,2,60\n")]
    [InlineData("average FPS,60\n")]
    [InlineData("")]
    public async Task Invalid_or_mixed_log_is_rejected(string content)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.AnalyzeAsync(
            Write("invalid.csv", content), new TuningGoal(), ManualQualityTier.High));
    }

    [Fact]
    public async Task Frame_log_computes_low_from_slowest_one_percent()
    {
        var path = Write("any-vendor.csv", "MsBetweenPresents\n" +
            string.Join('\n', Enumerable.Repeat("10", 990).Concat(Enumerable.Repeat("50", 10))));
        var report = await _service.AnalyzeAsync(path, new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(1000 / 10.4, report.Metrics.AverageFps, 5);
        Assert.Equal(20, report.Metrics.LowFps);
        Assert.Equal(10, report.Metrics.FrameTimeP99Ms);
        Assert.Equal(TelemetryPrecision.FrameAccurate, report.Metrics.Precision);
        Assert.Equal(1000, report.SampleCount);
    }

    [Fact]
    public async Task Benchmark_p99_equivalent_does_not_claim_exact_low_or_stop_comparison()
    {
        var path = Write("Benchmark.txt", """
            Frames Per Second (Higher is better) Min, Max, Avg
            Pass 0, 50.00, 90.00, 80.00
            Pass 1, 55.00, 95.00, 82.00
            Percentiles in ms for pass 0
            99%, 16.00
            """);
        var goal = new TuningGoal();
        var report = await _service.AnalyzeAsync(path, goal, ManualQualityTier.Medium);
        Assert.Equal(81, report.Metrics.AverageFps);
        Assert.Equal(62.5, report.Metrics.LowFps);
        Assert.False(new AnalysisRound(1, report, new PlayerConfiguration()).MeetsTarget(goal));
        Assert.Equal(TelemetryPrecision.Estimated, report.Metrics.Precision);
        Assert.Contains("\u8865\u5145", report.Recommendation.Title);
    }

    [Fact]
    public async Task Met_target_keeps_actual_current_tier_instead_of_recommending_more_reduction()
    {
        var report = await _service.AnalyzeAsync(Write("met.csv", Nvidia(65, 58)),
            new TuningGoal(), ManualQualityTier.Medium);
        Assert.True(new AnalysisRound(1, report, new PlayerConfiguration()).MeetsTarget(new TuningGoal()));
        Assert.Equal(ManualQualityTier.Medium, report.Recommendation.SuggestedTier);
        Assert.All(report.Recommendation.Settings, item => Assert.DoesNotContain("\u964d\u4f4e", item.SuggestedValue));
    }

    [Fact]
    public async Task File_content_fingerprint_is_stable_across_names_and_changes_when_recording_changes()
    {
        var first = await _service.AnalyzeAsync(Write("one.csv", Nvidia(40, 30)), new TuningGoal(), ManualQualityTier.High);
        var copy = await _service.AnalyzeAsync(Write("renamed.csv", Nvidia(40, 30)), new TuningGoal(), ManualQualityTier.High);
        var updated = await _service.AnalyzeAsync(Write("one.csv", Nvidia(45, 35)), new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(first.SourceHash, copy.SourceHash);
        Assert.NotEqual(first.SourceHash, updated.SourceHash);
    }

    [Fact]
    public async Task Partial_gpu_measurements_do_not_claim_a_gpu_bottleneck_from_only_the_available_intervals()
    {
        var path = Write("partial-gpu.csv", """
            Timestamp (Elapsed time in seconds),FPS,FPS 1(%) Low,GPU1 Utilization(%),CPU Utilization(%)
            0,45,35,98,55
            10,45,35,N/A,55
            300,45,35,98,55
            """);
        var report = await _service.AnalyzeAsync(path, new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(0, report.Metrics.GpuUtilization);
        Assert.Equal(BottleneckKind.Unknown, report.Metrics.Bottleneck);
        Assert.Null(report.Recommendation.SuggestedTier);
    }

    [Fact]
    public async Task Frame_log_with_multiple_processes_is_not_silently_combined()
    {
        var path = Write("mixed-frame.csv", "ProcessID,MsBetweenPresents\n"
            + string.Join('\n', Enumerable.Range(0, 20).Select(index => $"{index % 2 + 1},16")));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.AnalyzeAsync(path, new TuningGoal(), ManualQualityTier.High));
    }

    [Fact]
    public async Task Tiny_fast_frame_fragment_does_not_stop_iteration_as_a_proven_configuration()
    {
        var report = await _service.AnalyzeAsync(Write("tiny-frame.csv", "FrameTime\n"
            + string.Join('\n', Enumerable.Repeat("10", 10))), new TuningGoal(), ManualQualityTier.High);
        Assert.Equal(100, report.Metrics.AverageFps);
        Assert.Equal(100, report.Metrics.LowFps);
        Assert.False(new AnalysisRound(1, report, new PlayerConfiguration()).MeetsTarget(new TuningGoal()));
        Assert.Contains("\u8865\u5145", report.Recommendation.Title);
    }

    [Fact]
    public void Latest_log_skips_empty_and_latency_files()
    {
        var performance = Write("Performance.csv", Nvidia(40, 30));
        Write("new-latency_Latency_Log.csv", "ignore");
        Write("empty.csv", "");
        Assert.Equal(performance, _service.FindLatestLog(_root));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Nvidia(double fps, double low, double gpu = 97, double cpu = 55) =>
        FormattableString.Invariant($"Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)\n0,77,{fps},{low},{cpu},{gpu}\n300,77,{fps},{low},{cpu},{gpu}\n");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
