namespace GameTuner.Core.Models;

public sealed class TuningGoal
{
    public TuningMode Mode { get; set; } = TuningMode.Balanced;
    public double TargetFps { get; set; } = 60;
    public double LowFpsRatio { get; set; } = 0.9;

    public double MinimumLowFps => TargetFps * LowFpsRatio;

    public void Validate()
    {
        if (TargetFps is < 15 or > 480)
        {
            throw new ArgumentOutOfRangeException(nameof(TargetFps), "目标帧率必须在 15 到 480 之间。");
        }

        if (LowFpsRatio is < 0.5 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LowFpsRatio), "低帧比例必须在 0.5 到 1 之间。");
        }
    }
}
