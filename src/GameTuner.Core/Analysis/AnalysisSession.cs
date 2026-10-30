using GameTuner.Core.Models;

namespace GameTuner.Core.Analysis;

public sealed record PlayerConfiguration
{
    public ManualQualityTier Tier { get; init; }
    public string GraphicsSettings { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string Cpu { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string Memory { get; init; } = "";
    public string Scene { get; init; } = "";
}

public sealed record AnalysisRound(int Number, LogAnalysisReport Report, PlayerConfiguration Configuration)
{
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.Now;
    public bool MeetsTarget(TuningGoal goal) =>
        Report.Metrics.HasUsableFrames
        && double.IsFinite(Report.Metrics.AverageFps) && double.IsFinite(Report.Metrics.LowFps)
        && Report.Metrics.AverageFps >= goal.TargetFps
        && Report.Metrics.LowFps >= goal.MinimumLowFps
        && Report.Metrics.Precision is TelemetryPrecision.FrameAccurate or TelemetryPrecision.NvidiaAggregate;
}

public sealed class AnalysisSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Game { get; init; } = "";
    public TuningGoal Goal { get; init; } = new();
    public List<AnalysisRound> Rounds { get; init; } = [];
    public int? SavedRoundNumber { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
}
