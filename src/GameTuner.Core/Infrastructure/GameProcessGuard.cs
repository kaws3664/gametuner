using System.Diagnostics;

namespace GameTuner.Core.Infrastructure;

/// <summary>
/// 通用游戏进程守卫：以进程名集合为参数，不绑定任何具体游戏。
/// 旧链路中的 IFirstLightProcessGuard / IProcessGuard 保留作兼容，
/// 新控制器统一走本接口。
/// </summary>
public interface IGameProcessGuard
{
    bool IsAnyRunning(IEnumerable<string> processNames);

    Task WaitForStartAsync(
        string processName,
        IProgress<TimeSpan>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> WaitForExitAsync(
        IEnumerable<string> processNames,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 自动关闭游戏进程：先请求主窗口关闭，等待宽限期后强制结束未退出的进程。
    /// 返回是否已全部退出。供 Benchmark 自动循环链路使用，由 profile/配置决定是否启用。
    /// </summary>
    Task<bool> StopAsync(
        IEnumerable<string> processNames,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default);
}

public sealed class GameProcessGuard : IGameProcessGuard
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    public bool IsAnyRunning(IEnumerable<string> processNames)
    {
        var names = processNames
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return false;
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (names.Contains(process.ProcessName))
                {
                    return true;
                }
            }
            catch
            {
                // 进程在枚举期间退出，忽略。
            }
        }

        return false;
    }

    public async Task WaitForStartAsync(
        string processName,
        IProgress<TimeSpan>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!IsAnyRunning([processName]))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(stopwatch.Elapsed);
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public async Task<bool> WaitForExitAsync(
        IEnumerable<string> processNames,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var names = processNames.ToArray();
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAnyRunning(names))
                {
                    return true;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAnyRunning(names))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return !IsAnyRunning(names);
    }

    public async Task<bool> StopAsync(
        IEnumerable<string> processNames,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        var names = processNames.ToArray();
        if (!IsAnyRunning(names))
        {
            return true;
        }

        RequestClose(names);

        var deadline = DateTimeOffset.UtcNow + gracePeriod;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAnyRunning(names))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        if (IsAnyRunning(names))
        {
            Kill(names);
        }

        return !IsAnyRunning(names);
    }

    private static void RequestClose(IEnumerable<string> names)
    {
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (set.Contains(process.ProcessName) && process.MainWindowHandle != 0)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
                // 进程在操作期间退出，忽略。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void Kill(IEnumerable<string> names)
    {
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (set.Contains(process.ProcessName))
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
                // 进程已退出或无权限，忽略。
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
