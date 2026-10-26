using GameTuner.Core.Models;

namespace GameTuner.App.ViewModels;

public sealed class RoundCardViewModel(RoundResult round)
{
    public string Number => $"第{round.RoundNumber}轮";
    public string Candidate => round.Candidate.Label;
    public string AverageFps => $"{round.Metrics.AverageFps:0.0}";
    public string LowFps => $"{round.Metrics.LowFps:0.0}";
    public string Gpu => $"{round.Metrics.GpuUtilization:0}%";
    public string Cpu => $"{round.Metrics.CpuUtilization:0}%";
    public string Vram => round.Metrics.GpuMemoryTotalMb > 0
        ? $"{round.Metrics.GpuMemoryUsedMb / 1024:0.0}/{round.Metrics.GpuMemoryTotalMb / 1024:0.0} GB"
        : "N/A";
    public string Verdict => round.Outcome == RoundOutcome.MeetsTarget ? "达标" : "未达标";
    public string Score => $"{round.Score:0.00}";
    public string Explanation => round.Explanation;
    public string Precision => round.Metrics.Precision switch
    {
        TelemetryPrecision.FrameAccurate => "NVIDIA 日志实测",
        TelemetryPrecision.NvidiaAggregate => "NVIDIA 分段统计",
        TelemetryPrecision.Estimated => "屏幕变化估算",
        _ => "精度不足"
    };
    public string Accent => round.Outcome == RoundOutcome.MeetsTarget ? "#B8F33D" : "#FF9D45";
}
