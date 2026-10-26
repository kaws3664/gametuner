using System.Globalization;
using System.Text.RegularExpressions;
using GameTuner.Core.Models;

namespace GameTuner.Core.Telemetry;

public sealed record Rdr2BenchmarkSummary(
    double AverageFps,
    double LowFps,
    double P99FrameTimeMs,
    int FrameCount,
    string SourcePath,
    IReadOnlyDictionary<string, string> Settings,
    string SystemSummary);

public sealed class BenchmarkBaseline
{
    private readonly HashSet<string> _fingerprints;

    private BenchmarkBaseline(HashSet<string> fingerprints)
    {
        _fingerprints = fingerprints;
    }

    public static BenchmarkBaseline Capture(string benchmarksDirectory)
    {
        if (!Directory.Exists(benchmarksDirectory))
        {
            return new BenchmarkBaseline(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var fingerprints = new DirectoryInfo(benchmarksDirectory)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Select(GetFingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new BenchmarkBaseline(fingerprints);
    }

    public bool IsNew(FileInfo file) => !_fingerprints.Contains(GetFingerprint(file));

    private static string GetFingerprint(FileInfo file) =>
        $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
}

public static partial class Rdr2BenchmarkReader
{
    private static readonly TimeSpan DefaultSettleTime = TimeSpan.FromSeconds(1);

    public static Rdr2BenchmarkSummary? FindAndParse(
        string benchmarksDirectory,
        DateTimeOffset createdAfter)
    {
        if (!Directory.Exists(benchmarksDirectory))
        {
            return null;
        }

        var threshold = createdAfter.UtcDateTime.AddSeconds(-5);
        var files = new DirectoryInfo(benchmarksDirectory)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => file.LastWriteTimeUtc >= threshold)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        var summaryFile = files.FirstOrDefault(file =>
            Path.GetFileName(file.FullName).StartsWith("Benchmark-", StringComparison.OrdinalIgnoreCase));
        if (summaryFile is null)
        {
            return null;
        }

        var summaryText = File.ReadAllText(summaryFile.FullName);
        if (!summaryText.Contains("Frames Per Second", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var frameFiles = files
            .Where(file => Path.GetFileName(file.FullName)
                .StartsWith("FrameTimes-Pass", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var frameTimes = ReadFrameTimes(frameFiles);
        if (frameTimes.Count > 0)
        {
            var totalMs = frameTimes.Sum();
            var average = frameTimes.Count / (totalMs / 1000d);
            var ordered = frameTimes.OrderBy(value => value).ToArray();
            var percentileIndex = Math.Clamp(
                (int)Math.Ceiling(ordered.Length * 0.99) - 1,
                0,
                ordered.Length - 1);
            var p99 = ordered[percentileIndex];
            return new Rdr2BenchmarkSummary(
                average,
                p99 > 0 ? 1000d / p99 : 0,
                p99,
                frameTimes.Count,
                summaryFile?.FullName ?? frameFiles[0].FullName,
                ParseSettings(summaryText),
                ParseSystem(summaryText));
        }

        var passes = PassPattern().Matches(summaryText)
            .Select(match => new
            {
                Average = ParseDouble(match.Groups[4].Value),
                Min = ParseDouble(match.Groups[2].Value),
                Max = ParseDouble(match.Groups[3].Value)
            })
            .Where(value => value.Average > 0)
            .ToArray();
        if (passes.Length == 0)
        {
            return null;
        }

        var passAverage = passes.Average(value => value.Average);
        var percentileValues = PercentilePattern().Matches(summaryText)
            .Where(match => match.Groups[1].Value.Trim() is "99" or "99.0")
            .Select(match => ParseDouble(match.Groups[2].Value))
            .Where(value => value > 0)
            .ToArray();
        var p99FrameTime = percentileValues.Length > 0
            ? percentileValues.Max()
            : 1000d / Math.Max(1, passes.Min(value => value.Min));

        return new Rdr2BenchmarkSummary(
            passAverage,
            p99FrameTime > 0 ? 1000d / p99FrameTime : 0,
            p99FrameTime,
            0,
            summaryFile.FullName,
            ParseSettings(summaryText),
            ParseSystem(summaryText));
    }

    public static Rdr2BenchmarkSummary? FindAndParseNew(
        string benchmarksDirectory,
        BenchmarkBaseline baseline,
        TimeSpan? settleTime = null)
    {
        if (!Directory.Exists(benchmarksDirectory))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var settle = settleTime ?? DefaultSettleTime;
        var newFiles = new DirectoryInfo(benchmarksDirectory)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(baseline.IsNew)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        if (newFiles.Length == 0)
        {
            return null;
        }

        var summaryFile = newFiles.FirstOrDefault(file =>
            Path.GetFileName(file.FullName)
                .StartsWith("Benchmark-", StringComparison.OrdinalIgnoreCase));
        if (summaryFile is null || now - summaryFile.LastWriteTimeUtc < settle)
        {
            return null;
        }

        var newFrameFiles = newFiles
            .Where(file => Path.GetFileName(file.FullName)
                .StartsWith("FrameTimes-Pass", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (newFrameFiles.Any(file => now - file.LastWriteTimeUtc < settle))
        {
            return null;
        }

        var summaryText = File.ReadAllText(summaryFile.FullName);
        if (!summaryText.Contains("Frames Per Second", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ParseSummary(summaryFile, newFrameFiles, summaryText);
    }

    private static Rdr2BenchmarkSummary? ParseSummary(
        FileInfo summaryFile,
        FileInfo[] frameFiles,
        string summaryText)
    {
        var frameTimes = ReadFrameTimes(frameFiles);
        if (frameTimes.Count > 0)
        {
            var totalMs = frameTimes.Sum();
            var average = frameTimes.Count / (totalMs / 1000d);
            var ordered = frameTimes.OrderBy(value => value).ToArray();
            var percentileIndex = Math.Clamp(
                (int)Math.Ceiling(ordered.Length * 0.99) - 1,
                0,
                ordered.Length - 1);
            var p99 = ordered[percentileIndex];
            return new Rdr2BenchmarkSummary(
                average,
                p99 > 0 ? 1000d / p99 : 0,
                p99,
                frameTimes.Count,
                summaryFile.FullName,
                ParseSettings(summaryText),
                ParseSystem(summaryText));
        }

        var passes = PassPattern().Matches(summaryText)
            .Select(match => new
            {
                Average = ParseDouble(match.Groups[4].Value),
                Min = ParseDouble(match.Groups[2].Value)
            })
            .Where(value => value.Average > 0)
            .ToArray();
        if (passes.Length == 0)
        {
            return null;
        }

        var passAverage = passes.Average(value => value.Average);
        var percentileValues = PercentilePattern().Matches(summaryText)
            .Where(match => match.Groups[1].Value.Trim() is "99" or "99.0")
            .Select(match => ParseDouble(match.Groups[2].Value))
            .Where(value => value > 0)
            .ToArray();
        var p99FrameTime = percentileValues.Length > 0
            ? percentileValues.Max()
            : 1000d / Math.Max(1, passes.Min(value => value.Min));

        return new Rdr2BenchmarkSummary(
            passAverage,
            p99FrameTime > 0 ? 1000d / p99FrameTime : 0,
            p99FrameTime,
            0,
            summaryFile.FullName,
            ParseSettings(summaryText),
            ParseSystem(summaryText));
    }

    private static List<double> ReadFrameTimes(IEnumerable<FileInfo> files)
    {
        var result = new List<double>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file.FullName))
            {
                var parts = line.Split('\t', StringSplitOptions.TrimEntries);
                if (parts.Length < 2 ||
                    !double.TryParse(
                        parts[1].Replace(',', '.'),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var frameTime))
                {
                    continue;
                }

                if (frameTime > 0 && frameTime < 5000)
                {
                    result.Add(frameTime);
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseSettings(string text)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = text.IndexOf("=== SETTINGS ===", StringComparison.OrdinalIgnoreCase);
        if (section < 0)
        {
            return settings;
        }

        foreach (var line in text[section..].Split('\n').Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            settings[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return settings;
    }

    private static string ParseSystem(string text)
    {
        var start = text.IndexOf("=== SYSTEM ===", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        var settings = text.IndexOf("=== SETTINGS ===", start, StringComparison.OrdinalIgnoreCase);
        return settings > start
            ? text[start..settings].Trim()
            : text[start..].Trim();
    }

    private static double ParseDouble(string value) =>
        double.TryParse(
            value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;

    [GeneratedRegex(
        @"^\s*Pass\s+(\d+)\s*,\s*([\d.,]+)\s*,\s*([\d.,]+)\s*,\s*([\d.,]+)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PassPattern();

    [GeneratedRegex(
        @"^\s*(\d+(?:\.\d+)?)\s*%\s*,\s*([\d.,]+)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex PercentilePattern();
}
