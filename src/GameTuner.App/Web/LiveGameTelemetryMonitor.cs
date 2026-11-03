using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GameTuner.App.Desktop;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Telemetry;

namespace GameTuner.App.Web;

public sealed record LiveTelemetryState
{
    public bool IsRecording { get; init; }
    public string LogPath { get; init; } = "";
    public string Status { get; init; } = "等待游戏启动后自动监测。";
    public double Fps { get; init; }
    public double Cpu { get; init; }
    public double Gpu { get; init; }
    public double DurationSeconds { get; init; }
}

/// <summary>Owns only the PresentMon child processes started by GameTuner.</summary>
public sealed class LiveGameTelemetryMonitor(AppPaths paths) : ICaptureService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly NvidiaSmiReader _gpuReader = new();
    private readonly CpuPerformanceReader _cpuReader = new();
    private readonly List<GpuSnapshot> _gpuSamples = [];
    private readonly List<CpuSnapshot> _cpuSamples = [];
    private Process? _game;
    private Process? _presentMon;
    private CancellationTokenSource? _stopping;
    private Task? _captureTask;
    private Stopwatch? _clock;
    private string _sessionName = "";
    private string _logPath = "";
    private bool _isLogPathClaimed;
    private string _status = "等待游戏启动后自动监测。";
    private bool _isRecording;
    private double _fps;
    private double _cpu;
    private double _gpu;
    private int _frameColumn = -1;

    public LiveTelemetryState Snapshot()
    {
        lock (_sync)
            return new LiveTelemetryState
            {
                IsRecording = _isRecording, LogPath = _logPath, Status = _status,
                Fps = _fps, Cpu = _cpu, Gpu = _gpu, DurationSeconds = _clock?.Elapsed.TotalSeconds ?? 0
            };
    }

    public void Start(int processId, string game)
    {
        if (processId <= 0)
        {
            lock (_sync) _status = "没有可采集的游戏进程，请先选择正在运行的游戏。";
            return;
        }
        if (!File.Exists(paths.PresentMonExecutable))
        {
            lock (_sync) _status = "未找到 PresentMon 采集组件；请重新解压完整发布包后再试。";
            return;
        }
        _lifecycle.Wait();
        try
        {
            lock (_sync)
            {
                if (_isRecording && _game?.Id == processId) return;
            }
            StopForAnalysisCoreAsync().GetAwaiter().GetResult();
            lock (_sync)
            {
                try
                {
                    var gameProcess = Process.GetProcessById(processId);
                    _game = gameProcess;
                    var safeGame = string.Concat(game.Where(char.IsLetterOrDigit));
                    _logPath = Path.Combine(paths.Captures,
                        $"GameTuner-{safeGame}-{DateTime.Now:yyyyMMdd-HHmmss}-{processId}-{Guid.NewGuid():N}.csv");
                    _isLogPathClaimed = false;
                    _sessionName = $"GameTuner-{processId}-{Guid.NewGuid():N}";
                    var start = new ProcessStartInfo(paths.PresentMonExecutable)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    foreach (var argument in new[]
                    {
                        "--process_id", processId.ToString(CultureInfo.InvariantCulture),
                        "--output_file", _logPath,
                        "--session_name", _sessionName,
                        "--terminate_on_proc_exit", "--exclude_dropped", "--no_console_stats"
                    }) start.ArgumentList.Add(argument);
                    var presentMonProcess = Process.Start(start) ?? throw new InvalidOperationException("PresentMon 未能启动。");
                    _presentMon = presentMonProcess;
                    _gpuSamples.Clear(); _cpuSamples.Clear();
                    _clock = Stopwatch.StartNew();
                    var stopping = new CancellationTokenSource();
                    _stopping = stopping;
                    _isRecording = true; _fps = 0; _cpu = 0; _gpu = 0; _frameColumn = -1;
                    _status = "正在后台采集游戏帧数据、CPU 和 GPU…";
                    _captureTask = Task.Run(() => CaptureAsync(gameProcess, presentMonProcess, stopping.Token));
                }
                catch (Exception exception)
                {
                    _status = "实时监测启动失败：" + exception.Message
                        + "；仍可使用 NVIDIA 日志或手动选择已有日志。";
                    _isRecording = false;
                    _presentMon?.Dispose(); _game?.Dispose();
                    _presentMon = null; _game = null;
                }
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<string?> StopForAnalysisAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { return await StopForAnalysisCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task<string?> StopForAnalysisCoreAsync()
    {
        Task? task;
        string path;
        bool claimPath;
        lock (_sync)
        {
            task = _captureTask;
            claimPath = _logPath.Length > 0 && !_isLogPathClaimed;
            path = claimPath ? _logPath : "";
            if (claimPath) _isLogPathClaimed = true;
            if (_isRecording) _status = "正在整理本轮监测日志…";
            if (_stopping is not null && !_stopping.IsCancellationRequested) _stopping.Cancel();
        }
        if (task is not null) await task.ConfigureAwait(false);
        lock (_sync)
        {
            if (claimPath && string.Equals(_logPath, path, StringComparison.OrdinalIgnoreCase)) _logPath = "";
            return claimPath && File.Exists(path) ? path : null;
        }
    }

    private async Task CaptureAsync(Process game, Process presentMon, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !game.HasExited && !presentMon.HasExited)
            {
                var gpu = await _gpuReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var cpu = _cpuReader.Read();
                lock (_sync)
                {
                    if (gpu is not null)
                    {
                        _gpuSamples.Add(gpu);
                        _gpu = gpu.Utilization;
                    }
                    if (cpu.AverageUtilization > 0)
                    {
                        _cpuSamples.Add(cpu);
                        _cpu = cpu.AverageUtilization;
                    }
                    _fps = ReadRecentFps();
                    var seconds = _clock?.Elapsed.TotalSeconds ?? 0;
                    _status = $"正在监测 · {seconds:0} 秒 · {Math.Max(0, _gpuSamples.Count + _cpuSamples.Count)} 组硬件样本";
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (_sync) _status = "本轮监测已结束，正在整理可用日志。";
        }
        finally
        {
            await StopPresenterAsync(presentMon).ConfigureAwait(false);
            var presenterError = await presentMon.StandardError.ReadToEndAsync().ConfigureAwait(false);
            FinalizeLog();
            lock (_sync)
            {
                _isRecording = false;
                _status = File.Exists(_logPath)
                    ? "本轮监测日志已准备好，点击分析即可生成建议。"
                    : DescribeCaptureFailure(presenterError);
                _captureTask = null;
                _stopping?.Dispose(); _stopping = null;
                presentMon.Dispose(); game.Dispose();
                _presentMon = null; _game = null;
            }
        }
    }

    private async Task StopPresenterAsync(Process presentMon)
    {
        try
        {
            if (presentMon.HasExited) return;
            var start = new ProcessStartInfo(paths.PresentMonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--session_name"); start.ArgumentList.Add(_sessionName);
            start.ArgumentList.Add("--terminate_existing_session");
            using var stop = Process.Start(start);
            if (stop is not null) await stop.WaitForExitAsync().ConfigureAwait(false);
            await presentMon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch
        {
            try { if (!presentMon.HasExited) presentMon.Kill(); } catch { }
        }
    }

    private double ReadRecentFps()
    {
        if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return 0;
        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 40) return 0;
        var skipPartial = stream.Length > 131072;
        if (skipPartial) stream.Seek(-131072, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (skipPartial) reader.ReadLine();
        var frames = new List<double>();
        while (reader.ReadLine() is { } line)
        {
            var cells = PerformanceLogReader.SplitCsv(line);
            if (_frameColumn < 0)
            {
                _frameColumn = cells.FindIndex(cell => cell.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (_frameColumn >= cells.Count) continue;
            if (double.TryParse(cells[_frameColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var frame)
                && frame is > 0 and < 10000) frames.Add(frame);
        }
        var recent = frames.TakeLast(3000).ToArray();
        var duration = recent.Sum();
        return duration > 0 ? recent.Length * 1000 / duration : 0;
    }

    /// <summary>PresentMon refuses to start an ETW session without administrative
    /// rights; report that plainly instead of blaming a missing game frame.</summary>
    private static string DescribeCaptureFailure(string presenterError)
    {
        var error = presenterError.Trim();
        var denied = error.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("administrative privileges", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase);
        if (denied)
            return "采集未能启动：PresentMon 需要管理员权限才能建立性能追踪会话。"
                + "请以管理员身份重新启动本程序后再点“开始新采集”；也可以导入已有日志继续分析。";
        return "本轮没有采集到可显示的游戏帧；请进入实际游玩画面后再分析。"
            + (error.Length == 0 ? "" : $" {error}");
    }

    private void FinalizeLog()
    {
        if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return;
        var cpu = _cpuSamples.Count == 0 ? 0 : _cpuSamples.Average(item => item.AverageUtilization);
        var gpu = _gpuSamples.Count == 0 ? 0 : _gpuSamples.Average(item => item.Utilization);
        var used = _gpuSamples.Count == 0 ? 0 : _gpuSamples.Max(item => item.MemoryUsedMb);
        var total = _gpuSamples.Count == 0 ? 0 : _gpuSamples.Max(item => item.MemoryTotalMb);
        var temp = _logPath + ".tmp";
        using (var input = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(input))
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            var header = reader.ReadLine();
            if (string.IsNullOrEmpty(header)) return;
            writer.WriteLine(header + ",CPU Utilization(%),GPU Utilization(%),GPU Memory Used(MB),GPU Memory Total(MB)");
            var suffix = string.Join(',', new[] { cpu, gpu, used, total }.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));
            while (reader.ReadLine() is { } line)
                if (line.Length > 0) writer.WriteLine(line + suffix);
        }
        if (File.Exists(temp) && new FileInfo(temp).Length > 0) File.Move(temp, _logPath, overwrite: true);
        else if (File.Exists(temp)) File.Delete(temp);
    }

    public void Dispose() => StopForAnalysisAsync().GetAwaiter().GetResult();
}
