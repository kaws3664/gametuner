using GameTuner.Core.Analysis;
using GameTuner.Core.Models;

namespace GameTuner.App.ViewModels;

public sealed class AnalysisRoundViewModel(AnalysisRound round, AnalysisRound? previous, AnalysisRound baseline, TuningGoal goal)
{
    public AnalysisRound Round { get; } = round;
    public int Number => Round.Number;
    public string RoundLabel => $"第 {Number} 次";
    public string Tier => ManualRecommendationPlanner.TierLabel(Round.Configuration.Tier);
    public string Average => $"{Round.Report.Metrics.AverageFps:0.0}";
    public string Low => Round.Report.Metrics.LowFps > 0 ? $"{Round.Report.Metrics.LowFps:0.0}" : "--";
    public string Delta => previous is null ? "基线" : Round.Report.Metrics.Precision != previous.Report.Metrics.Precision
        ? "口径不同" : $"{Round.Report.Metrics.AverageFps - previous.Report.Metrics.AverageFps:+0.0;-0.0;0.0} FPS";
    public string BaselineDelta => Round.Number == baseline.Number ? "第一份日志 / 基线"
        : Round.Report.Metrics.Precision != baseline.Report.Metrics.Precision ? "数据口径不同，不能直接判定提升"
        : $"相对首份日志 {Round.Report.Metrics.AverageFps - baseline.Report.Metrics.AverageFps:+0.0;-0.0;0.0} FPS";
    public string LowDelta => previous is null ? "基线"
        : Round.Report.Metrics.Precision != previous.Report.Metrics.Precision
          || Round.Report.Metrics.LowFps <= 0 || previous.Report.Metrics.LowFps <= 0 ? "不可直接比较"
        : $"{Round.Report.Metrics.LowFps - previous.Report.Metrics.LowFps:+0.0;-0.0;0.0} FPS";
    public bool MeetsTarget => Round.MeetsTarget(goal);
    public string Verdict => MeetsTarget ? "已达标" : Round.Report.Metrics.LowFps <= 0
        || Round.Report.Metrics.Precision == TelemetryPrecision.Estimated ? "低帧待确认"
        : Round.Report.Metrics.Duration < TimeSpan.FromSeconds(5) ? "片段过短" : "继续试调";
    public string Accent => MeetsTarget ? "#137568" : "#A5652A";
    public string RecommendationTitle => Round.Report.Recommendation.Title;
    public string RecommendationReason => Round.Report.Recommendation.Explanation;
    public IReadOnlyList<ManualSettingAdvice> Suggestions => Round.Report.Recommendation.Settings;
    public string Warnings => string.Join("\n", Round.Report.Warnings);
    public string Precision => Round.Report.PrecisionLabel;
    public string SourceName => System.IO.Path.GetFileName(Round.Report.SourcePath);
    public string Settings => Empty(Round.Configuration.GraphicsSettings, "尚未填写逐项画面设置");
    public string Scene => Empty(Round.Configuration.Scene, "未填写场景，需自行确认两次录制可比");
    public string GpuLoad => Round.Report.Metrics.GpuUtilization > 0 ? $"{Round.Report.Metrics.GpuUtilization:0}%" : "未记录";
    public string CpuLoad => Round.Report.Metrics.CpuUtilization > 0 ? $"{Round.Report.Metrics.CpuUtilization:0}%" : "未记录";
    public static string Empty(string text, string fallback = "未填写") => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
