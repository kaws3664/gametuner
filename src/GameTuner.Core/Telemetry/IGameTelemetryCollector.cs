using GameTuner.Core.Models;

namespace GameTuner.Core.Telemetry;

/// <summary>通用遥测采集参数：进程名 + 数据目录 + 本轮基准，适配两种数据来源。</summary>
public sealed record GameTelemetryOptions(
    string ProcessName,
    string SourceDirectory,
    BenchmarkBaseline? BenchmarkBaseline = null,
    TimeSpan? MinimumDuration = null,
    DateTimeOffset? LogBaseline = null);

/// <summary>
/// 统一遥测采集接口：游戏无关。实现方各自封装 CSV 解析 / Benchmark 读取。
/// </summary>
public interface IGameTelemetryCollector
{
    Task<PerformanceMetrics> CollectAsync(
        GameTelemetryOptions options,
        IProgress<PerformanceMetrics>? progress = null,
        CancellationToken cancellationToken = default);
}
