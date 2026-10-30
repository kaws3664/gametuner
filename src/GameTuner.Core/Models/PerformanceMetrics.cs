namespace GameTuner.Core.Models;

public sealed class PerformanceMetrics
{
    public double AverageFps { get; set; }
    public double LowFps { get; set; }
    public double FrameTimeP99Ms { get; set; }
    public double GpuUtilization { get; set; }
    public double GpuMemoryUsedMb { get; set; }
    public double GpuMemoryTotalMb { get; set; }
    public double CpuUtilization { get; set; }
    public double MaxCpuCoreUtilization { get; set; }
    public double GpuTemperatureC { get; set; }
    public double GpuPowerW { get; set; }
    public TimeSpan Duration { get; set; }
    public TelemetryPrecision Precision { get; set; } = TelemetryPrecision.Estimated;
    public BottleneckKind Bottleneck { get; set; } = BottleneckKind.Unknown;

    public double VramRatio =>
        GpuMemoryTotalMb <= 0 ? 0 : GpuMemoryUsedMb / GpuMemoryTotalMb;

    public bool HasUsableFrames =>
        AverageFps > 1 && LowFps > 0 && Duration >= TimeSpan.FromSeconds(5);
}
