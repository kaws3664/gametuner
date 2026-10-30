using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GameTuner.Core.Telemetry;

public sealed record FrameSample(
    TimeSpan Timestamp,
    long PresentTicks,
    int AccumulatedFrames);

public interface IFrameSampler : IDisposable
{
    Task<IReadOnlyList<FrameSample>> SampleAsync(
        TimeSpan duration,
        TimeSpan warmup,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class DxgiFrameSampler : IFrameSampler
{
    public Task<IReadOnlyList<FrameSample>> SampleAsync(
        TimeSpan duration,
        TimeSpan warmup,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => SampleCore(duration, warmup, progress, cancellationToken),
            cancellationToken);

    private static IReadOnlyList<FrameSample> SampleCore(
        TimeSpan duration,
        TimeSpan warmup,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(0, out var adapter).CheckError();
        using (adapter)
        {
            var levels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };
            D3D11.D3D11CreateDevice(
                adapter.NativePointer,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                levels,
                out var device,
                out var context).CheckError();
            using (device)
            using (context)
            {
                adapter.EnumOutputs(0, out var output).CheckError();
                using (output)
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                using (var duplication = output1.DuplicateOutput(device))
                {
                    return CaptureFrames(
                        duplication,
                        duration,
                        warmup,
                        progress,
                        cancellationToken);
                }
            }
        }
    }

    private static IReadOnlyList<FrameSample> CaptureFrames(
        IDXGIOutputDuplication duplication,
        TimeSpan duration,
        TimeSpan warmup,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var samples = new List<FrameSample>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nextProgress = TimeSpan.Zero;
        while (clock.Elapsed < warmup + duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = duplication.AcquireNextFrame(100, out var frameInfo, out var resource);
            if (result.Success)
            {
                try
                {
                    if (frameInfo.LastPresentTime != 0)
                    {
                        samples.Add(new FrameSample(
                            clock.Elapsed,
                            frameInfo.LastPresentTime,
                            checked((int)frameInfo.AccumulatedFrames)));
                    }
                }
                finally
                {
                    resource.Dispose();
                    duplication.ReleaseFrame();
                }
            }
            else if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            {
                // Static menus and loading screens legitimately produce no new frames.
            }
            else
            {
                result.CheckError();
            }

            if (clock.Elapsed >= nextProgress)
            {
                var elapsed = Math.Max(0, clock.Elapsed.TotalMilliseconds - warmup.TotalMilliseconds);
                progress?.Report(Math.Clamp(elapsed / duration.TotalMilliseconds, 0, 1));
                nextProgress = clock.Elapsed + TimeSpan.FromMilliseconds(500);
            }
        }

        return samples.Where(sample => sample.Timestamp >= warmup).ToArray();
    }

    public void Dispose()
    {
    }
}
