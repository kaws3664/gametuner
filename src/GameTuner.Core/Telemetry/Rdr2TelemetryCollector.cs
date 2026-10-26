using System.Diagnostics;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.Core.Telemetry;

public sealed record CaptureOptions(
    string ProcessName,
    string BenchmarksDirectory,
    BenchmarkBaseline BenchmarkBaseline);

public interface ITelemetryCollector
{
    Task<PerformanceMetrics> CollectAsync(
        CaptureOptions options,
        IProgress<PerformanceMetrics>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class Rdr2TelemetryCollector(
    NvidiaSmiReader? gpuReader = null,
    CpuPerformanceReader? cpuReader = null,
    IProcessGuard? processGuard = null) : ITelemetryCollector, IGameTelemetryCollector
{
    private readonly NvidiaSmiReader _gpuReader = gpuReader ?? new NvidiaSmiReader();
    private readonly CpuPerformanceReader _cpuReader = cpuReader ?? new CpuPerformanceReader();
    private readonly IProcessGuard _processGuard = processGuard ?? new Rdr2ProcessGuard();

    Task<PerformanceMetrics> IGameTelemetryCollector.CollectAsync(
        GameTelemetryOptions options,
        IProgress<PerformanceMetrics>? progress,
        CancellationToken cancellationToken)
    {
        return CollectAsync(
            new CaptureOptions(
                options.ProcessName,
                options.SourceDirectory,
                options.BenchmarkBaseline
                    ?? throw new InvalidOperationException("Benchmark 链路必须提供本轮基准。")),
            progress,
            cancellationToken);
    }

    public async Task<PerformanceMetrics> CollectAsync(
        CaptureOptions options,
        IProgress<PerformanceMetrics>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var captureClock = Stopwatch.StartNew();
        var gpuSamples = new List<GpuSnapshot>();
        var cpuSamples = new List<CpuSnapshot>();
        var nextGpuRead = TimeSpan.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var benchmark = Rdr2BenchmarkReader.FindAndParseNew(
                options.BenchmarksDirectory,
                options.BenchmarkBaseline);
            if (benchmark is not null)
            {
                return BuildResult(benchmark, gpuSamples, cpuSamples, captureClock.Elapsed);
            }

            if (!_processGuard.IsRdr2GameRunning())
            {
                throw new InvalidOperationException(
                    "RDR2 已退出，但没有检测到本轮新的 Benchmark 结果。请确认 Benchmark 已跑完后再退出游戏。");
            }

            if (captureClock.Elapsed >= nextGpuRead)
            {
                var gpu = await _gpuReader.ReadAsync(cancellationToken);
                if (gpu is not null)
                {
                    gpuSamples.Add(gpu);
                }

                var cpu = _cpuReader.Read();
                if (cpu.LogicalCoreCount > 0 && cpu.AverageUtilization > 0)
                {
                    cpuSamples.Add(cpu);
                }

                nextGpuRead = captureClock.Elapsed + TimeSpan.FromSeconds(2);
            }

            progress?.Report(new PerformanceMetrics
            {
                GpuUtilization = AverageGpu(gpuSamples, value => value.Utilization),
                GpuMemoryUsedMb = gpuSamples.Count == 0 ? 0 : gpuSamples.Max(value => value.MemoryUsedMb),
                GpuMemoryTotalMb = gpuSamples.Count == 0 ? 0 : gpuSamples.Max(value => value.MemoryTotalMb),
                CpuUtilization = cpuSamples.Count == 0 ? 0 : cpuSamples.Average(value => value.AverageUtilization),
                MaxCpuCoreUtilization = cpuSamples.Count == 0 ? 0 : cpuSamples.Max(value => value.MaxCoreUtilization),
                GpuTemperatureC = gpuSamples.Count == 0 ? 0 : gpuSamples.Max(value => value.TemperatureC),
                GpuPowerW = AverageGpu(gpuSamples, value => value.PowerW),
                Duration = captureClock.Elapsed,
                Precision = TelemetryPrecision.UtilizationOnly
            });
            await Task.Delay(750, cancellationToken);
        }
    }

    private static PerformanceMetrics BuildResult(
        Rdr2BenchmarkSummary benchmark,
        IReadOnlyCollection<GpuSnapshot> gpuSamples,
        IReadOnlyCollection<CpuSnapshot> cpuSamples,
        TimeSpan duration)
    {
        var metrics = new PerformanceMetrics
        {
            AverageFps = benchmark.AverageFps,
            LowFps = benchmark.LowFps,
            FrameTimeP99Ms = benchmark.P99FrameTimeMs,
            GpuUtilization = AverageGpu(gpuSamples, value => value.Utilization),
            GpuMemoryUsedMb = gpuSamples.Count == 0 ? 0 : gpuSamples.Max(value => value.MemoryUsedMb),
            GpuMemoryTotalMb = gpuSamples.Count == 0 ? 0 : gpuSamples.Max(value => value.MemoryTotalMb),
            CpuUtilization = cpuSamples.Count == 0 ? 0 : cpuSamples.Average(value => value.AverageUtilization),
            MaxCpuCoreUtilization = cpuSamples.Count == 0 ? 0 : cpuSamples.Max(value => value.MaxCoreUtilization),
            GpuTemperatureC = gpuSamples.Count == 0 ? 0 : gpuSamples.Max(value => value.TemperatureC),
            GpuPowerW = AverageGpu(gpuSamples, value => value.PowerW),
            Duration = duration,
            Precision = TelemetryPrecision.FrameAccurate
        };
        metrics.Bottleneck = ClassifyBottleneck(metrics);
        return metrics;
    }

    private static double AverageGpu(
        IReadOnlyCollection<GpuSnapshot> samples,
        Func<GpuSnapshot, double> selector) =>
        samples.Count == 0 ? 0 : samples.Average(selector);

    public static BottleneckKind ClassifyBottleneck(PerformanceMetrics metrics)
    {
        if (metrics.VramRatio >= 0.92)
        {
            return BottleneckKind.Vram;
        }

        if (metrics.GpuUtilization >= 90)
        {
            return BottleneckKind.Gpu;
        }

        if (metrics.GpuUtilization < 87 &&
            (metrics.MaxCpuCoreUtilization >= 88 || metrics.CpuUtilization >= 85))
        {
            return BottleneckKind.Cpu;
        }

        return metrics.GpuUtilization > 0
            ? BottleneckKind.None
            : BottleneckKind.Unknown;
    }
}
