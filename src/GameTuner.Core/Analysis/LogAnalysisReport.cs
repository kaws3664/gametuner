using GameTuner.Core.Models;

namespace GameTuner.Core.Analysis;

public enum ManualQualityTier { Unknown, Low, Medium, High, Ultra }

public sealed record ManualSettingAdvice(string Setting, string SuggestedValue, string Reason);

public sealed record ManualRecommendation(
    string Title,
    string Explanation,
    ManualQualityTier? SuggestedTier,
    IReadOnlyList<ManualSettingAdvice> Settings);

public sealed record LogAnalysisReport(
    string SourcePath,
    PerformanceMetrics Metrics,
    int SampleCount,
    string PrecisionLabel,
    IReadOnlyList<string> Warnings,
    ManualRecommendation Recommendation)
{
    public string SourceHash { get; init; } = "";
}
