using GameTuner.Core.Models;

namespace GameTuner.Core.Telemetry;

public static class FrameRateAnalyzer
{
    public static PerformanceMetrics Analyze(
        IReadOnlyList<FrameSample> samples,
        TimeSpan duration)
    {
        if (samples.Count < 2 || duration < TimeSpan.FromSeconds(2))
        {
            return new PerformanceMetrics
            {
                Duration = duration,
                Precision = TelemetryPrecision.UtilizationOnly
            };
        }

        var ordered = samples.OrderBy(sample => sample.Timestamp).ToArray();
        var intervals = new List<double>();
        var frameCount = 0;
        for (var index = 1; index < ordered.Length; index++)
        {
            var elapsedMs = (ordered[index].Timestamp - ordered[index - 1].Timestamp).TotalMilliseconds;
            var frames = Math.Clamp(ordered[index].AccumulatedFrames, 1, 1000);
            if (elapsedMs <= 0 || elapsedMs > 5000)
            {
                continue;
            }

            frameCount += frames;
            var perFrameMs = elapsedMs / frames;
            if (perFrameMs is > 0.5 and < 1000)
            {
                intervals.Add(perFrameMs);
            }
        }

        if (frameCount < 10 || intervals.Count < 5)
        {
            return new PerformanceMetrics
            {
                Duration = duration,
                Precision = TelemetryPrecision.UtilizationOnly
            };
        }

        intervals.Sort();
        var p99Index = Math.Clamp(
            (int)Math.Ceiling(intervals.Count * 0.99) - 1,
            0,
            intervals.Count - 1);
        var p99 = intervals[p99Index];
        return new PerformanceMetrics
        {
            AverageFps = frameCount / duration.TotalSeconds,
            FrameTimeP99Ms = p99,
            LowFps = p99 > 0 ? 1000d / p99 : 0,
            Duration = duration,
            Precision = TelemetryPrecision.Estimated
        };
    }

    public static PerformanceMetrics ApplyBenchmark(
        PerformanceMetrics live,
        Rdr2BenchmarkSummary benchmark)
    {
        return new PerformanceMetrics
        {
            AverageFps = benchmark.AverageFps,
            LowFps = benchmark.LowFps,
            FrameTimeP99Ms = benchmark.P99FrameTimeMs,
            GpuUtilization = live.GpuUtilization,
            GpuMemoryUsedMb = live.GpuMemoryUsedMb,
            GpuMemoryTotalMb = live.GpuMemoryTotalMb,
            CpuUtilization = live.CpuUtilization,
            MaxCpuCoreUtilization = live.MaxCpuCoreUtilization,
            GpuTemperatureC = live.GpuTemperatureC,
            GpuPowerW = live.GpuPowerW,
            Duration = live.Duration,
            Precision = TelemetryPrecision.FrameAccurate
        };
    }
}
