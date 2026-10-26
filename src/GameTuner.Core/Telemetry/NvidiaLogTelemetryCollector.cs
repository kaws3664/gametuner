using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.Core.Telemetry;

public sealed record LogCaptureOptions(
    string LogDirectory,
    DateTimeOffset Baseline,
    TimeSpan? MinimumDuration = null);

public interface INvidiaLogTelemetryCollector
{
    Task<PerformanceMetrics> CollectAsync(
        LogCaptureOptions options,
        IProgress<PerformanceMetrics>? progress,
        CancellationToken cancellationToken);
}

public sealed class NvidiaLogTelemetryCollector(
    IFirstLightProcessGuard processGuard,
    NvidiaSmiReader? gpuReader = null,
    CpuPerformanceReader? cpuReader = null) : INvidiaLogTelemetryCollector, IGameTelemetryCollector
{
    private readonly NvidiaSmiReader _gpuReader = gpuReader ?? new NvidiaSmiReader();
    private readonly CpuPerformanceReader _cpuReader = cpuReader ?? new CpuPerformanceReader();

    Task<PerformanceMetrics> IGameTelemetryCollector.CollectAsync(
        GameTelemetryOptions options,
        IProgress<PerformanceMetrics>? progress,
        CancellationToken cancellationToken)
    {
        return CollectAsync(
            new LogCaptureOptions(
                options.SourceDirectory,
                options.LogBaseline ?? DateTimeOffset.Now,
                options.MinimumDuration),
            progress,
            cancellationToken);
    }

    public async Task<PerformanceMetrics> CollectAsync(
        LogCaptureOptions options,
        IProgress<PerformanceMetrics>? progress,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gpu = new List<GpuSnapshot>();
        var cpu = new List<CpuSnapshot>();
        var nextSample = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= nextSample)
            {
                var gpuSample = await _gpuReader.ReadAsync(cancellationToken);
                if (gpuSample is not null) gpu.Add(gpuSample);
                var cpuSample = _cpuReader.Read();
                if (cpuSample.LogicalCoreCount > 0 && cpuSample.AverageUtilization > 0) cpu.Add(cpuSample);
                nextSample = clock.Elapsed + TimeSpan.FromSeconds(2);
            }

            var live = Live(gpu, cpu, clock.Elapsed);
            progress?.Report(live);
            var parsed = FindNewLog(options);
            if (parsed is not null)
            {
                var minimumDuration = options.MinimumDuration ?? TimeSpan.FromMinutes(5);
                if (parsed.IsFrameAccurate && parsed.Duration == TimeSpan.Zero)
                    parsed = parsed with { Duration = clock.Elapsed };
                var sourceStopped = DateTime.UtcNow - File.GetLastWriteTimeUtc(parsed.SourcePath) >= TimeSpan.FromSeconds(3);
                if (parsed.Duration < minimumDuration && sourceStopped)
                    throw new InvalidOperationException(
                        $"本次日志只有 {parsed.Duration.TotalSeconds:0} 秒，不足 {minimumDuration.TotalSeconds:0} 秒。请重新采集有代表性的实际游玩片段。");
                if (parsed.Duration >= minimumDuration)
                {
                    live.AverageFps = parsed.AverageFps;
                    live.LowFps = parsed.LowFps;
                    live.FrameTimeP99Ms = parsed.FrameTimeP99Ms;
                    live.Duration = parsed.Duration;
                    live.Precision = parsed.IsFrameAccurate
                        ? TelemetryPrecision.FrameAccurate
                        : TelemetryPrecision.NvidiaAggregate;
                    live.Bottleneck = Rdr2TelemetryCollector.ClassifyBottleneck(live);
                    return live;
                }
            }
            if (!processGuard.IsRunning())
            {
                throw new InvalidOperationException(
                    "007 已退出，但没有发现本轮新日志。请在 NVIDIA 浮层中开始记录统计数据，游玩约 5 分钟后停止记录并导出 CSV。");
            }
            await Task.Delay(750, cancellationToken);
        }
    }

    private static PerformanceMetrics Live(
        IReadOnlyCollection<GpuSnapshot> gpu,
        IReadOnlyCollection<CpuSnapshot> cpu,
        TimeSpan elapsed) => new()
    {
        GpuUtilization = gpu.Count == 0 ? 0 : gpu.Average(item => item.Utilization),
        GpuMemoryUsedMb = gpu.Count == 0 ? 0 : gpu.Max(item => item.MemoryUsedMb),
        GpuMemoryTotalMb = gpu.Count == 0 ? 0 : gpu.Max(item => item.MemoryTotalMb),
        GpuTemperatureC = gpu.Count == 0 ? 0 : gpu.Max(item => item.TemperatureC),
        GpuPowerW = gpu.Count == 0 ? 0 : gpu.Average(item => item.PowerW),
        CpuUtilization = cpu.Count == 0 ? 0 : cpu.Average(item => item.AverageUtilization),
        MaxCpuCoreUtilization = cpu.Count == 0 ? 0 : cpu.Max(item => item.MaxCoreUtilization),
        Duration = elapsed,
        Precision = TelemetryPrecision.UtilizationOnly
    };

    internal static PerformanceLogResult? FindNewLog(LogCaptureOptions options)
    {
        var directory = string.IsNullOrWhiteSpace(options.LogDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvlog")
            : options.LogDirectory;
        if (!Directory.Exists(directory)) return null;
        var now = DateTime.UtcNow;
        var candidates = new DirectoryInfo(directory).GetFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                           file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.Contains("Latency_Log", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.LastWriteTimeUtc >= options.Baseline.UtcDateTime.AddSeconds(-2))
            .Where(file => file.Length > 32)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName);
        foreach (var path in candidates)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return PerformanceLogReader.Parse(path, reader.ReadToEnd());
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
