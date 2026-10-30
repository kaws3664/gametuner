using System.Diagnostics;

namespace GameTuner.Core.Infrastructure;

public interface IProcessGuard
{
    bool IsRdr2Running();
    bool IsRdr2GameRunning();
    Task WaitForRdr2StartAsync(
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken);
    Task<bool> WaitForRdr2ExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class Rdr2ProcessGuard : IProcessGuard
{
    private static readonly string[] LauncherAndGameProcesses = ["RDR2", "PlayRDR2"];

    public bool IsRdr2Running() =>
        LauncherAndGameProcesses.Any(IsProcessRunning);

    public bool IsRdr2GameRunning() => IsProcessRunning("RDR2");

    public async Task WaitForRdr2StartAsync(
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!IsRdr2GameRunning())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(elapsed.Elapsed);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        progress?.Report(elapsed.Elapsed);
    }

    public async Task<bool> WaitForRdr2ExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRdr2Running())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return !IsRdr2Running();
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
