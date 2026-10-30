using GameTuner.Core.Models;
using GameTuner.Core.Optimization;

namespace GameTuner.Tests;

public sealed class TuningEngineTests
{
    private readonly TuningEngine _engine = new();
    private readonly TuningGoal _goal = new() { TargetFps = 60, Mode = TuningMode.Balanced };

    [Fact]
    public void InitialCandidate_uses_quality_for_quality_mode()
    {
        var candidate = _engine.InitialCandidate(
            new TuningGoal { Mode = TuningMode.Quality },
            [QualityPreset.Performance, QualityPreset.Quality]);

        Assert.Equal(QualityPreset.Quality, candidate.Preset);
    }

    [Fact]
    public void InitialCandidate_uses_performance_for_frame_rate_mode()
    {
        var candidate = _engine.InitialCandidate(
            new TuningGoal { Mode = TuningMode.FrameRate },
            [QualityPreset.Performance, QualityPreset.Balanced]);

        Assert.Equal(QualityPreset.Performance, candidate.Preset);
    }

    [Fact]
    public void Evaluate_requires_average_and_low_fps()
    {
        var result = _engine.Evaluate(
            _goal,
            Metrics(average: 65, low: 50, gpu: 99, cpu: 50, maxCpu: 60));

        Assert.Equal(RoundOutcome.MissesTarget, result.Outcome);
    }

    [Fact]
    public void Evaluate_meets_target_when_both_thresholds_pass()
    {
        var result = _engine.Evaluate(
            _goal,
            Metrics(average: 65, low: 55, gpu: 99, cpu: 50, maxCpu: 60));

        Assert.Equal(RoundOutcome.MeetsTarget, result.Outcome);
    }

    [Theory]
    [InlineData(98, 60, 25, BottleneckKind.Gpu)]
    [InlineData(55, 80, 95, BottleneckKind.Cpu)]
    [InlineData(99, 60, 8000, BottleneckKind.Vram)]
    public void ClassifyBottleneck_detects_primary_constraint(
        double gpu,
        double cpu,
        double maxCpuOrVram,
        BottleneckKind expected)
    {
        var metrics = Metrics(
            average: 60,
            low: 55,
            gpu: gpu,
            cpu: cpu,
            metricsVram: expected == BottleneckKind.Vram ? maxCpuOrVram : 4000,
            maxCpu: expected == BottleneckKind.Vram ? 60 : maxCpuOrVram);

        Assert.Equal(expected, GameTuner.Core.Telemetry.Rdr2TelemetryCollector.ClassifyBottleneck(metrics));
    }

    [Fact]
    public void SelectBest_prefers_target_meeting_candidate_even_with_lower_quality()
    {
        var quality = Round(
            1,
            QualityPreset.Quality,
            Metrics(45, 35, 99, 50, 4000, 60));
        var performance = Round(
            2,
            QualityPreset.Performance,
            Metrics(70, 62, 80, 60, 4000, 70));

        var best = _engine.SelectBest([quality, performance], _goal);

        Assert.Same(performance, best);
    }

    [Fact]
    public void NextPresetCandidate_drops_a_preset_when_missing_target()
    {
        var current = new TuningCandidate
        {
            Preset = QualityPreset.Quality,
            Label = "NVIDIA Quality"
        };

        var next = _engine.NextPresetCandidate(
            _goal,
            current,
            Metrics(45, 35, 99, 50, 4000, 60),
            [QualityPreset.Quality, QualityPreset.Recommended]);

        Assert.Equal(QualityPreset.Recommended, next.Preset);
    }

    [Fact]
    public void NextPresetCandidate_raises_quality_when_target_is_met()
    {
        var current = new TuningCandidate
        {
            Preset = QualityPreset.Recommended,
            Label = "NVIDIA Recommended"
        };

        var next = _engine.NextPresetCandidate(
            _goal,
            current,
            Metrics(70, 63, 80, 50, 4000, 60),
            [QualityPreset.Recommended, QualityPreset.Quality]);

        Assert.Equal(QualityPreset.Quality, next.Preset);
    }

    [Fact]
    public void HasMeaningfulDifference_ignores_small_noise()
    {
        var first = Round(1, QualityPreset.Quality, Metrics(60, 55, 99, 50, 4000, 60));
        var second = Round(2, QualityPreset.Recommended, Metrics(60.5, 55.4, 98, 50, 4000, 60));

        Assert.False(_engine.HasMeaningfulDifference(first, second));
    }

    [Fact]
    public void HasMeaningfulDifference_detects_material_change()
    {
        var first = Round(1, QualityPreset.Quality, Metrics(60, 55, 99, 50, 4000, 60));
        var second = Round(2, QualityPreset.Recommended, Metrics(68, 62, 96, 50, 4000, 60));

        Assert.True(_engine.HasMeaningfulDifference(first, second));
    }

    [Theory]
    [InlineData("我要60帧，画质优先", 60, TuningMode.Quality)]
    [InlineData("目标 120 fps，帧数优先", 120, TuningMode.FrameRate)]
    [InlineData("75帧，平衡", 75, TuningMode.Balanced)]
    public void GoalParser_parses_natural_language(string text, double fps, TuningMode mode)
    {
        var goal = GoalParser.Parse(text);

        Assert.Equal(fps, goal.TargetFps);
        Assert.Equal(mode, goal.Mode);
    }

    private static RoundResult Round(
        int number,
        QualityPreset preset,
        PerformanceMetrics metrics) =>
        new()
        {
            RoundNumber = number,
            Candidate = new TuningCandidate { Preset = preset, Label = preset.ToString() },
            Metrics = metrics,
            Outcome = metrics.AverageFps >= 60 && metrics.LowFps >= 54
                ? RoundOutcome.MeetsTarget
                : RoundOutcome.MissesTarget
        };

    private static PerformanceMetrics Metrics(
        double average,
        double low,
        double gpu,
        double cpu,
        double? metricsVram = null,
        double maxCpu = 60) =>
        new()
        {
            AverageFps = average,
            LowFps = low,
            GpuUtilization = gpu,
            CpuUtilization = cpu,
            MaxCpuCoreUtilization = maxCpu,
            GpuMemoryUsedMb = metricsVram ?? 4000,
            GpuMemoryTotalMb = 8192,
            Duration = TimeSpan.FromMinutes(2)
        };
}
