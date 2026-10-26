using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.Core.Optimization;

public sealed class TuningEngine
{
    private static readonly QualityPreset[] PresetOrder =
    [
        QualityPreset.Performance,
        QualityPreset.Balanced,
        QualityPreset.Recommended,
        QualityPreset.Quality
    ];

    public TuningCandidate InitialCandidate(
        TuningGoal goal,
        IReadOnlyCollection<QualityPreset> available)
    {
        var requested = goal.Mode switch
        {
            TuningMode.Quality => QualityPreset.Quality,
            TuningMode.FrameRate => QualityPreset.Performance,
            _ => QualityPreset.Recommended
        };
        var preset = NearestAvailable(requested, available);
        return new TuningCandidate
        {
            Label = $"NVIDIA {preset}",
            Preset = preset,
            Rationale = goal.Mode switch
            {
                TuningMode.Quality => "从 NVIDIA 画质档开始，优先保留视觉效果。",
                TuningMode.FrameRate => "从 NVIDIA 性能档开始，优先接近目标帧率。",
                _ => "从 NVIDIA 推荐档开始，先建立平衡基线。"
            }
        };
    }

    public TuningCandidate NextPresetCandidate(
        TuningGoal goal,
        TuningCandidate current,
        PerformanceMetrics metrics,
        IReadOnlyCollection<QualityPreset> available)
    {
        if (current.Preset is null)
        {
            return current.Clone();
        }

        var currentIndex = Array.IndexOf(PresetOrder, current.Preset.Value);
        var meets = Evaluate(goal, metrics).Outcome == RoundOutcome.MeetsTarget;
        var direction = meets ? 1 : -1;
        var targetIndex = Math.Clamp(currentIndex + direction, 0, PresetOrder.Length - 1);

        for (var index = targetIndex;
             index >= 0 && index < PresetOrder.Length;
             index += direction)
        {
            if (available.Contains(PresetOrder[index]))
            {
                return new TuningCandidate
                {
                    Label = $"NVIDIA {PresetOrder[index]}",
                    Preset = PresetOrder[index],
                    Rationale = meets
                        ? "基线已达标，尝试提高一档画质以确认是否仍有性能余量。"
                        : "基线未达标，向低一档 NVIDIA 预设回退。"
                };
            }
        }

        return current.Clone();
    }

    public (RoundOutcome Outcome, double Score) Evaluate(
        TuningGoal goal,
        PerformanceMetrics metrics)
    {
        if (!metrics.HasUsableFrames)
        {
            return (RoundOutcome.Invalid, 0);
        }

        var averageRatio = metrics.AverageFps / goal.TargetFps;
        var lowRatio = metrics.LowFps / goal.MinimumLowFps;
        var outcome = averageRatio >= 1 && lowRatio >= 1
            ? RoundOutcome.MeetsTarget
            : RoundOutcome.MissesTarget;

        var averageScore = Math.Clamp(averageRatio, 0, 1.5) / 1.5;
        var lowScore = Math.Clamp(lowRatio, 0, 1.5) / 1.5;
        var targetBonus = outcome == RoundOutcome.MeetsTarget ? 0.12 : 0;
        return (outcome, averageScore * 0.35 + lowScore * 0.35 + targetBonus);
    }

    public double QualityWeight(TuningCandidate candidate)
    {
        if (candidate.Preset is null)
        {
            return 0.55;
        }

        return Array.IndexOf(PresetOrder, candidate.Preset.Value) / 3d;
    }

    public double ScoreCandidate(
        TuningGoal goal,
        TuningCandidate candidate,
        PerformanceMetrics metrics)
    {
        var (outcome, performanceScore) = Evaluate(goal, metrics);
        if (outcome == RoundOutcome.Invalid)
        {
            return 0;
        }

        var qualityScore = QualityWeight(candidate);
        var (performanceWeight, qualityWeight) = goal.Mode switch
        {
            TuningMode.Quality => (0.45, 0.55),
            TuningMode.FrameRate => (0.8, 0.2),
            _ => (0.6, 0.4)
        };
        return performanceScore * performanceWeight + qualityScore * qualityWeight;
    }

    public RoundResult SelectBest(IReadOnlyCollection<RoundResult> rounds, TuningGoal goal) =>
        rounds
            .Where(round => round.Metrics.HasUsableFrames)
            .OrderByDescending(round =>
                round.Outcome == RoundOutcome.MeetsTarget)
            .ThenByDescending(round => ScoreCandidate(goal, round.Candidate, round.Metrics))
            .ThenByDescending(round => round.Metrics.LowFps)
            .First();

    public bool HasMeaningfulDifference(
        RoundResult first,
        RoundResult second,
        double threshold = 0.02)
    {
        var averageDelta = RelativeDelta(first.Metrics.AverageFps, second.Metrics.AverageFps);
        var lowDelta = RelativeDelta(first.Metrics.LowFps, second.Metrics.LowFps);
        return averageDelta >= threshold || lowDelta >= threshold;
    }

    public static bool IsCpuBound(PerformanceMetrics metrics) =>
        metrics.Bottleneck == BottleneckKind.Cpu;

    private static double RelativeDelta(double first, double second)
    {
        var baseline = Math.Max(1, (Math.Abs(first) + Math.Abs(second)) / 2);
        return Math.Abs(first - second) / baseline;
    }

    private static QualityPreset NearestAvailable(
        QualityPreset requested,
        IReadOnlyCollection<QualityPreset> available)
    {
        if (available.Count == 0)
        {
            throw new InvalidOperationException("NVIDIA App 未提供任何可用优化预设。");
        }

        if (available.Contains(requested))
        {
            return requested;
        }

        var requestedIndex = Array.IndexOf(PresetOrder, requested);
        return available
            .OrderBy(preset => Math.Abs(Array.IndexOf(PresetOrder, preset) - requestedIndex))
            .First();
    }
}
