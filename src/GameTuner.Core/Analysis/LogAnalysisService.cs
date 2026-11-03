using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using GameTuner.Core.Models;
using GameTuner.Core.Telemetry;

namespace GameTuner.Core.Analysis;

// This service has no game process, registry, configuration adapter or MCP dependency.
public sealed partial class LogAnalysisService
{
    private const long MaximumLogBytes = 64 * 1024 * 1024;

    public async Task<LogAnalysisReport> AnalyzeAsync(
        string path, TuningGoal goal, ManualQualityTier currentTier,
        CancellationToken cancellationToken = default, string graphicsSettings = "")
    {
        goal.Validate();
        var source = Path.GetFullPath(path.Trim());
        if (!File.Exists(source)) throw new FileNotFoundException("请先选择一个存在的性能日志文件。", source);
        if (new FileInfo(source).Length > MaximumLogBytes)
            throw new InvalidDataException("日志超过 64 MB，请截取有代表性的游玩片段后再导入。");

        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        if (content.Length == 0) throw new InvalidDataException("日志为空，不能生成优化建议。");
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => AnalyzeContent(source, content, goal, currentTier, graphicsSettings), cancellationToken);
    }

    public string? FindLatestLog(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("日志目录不存在，请检查路径或手动选择日志。");
        return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => file.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.Contains("Latency_Log", StringComparison.OrdinalIgnoreCase) && file.Length > 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName).FirstOrDefault();
    }

    private static LogAnalysisReport AnalyzeContent(string source, string content, TuningGoal goal, ManualQualityTier currentTier, string graphicsSettings)
    {
        var warnings = new List<string>();
        PerformanceMetrics metrics;
        int samples;
        string precision;
        if (content.Contains("Frames Per Second", StringComparison.OrdinalIgnoreCase)
            && BenchmarkPass().IsMatch(content))
        {
            var averages = BenchmarkPass().Matches(content)
                .Select(match => Number(match.Groups[1].Value)).Where(value => value > 0).ToArray();
            var p99 = BenchmarkP99().Matches(content)
                .Select(match => Number(match.Groups[1].Value)).Where(value => value > 0).DefaultIfEmpty(0).Max();
            if (averages.Length == 0) throw new InvalidDataException("Benchmark 没有有效的平均帧率。");
            metrics = new()
            {
                AverageFps = averages.Average(), LowFps = p99 > 0 ? 1000 / p99 : 0,
                FrameTimeP99Ms = p99, Precision = TelemetryPrecision.Estimated
            };
            samples = averages.Length;
            precision = "Benchmark 摘要 / P99 等效低帧（不是精确 1% Low）";
            warnings.Add("只读取所选 Benchmark 摘要，不合并其他轮次的逐帧文件；未记录利用率时无法判断瓶颈。");
        }
        else
        {
            var rows = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var headerIndex = Array.FindIndex(rows, row =>
                PerformanceLogReader.SplitCsv(row).Any(cell => cell.Equals("FPS", StringComparison.OrdinalIgnoreCase))
                && row.Contains("Timestamp", StringComparison.OrdinalIgnoreCase));
            if (headerIndex >= 0)
                (metrics, samples) = ParseNvidia(rows, headerIndex);
            else
            {
                // Summary-only formats are deliberately rejected rather than inventing missing low-FPS values.
                if (!rows.Any(row => row.Contains("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)
                    || row.Contains("FrameTime", StringComparison.OrdinalIgnoreCase)
                    || row.Contains("Frame Time", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("未识别的日志。支持 NVIDIA 性能 CSV、逐帧耗时 CSV 和 RDR2 Benchmark 摘要。");
                (metrics, samples) = ParseFrames(rows);
            }
            precision = metrics.Precision == TelemetryPrecision.FrameAccurate
                ? "逐帧日志 / 最慢 1% 帧的平均 FPS" : "NVIDIA 分段统计 / 时间加权平均";
        }
        if (!double.IsFinite(metrics.AverageFps) || metrics.AverageFps <= 0
            || !double.IsFinite(metrics.LowFps) || metrics.LowFps < 0)
            throw new InvalidDataException("日志计算结果无效，请检查数值或分别导出每次录制。");
        metrics.Bottleneck = Classify(metrics);
        if (metrics.LowFps <= 0) warnings.Add("日志没有低帧数据：只评估平均 FPS，不宣称低帧也已达标。");
        if (metrics.Duration == TimeSpan.Zero) warnings.Add("摘要没有采集时长，无法确认片段是否足够有代表性。");
        else if (metrics.Duration < TimeSpan.FromMinutes(3))
            warnings.Add($"片段只有 {metrics.Duration.TotalSeconds:0} 秒，少于 3 分钟，建议仅作为初步试调参考。");
        if (metrics.GpuMemoryTotalMb <= 0) warnings.Add("日志没有完整的显存容量信息，不能断言显存瓶颈。");
        warnings.Add("请确认日志与当前配置属于同一游玩片段；菜单、加载和不同场景会影响比较。");
        return new(source, metrics, samples, precision, warnings,
            ManualRecommendationPlanner.Recommend(metrics, goal, currentTier, graphicsSettings))
        {
            SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
        };
    }

    private static (PerformanceMetrics Metrics, int Samples) ParseNvidia(string[] rows, int headerIndex)
    {
        var headers = PerformanceLogReader.SplitCsv(rows[headerIndex]);
        int Column(params string[] names) => headers.FindIndex(header => names.Contains(header, StringComparer.OrdinalIgnoreCase));
        var timeIndex = Column("Timestamp (Elapsed time in seconds)");
        var fpsIndex = Column("FPS");
        var lowIndex = Column("FPS 1(%) Low", "FPS 1% Low");
        var cpuIndex = Column("CPU Utilization(%)", "CPU Utilization (%)");
        var gpuIndex = Column("GPU1 Utilization(%)", "GPU Utilization(%)", "GPU Utilization (%)");
        var usedIndex = Column("GPU1 Memory Used(MB)", "GPU Memory Used(MB)", "GPU Memory Used (MB)");
        var totalIndex = Column("GPU1 Memory Total(MB)", "GPU Memory Total(MB)", "GPU Memory Total (MB)");
        var pidIndex = Column("PID");
        if (timeIndex < 0 || fpsIndex < 0) throw new InvalidDataException("日志缺少时间戳或 FPS 字段。");
        var samples = new List<(double Time, double Fps, double Low, double Cpu, double Gpu, double Used, double Total)>();
        var processIds = new HashSet<string>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var cells = PerformanceLogReader.SplitCsv(row);
            double Read(int column) => column < 0 || column >= cells.Count ? double.NaN : Number(cells[column]);
            var time = Read(timeIndex);
            var fps = Read(fpsIndex);
            if (!double.IsFinite(time) || !double.IsFinite(fps) || time < 0 || fps <= 0) continue;
            if (samples.Count > 0 && time < samples[^1].Time)
                throw new InvalidDataException("日志时间倒退，可能混合了多次录制，请分别导入。");
            if (pidIndex >= 0 && pidIndex < cells.Count && !string.IsNullOrWhiteSpace(cells[pidIndex]))
                processIds.Add(cells[pidIndex]);
            samples.Add((time, fps, Read(lowIndex), Read(cpuIndex), Read(gpuIndex), Read(usedIndex), Read(totalIndex)));
        }
        if (processIds.Count > 1) throw new InvalidDataException("日志包含多个 PID，请只保留同一游戏进程的记录再分析。");
        if (samples.Count < 2) throw new InvalidDataException("有效样本不足，至少需要两个不同时间点。");
        var duration = samples[^1].Time - samples[0].Time;
        if (duration <= 0) throw new InvalidDataException("日志没有有效的采样时长。");
        double Weighted(Func<(double Time, double Fps, double Low, double Cpu, double Gpu, double Used, double Total), double> get,
            double maximum = double.MaxValue)
        {
            double sum = 0, weight = 0;
            for (var index = 1; index < samples.Count; index++)
            {
                var value = get(samples[index]);
                var delta = samples[index].Time - samples[index - 1].Time;
                if (!double.IsFinite(value) || value < 0 || value > maximum || delta <= 0) continue;
                sum += value * delta;
                weight += delta;
            }
            return weight > 0 ? sum / weight : 0;
        }
        // Low-FPS coverage must include every weighted interval, otherwise the result is reported as unknown.
        var lowComplete = samples.Skip(1).Select((sample, index) => (sample, previous: samples[index]))
            .Where(pair => pair.sample.Time > pair.previous.Time)
            .All(pair => double.IsFinite(pair.sample.Low) && pair.sample.Low > 0);
        var low = lowComplete ? Weighted(sample => sample.Low) : 0;
        bool Complete(Func<(double Time, double Fps, double Low, double Cpu, double Gpu, double Used, double Total), double> get) =>
            samples.Skip(1).Select((sample, index) => (sample, previous: samples[index]))
                .Where(pair => pair.sample.Time > pair.previous.Time)
                .All(pair => double.IsFinite(get(pair.sample)) && get(pair.sample) is >= 0 and <= 100);
        var memorySamples = samples.Where(sample => double.IsFinite(sample.Used) && double.IsFinite(sample.Total)
            && sample.Total > 0 && sample.Used >= 0 && sample.Used <= sample.Total).ToArray();
        return (new PerformanceMetrics
        {
            AverageFps = Weighted(sample => sample.Fps), LowFps = low,
            FrameTimeP99Ms = 0, CpuUtilization = Complete(sample => sample.Cpu) ? Weighted(sample => sample.Cpu, 100) : 0,
            GpuUtilization = Complete(sample => sample.Gpu) ? Weighted(sample => sample.Gpu, 100) : 0,
            GpuMemoryUsedMb = memorySamples.Select(sample => sample.Used).DefaultIfEmpty(0).Max(),
            GpuMemoryTotalMb = memorySamples.Select(sample => sample.Total).DefaultIfEmpty(0).Max(),
            Duration = TimeSpan.FromSeconds(duration), Precision = TelemetryPrecision.NvidiaAggregate
        }, samples.Count);
    }

    private static (PerformanceMetrics Metrics, int Samples) ParseFrames(string[] rows)
    {
        var names = new[] { "MsBetweenPresents", "FrameTime", "Frametime", "Frame Time", "FrameTime (ms)", "Frame Time (ms)" };
        var headerIndex = Array.FindIndex(rows, row => PerformanceLogReader.SplitCsv(row)
            .Any(cell => names.Contains(cell, StringComparer.OrdinalIgnoreCase)));
        if (headerIndex < 0) throw new InvalidDataException("逐帧日志缺少明确的毫秒帧耗时字段。");
        var headers = PerformanceLogReader.SplitCsv(rows[headerIndex]);
        var frameIndex = headers.FindIndex(cell => names.Contains(cell, StringComparer.OrdinalIgnoreCase));
        var pidIndex = headers.FindIndex(cell => cell.Equals("PID", StringComparison.OrdinalIgnoreCase)
            || cell.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
        int Column(params string[] names) => headers.FindIndex(header => names.Contains(header, StringComparer.OrdinalIgnoreCase));
        var cpuIndex = Column("CPU Utilization(%)", "CPU Utilization (%)");
        var gpuIndex = Column("GPU Utilization(%)", "GPU Utilization (%)", "GPU1 Utilization(%)");
        var usedIndex = Column("GPU Memory Used(MB)", "GPU Memory Used (MB)", "GPU1 Memory Used(MB)");
        var totalIndex = Column("GPU Memory Total(MB)", "GPU Memory Total (MB)", "GPU1 Memory Total(MB)");
        var pids = new HashSet<string>();
        var frames = new List<double>();
        var cpu = new List<double>();
        var gpu = new List<double>();
        var memoryUsed = new List<double>();
        var memoryTotal = new List<double>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var values = PerformanceLogReader.SplitCsv(row);
            if (values.Count <= frameIndex) continue;
            var frame = Number(values[frameIndex]);
            if (!double.IsFinite(frame) || frame <= 0) continue;
            if (pidIndex >= 0 && values.Count > pidIndex && !string.IsNullOrWhiteSpace(values[pidIndex]))
                pids.Add(values[pidIndex]);
            void Collect(int column, List<double> target, double maximum)
            {
                if (column < 0 || column >= values.Count) return;
                var value = Number(values[column]);
                if (double.IsFinite(value) && value >= 0 && value <= maximum) target.Add(value);
            }
            Collect(cpuIndex, cpu, 100);
            Collect(gpuIndex, gpu, 100);
            Collect(usedIndex, memoryUsed, 1024 * 1024);
            Collect(totalIndex, memoryTotal, 1024 * 1024);
            frames.Add(frame);
        }
        if (pids.Count > 1) throw new InvalidDataException("逐帧日志包含多个 PID，请分别导出单个游戏进程。");
        if (frames.Count < 10) throw new InvalidDataException("逐帧日志有效帧太少，至少需要 10 帧。");
        var ordered = frames.Order().ToArray();
        var slowCount = Math.Max(1, (int)Math.Ceiling(frames.Count * 0.01));
        var p99 = ordered[Math.Clamp((int)Math.Ceiling(frames.Count * 0.99) - 1, 0, ordered.Length - 1)];
        return (new PerformanceMetrics
        {
            AverageFps = 1000 / frames.Average(),
            LowFps = 1000 / ordered.TakeLast(slowCount).Average(),
            FrameTimeP99Ms = p99,
            CpuUtilization = cpu.Count == 0 ? 0 : cpu.Average(),
            GpuUtilization = gpu.Count == 0 ? 0 : gpu.Average(),
            GpuMemoryUsedMb = memoryUsed.Count == 0 ? 0 : memoryUsed.Max(),
            GpuMemoryTotalMb = memoryTotal.Count == 0 ? 0 : memoryTotal.Max(),
            Duration = TimeSpan.FromMilliseconds(frames.Sum()),
            Precision = TelemetryPrecision.FrameAccurate
        }, frames.Count);
    }

    private static BottleneckKind Classify(PerformanceMetrics metrics)
    {
        if (metrics.VramRatio >= 0.92) return BottleneckKind.Vram;
        if (metrics.GpuUtilization >= 90) return BottleneckKind.Gpu;
        if (metrics.GpuUtilization > 0 && metrics.GpuUtilization < 87 && metrics.CpuUtilization >= 85)
            return BottleneckKind.Cpu;
        return metrics.GpuUtilization > 0 ? BottleneckKind.None : BottleneckKind.Unknown;
    }

    private static double Number(string text) => double.TryParse(text.Trim().Trim('%'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : double.NaN;

    [GeneratedRegex(@"^\s*Pass\s+\d+\s*,\s*[\d.]+\s*,\s*[\d.]+\s*,\s*([\d.]+)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BenchmarkPass();
    [GeneratedRegex(@"^\s*99(?:\.0)?\s*%\s*,\s*([\d.]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex BenchmarkP99();
}
