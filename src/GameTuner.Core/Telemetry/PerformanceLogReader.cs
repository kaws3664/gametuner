using System.Globalization;
using GameTuner.Core.Models;

namespace GameTuner.Core.Telemetry;

public sealed record PerformanceLogResult(
    double AverageFps,
    double LowFps,
    double FrameTimeP99Ms,
    int SampleCount,
    string SourcePath)
{
    public TimeSpan Duration { get; init; }
    public bool IsFrameAccurate { get; init; }
}

public static class PerformanceLogReader
{
    public static PerformanceLogResult Parse(string path, string content)
    {
        var rows = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerIndex = Array.FindIndex(rows, row =>
            row.Contains("MsBetweenPresents", StringComparison.OrdinalIgnoreCase) ||
            row.Contains("FrameTime", StringComparison.OrdinalIgnoreCase) ||
            row.Contains("Frametime", StringComparison.OrdinalIgnoreCase));
        if (headerIndex >= 0)
        {
            return ParseFrameLog(path, rows, headerIndex);
        }

        return ParseSummaryLog(path, rows);
    }

    private static PerformanceLogResult ParseFrameLog(string path, string[] rows, int headerIndex)
    {
        var headers = SplitCsv(rows[headerIndex]);
        var frameIndex = FindHeader(headers, "MsBetweenPresents", "FrameTime", "Frametime", "Frame Time");
        if (frameIndex < 0)
        {
            throw new InvalidDataException("日志中没有逐帧耗时字段。");
        }

        var frames = new List<double>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var values = SplitCsv(row);
            if (values.Count <= frameIndex ||
                !TryNumber(values[frameIndex], out var milliseconds) ||
                milliseconds <= 0 || milliseconds > 10000)
            {
                continue;
            }
            frames.Add(milliseconds);
        }
        if (frames.Count < 10)
        {
            throw new InvalidDataException("日志有效帧太少，无法用于画质比较。");
        }
        var p99 = Percentile(frames, 0.99);
        var slowestCount = Math.Max(1, (int)Math.Ceiling(frames.Count * 0.01));
        var lowFrameTime = frames.OrderDescending().Take(slowestCount).Average();
        return new PerformanceLogResult(
            1000d / frames.Average(),
            1000d / lowFrameTime,
            p99,
            frames.Count,
            path)
        {
            IsFrameAccurate = true
        };
    }

    private static PerformanceLogResult ParseSummaryLog(string path, string[] rows)
    {
        var headerIndex = Array.FindIndex(rows, row =>
            SplitCsv(row).Any(cell => cell.Equals("FPS", StringComparison.OrdinalIgnoreCase)) &&
            row.Contains("Timestamp", StringComparison.OrdinalIgnoreCase));
        if (headerIndex >= 0)
        {
            return ParseNvidiaPerformanceLog(path, rows, headerIndex);
        }

        double? average = null;
        double? low = null;
        double? p99 = null;
        foreach (var row in rows)
        {
            var cells = SplitCsv(row);
            for (var index = 0; index < cells.Count; index++)
            {
                if (!TryNumber(cells[index], out var value)) continue;
                var context = string.Join(' ', cells.Take(index)).ToLowerInvariant();
                if (context.Contains("99") && (context.Contains("1%") || context.Contains("low"))) low = value;
                else if (context.Contains("99") && context.Contains("ms")) p99 = value;
                else if ((context.Contains("average") || context.Contains("avg")) && context.Contains("fps")) average = value;
            }
        }
        if (average is null)
        {
            throw new InvalidDataException("未识别的 NVIDIA 性能日志。请导出包含 FPS 或逐帧耗时的 CSV。");
        }
        p99 ??= low is > 0 ? 1000d / low.Value : 1000d / average.Value;
        low ??= 1000d / p99.Value;
        return new PerformanceLogResult(average.Value, low.Value, p99.Value, 1, path);
    }

    private static PerformanceLogResult ParseNvidiaPerformanceLog(string path, string[] rows, int headerIndex)
    {
        var headers = SplitCsv(rows[headerIndex]);
        var timeIndex = FindExactHeader(headers, "Timestamp (Elapsed time in seconds)");
        var fpsIndex = FindExactHeader(headers, "FPS");
        var lowIndex = FindExactHeader(headers, "FPS 1(%) Low");
        if (timeIndex < 0 || fpsIndex < 0 || lowIndex < 0)
            throw new InvalidDataException("NVIDIA 性能日志缺少时间、FPS 或 FPS 1% Low 字段。");

        var samples = new List<(double Seconds, double Fps, double Low)>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var values = SplitCsv(row);
            if (values.Count <= Math.Max(timeIndex, Math.Max(fpsIndex, lowIndex)) ||
                !TryNumber(values[timeIndex], out var seconds) ||
                !TryNumber(values[fpsIndex], out var fps) ||
                !TryNumber(values[lowIndex], out var lowFps) ||
                seconds < 0 || fps <= 0 || lowFps <= 0)
                continue;
            if (samples.Count > 0 && seconds < samples[^1].Seconds)
                throw new InvalidDataException("NVIDIA 性能日志的时间顺序无效。");
            samples.Add((seconds, fps, lowFps));
        }
        if (samples.Count < 2 || samples[^1].Seconds <= 0)
            throw new InvalidDataException("NVIDIA 性能日志有效样本太少，无法用于画质比较。");

        // NVIDIA exports periodic statistics, not every presented frame. Preserve that distinction.
        var weightedFps = 0d;
        var weightedLow = 0d;
        var totalWeight = 0d;
        for (var index = 1; index < samples.Count; index++)
        {
            var weight = Math.Max(0, samples[index].Seconds - samples[index - 1].Seconds);
            if (weight == 0) continue;
            weightedFps += samples[index].Fps * weight;
            weightedLow += samples[index].Low * weight;
            totalWeight += weight;
        }
        if (totalWeight <= 0)
            throw new InvalidDataException("NVIDIA 性能日志没有有效的采样时间间隔。");

        var average = weightedFps / totalWeight;
        var low = weightedLow / totalWeight;
        return new PerformanceLogResult(average, low, 1000d / low, samples.Count, path)
        {
            Duration = TimeSpan.FromSeconds(samples[^1].Seconds),
            IsFrameAccurate = false
        };
    }

    private static int FindExactHeader(IReadOnlyList<string> headers, string candidate)
    {
        for (var index = 0; index < headers.Count; index++)
            if (headers[index].Equals(candidate, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static int FindHeader(IReadOnlyList<string> headers, params string[] candidates)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (candidates.Any(candidate => headers[index].Contains(candidate, StringComparison.OrdinalIgnoreCase)))
                return index;
        }
        return -1;
    }

    public static List<string> SplitCsv(string row)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in row)
        {
            if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted) { values.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(character);
        }
        values.Add(current.ToString().Trim());
        return values;
    }

    private static bool TryNumber(string value, out double result) =>
        double.TryParse(value.Trim().Trim('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
        double.TryParse(value.Trim().Trim('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out result);

    private static double Percentile(List<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        return ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)];
    }
}
