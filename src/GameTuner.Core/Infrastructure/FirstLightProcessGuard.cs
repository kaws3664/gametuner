using System.Diagnostics;

namespace GameTuner.Core.Infrastructure;

public interface IFirstLightProcessGuard
{
    bool IsRunning();
    Task WaitForStartAsync(IProgress<TimeSpan>? progress, CancellationToken cancellationToken);
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class FirstLightProcessGuard : IFirstLightProcessGuard
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName("007FirstLight");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public async Task WaitForStartAsync(
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!IsRunning())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(clock.Elapsed);
            await Task.Delay(500, cancellationToken);
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!IsRunning()) return true;
            await Task.Delay(1000, cancellationToken);
        }
        return !IsRunning();
    }
}
