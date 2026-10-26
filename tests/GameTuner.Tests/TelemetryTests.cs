using GameTuner.Core.Models;
using GameTuner.Core.Telemetry;

namespace GameTuner.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameTunerTelemetryTests",
        Guid.NewGuid().ToString("N"));

    public TelemetryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void BenchmarkReader_prefers_frame_times_for_average_and_p99()
    {
        var summary = Path.Combine(_root, "Benchmark-26-10-23-14-00-00.txt");
        var frameTimes = Path.Combine(_root, "FrameTimes-Pass1-26-10-23-14-00-00.txt");
        File.WriteAllText(
            summary,
            """
            Frames Per Second (Higher is better) Min, Max, Avg
            Pass 0, 48.00, 72.00, 60.00

            Percentiles in ms for pass 0
            99%, 22.00

            === SYSTEM ===
            CPU: Intel Core i3-12100F
            	Logical Cores: 8
            GPU: NVIDIA GeForce RTX 2070 SUPER
            	VRAM: 8192 MB
            === SETTINGS ===
            shadowQuality: kSettingLevel_High
            dlssQuality: 2
            """);
        File.WriteAllText(
            frameTimes,
            "Frame\tTime(ms)\n" +
            string.Join('\n', Enumerable.Repeat("1\t16.666", 96)
                .Concat(Enumerable.Repeat("2\t33.333", 4))));
        var now = DateTimeOffset.Now;
        File.SetLastWriteTimeUtc(summary, now.UtcDateTime);
        File.SetLastWriteTimeUtc(frameTimes, now.UtcDateTime);

        var result = Rdr2BenchmarkReader.FindAndParse(_root, now.AddSeconds(-1));

        Assert.NotNull(result);
        Assert.Equal(100, result.FrameCount);
        Assert.Equal(1000d / 33.333, result.LowFps, 1);
        Assert.Equal("kSettingLevel_High", result.Settings["shadowQuality"]);
        Assert.Contains("RTX 2070 SUPER", result.SystemSummary);
    }

    [Fact]
    public void BenchmarkReader_falls_back_to_summary_percentile()
    {
        var summary = Path.Combine(_root, "Benchmark-26-10-23-14-00-01.txt");
        File.WriteAllText(
            summary,
            """
            Frames Per Second (Higher is better) Min, Max, Avg
            Pass 0, 50.00, 70.00, 60.00
            Pass 1, 52.00, 72.00, 62.00
            Percentiles in ms for pass 0
            99%, 20.00
            === SETTINGS ===
            shadowQuality: kSettingLevel_High
            """);
        var now = DateTimeOffset.Now;
        File.SetLastWriteTimeUtc(summary, now.UtcDateTime);

        var result = Rdr2BenchmarkReader.FindAndParse(_root, now.AddSeconds(-1));

        Assert.NotNull(result);
        Assert.Equal(61, result.AverageFps);
        Assert.Equal(50, result.LowFps);
    }

    [Fact]
    public void FrameAnalyzer_computes_estimated_average_and_low_metrics()
    {
        var samples = new List<FrameSample>();
        var timestamp = TimeSpan.Zero;
        for (var index = 0; index < 240; index++)
        {
            samples.Add(new FrameSample(timestamp, index, 1));
            timestamp += index is 180 or 181 or 182 or 183
                ? TimeSpan.FromMilliseconds(33.333)
                : TimeSpan.FromMilliseconds(16.666);
        }

        var result = FrameRateAnalyzer.Analyze(samples, TimeSpan.FromSeconds(4));

        Assert.InRange(result.AverageFps, 58, 61);
        Assert.InRange(result.LowFps, 29, 31);
        Assert.Equal(TelemetryPrecision.Estimated, result.Precision);
    }

    [Fact]
    public void ApplyBenchmark_marks_result_frame_accurate()
    {
        var live = new PerformanceMetrics
        {
            GpuUtilization = 97,
            CpuUtilization = 62,
            MaxCpuCoreUtilization = 88,
            GpuMemoryUsedMb = 7000,
            GpuMemoryTotalMb = 8192,
            Duration = TimeSpan.FromMinutes(2),
            Precision = TelemetryPrecision.Estimated
        };
        var benchmark = new Rdr2BenchmarkSummary(
            60,
            54,
            18.5,
            5000,
            "test",
            new Dictionary<string, string>(),
            "");

        var result = FrameRateAnalyzer.ApplyBenchmark(live, benchmark);

        Assert.Equal(60, result.AverageFps);
        Assert.Equal(54, result.LowFps);
        Assert.Equal(97, result.GpuUtilization);
        Assert.Equal(TelemetryPrecision.FrameAccurate, result.Precision);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

}
