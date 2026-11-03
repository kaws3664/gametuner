using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows.Threading;
using GameTuner.App.Processes;
using GameTuner.App.Settings;
using GameTuner.App.ViewModels;
using GameTuner.App.Web;

namespace GameTuner.App.Desktop;

public sealed class DesktopSessionController(
    MainViewModel viewModel,
    ILocalConfigurationProvider configurationProvider,
    ICaptureService captureService,
    GameProcessService? processService = null) : IDisposable, INotifyPropertyChanged
{
    private readonly GameProcessService _processService = processService ?? new GameProcessService();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dispatcher? _dispatcher = SynchronizationContext.Current is DispatcherSynchronizationContext
        ? Dispatcher.CurrentDispatcher : null;
    private GameProcessInfo? _selectedProcess;
    private string _lastAutomaticResolution = "";
    private bool _playerSelectedGraphicsSettings;
    private bool _disposed;
    private bool _isBusy;
    private string _processStatus = "请选择正在运行的游戏进程。";
    private string _captureStatus = "尚未开始采集。也可直接导入已有日志。";
    private string _operationStatus = "";

    public MainViewModel ViewModel { get; } = viewModel;
    public int SelectedProcessId => _selectedProcess?.Id ?? 0;
    public string ConfigurationStatus { get; private set; } = "";
    public string ConfigurationSource { get; private set; } = "";
    public bool GraphicsReadOnly { get; private set; }
    public bool IsRecording => captureService.Snapshot().IsRecording;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Changed(nameof(IsBusy)); } }
    public string ProcessStatus { get => _processStatus; private set { _processStatus = value; Changed(); } }
    public string CaptureStatus { get => _captureStatus; private set { _captureStatus = value; Changed(); } }
    public string OperationStatus { get => _operationStatus; set { _operationStatus = value; Changed(); } }
    public LiveTelemetryState Telemetry => captureService.Snapshot();
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SelectProcess(GameProcessInfo process)
    {
        _selectedProcess = process ?? throw new ArgumentNullException(nameof(process));
        if (ViewModel.Rounds.Count == 0) ViewModel.Game = process.ProcessName;
        ProcessStatus = $"已选择 {process.ProcessName}.exe · PID {process.Id}";
        OperationStatus = "进程选择仅用于读取与 PresentMon 采集，不会启动或关闭游戏。";
    }

    public async Task InitializeAsync()
    {
        await RunActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            DetectRunningGame();
            await RefreshConfigurationAsync(includeDesktopResolution: true);
        });
    }

    /// <summary>
    /// Fills the game name and process selection from the running processes so the
    /// player does not have to pick a PID by hand. Only a first-round session may be
    /// rewritten; an existing comparison keeps the game the player already recorded.
    /// A detected process still stays read-only until the player starts a capture.
    /// </summary>
    private void DetectRunningGame()
    {
        if (ViewModel.Rounds.Count > 0) return;
        var detected = _processService.DetectRunningGame();
        if (detected is null)
        {
            ProcessStatus = _processService.DescribeDetection(_processService.BuildCandidates(_processService.GetProcesses()));
            return;
        }
        SelectProcess(detected);
        ProcessStatus = $"已检测到游戏：{detected.ProcessName}.exe"
            + (string.IsNullOrWhiteSpace(detected.WindowTitle) ? "" : $"「{detected.WindowTitle}」")
            + $" · PID {detected.Id}；已自动填入，可点击“选择游戏进程…”改选。";
    }

    public void StartCapture() => StartCaptureCore();

    public Task StartCaptureAsync() => RunActionAsync(() =>
    {
        StartCaptureCore();
        return Task.CompletedTask;
    });

    private void StartCaptureCore()
    {
        var process = RequireRunningProcess();
        captureService.Start(process.Id, ViewModel.Game);
        ViewModel.LogPath = captureService.Snapshot().LogPath;
        RefreshCaptureStatus();
    }

    public Task StopAndAnalyzeAsync() => RunActionAsync(async () =>
    {
        OperationStatus = "正在停止采集并分析本轮日志…";
        var capturedPath = await captureService.StopForAnalysisAsync();
        RefreshCaptureStatus();
        if (capturedPath is null)
        {
            OperationStatus = IsRecording
                ? "采集仍在进行，请稍候再停止。"
                : "没有可分析的新采集：" + captureService.Snapshot().Status;
            return;
        }
        await RefreshConfigurationAsync();
        ViewModel.LogPath = capturedPath;
        await ViewModel.AnalyzeAsync();
        OperationStatus = ViewModel.Status;
    });

    public Task ImportExternalLogAsync(string path) => RunActionAsync(async () =>
    {
        OperationStatus = "正在导入外部日志…";
        await captureService.StopForAnalysisAsync();
        RefreshCaptureStatus();
        await RefreshConfigurationAsync();
        ViewModel.LogPath = path;
        await ViewModel.AnalyzeAsync();
        OperationStatus = ViewModel.Status;
    });

    public Task ImportGraphicsSettingsAsync(string path) => RunActionAsync(async () =>
    {
        var settings = await new StructuredSettingsReader().ReadAsync(path);
        ViewModel.GraphicsSettings = settings;
        _playerSelectedGraphicsSettings = true;
        GraphicsReadOnly = false;
        ConfigurationSource = Path.GetFileName(path);
        ConfigurationStatus = "已从玩家选择的配置文件只读导入；不会修改源文件。";
        Changed(nameof(GraphicsReadOnly)); Changed(nameof(ConfigurationSource)); Changed(nameof(ConfigurationStatus));
        OperationStatus = ConfigurationStatus;
    });

    public async Task RunActionAsync(Func<Task> action)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => RunActionAsync(action)).Task.Unwrap();
            return;
        }
        await _gate.WaitAsync();
        try
        {
            IsBusy = true;
            try
            {
                await action();
            }
            catch (Exception exception) { OperationStatus = "操作未完成：" + exception.Message; }
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            RefreshCaptureStatus();
        }
    }

    public void RefreshCaptureStatus()
    {
        var live = captureService.Snapshot();
        CaptureStatus = live.IsRecording ? live.Status.StartsWith("等待", StringComparison.Ordinal)
            ? "正在采集所选进程的性能数据。" : live.Status
            : _captureStatus.StartsWith("正在", StringComparison.Ordinal) ? "采集已停止。" : live.Status;
        Changed(nameof(IsRecording)); Changed(nameof(Telemetry));
    }

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private GameProcessInfo RequireRunningProcess()
    {
        var selected = _selectedProcess
            ?? throw new InvalidOperationException("请先选择要监测的游戏进程。");
        try
        {
            using var process = Process.GetProcessById(selected.Id);
            if (process.HasExited) throw new InvalidOperationException();
            return selected;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("所选游戏进程已退出或无法访问，请重新选择。", exception);
        }
    }

    private async Task RefreshConfigurationAsync(bool includeDesktopResolution = false)
    {
        var detected = await configurationProvider.ReadAsync(
            _selectedProcess, ViewModel.Game, includeDesktopResolution, ViewModel.LogDirectory);
        if (detected.Cpu.Length > 0) ViewModel.Cpu = detected.Cpu;
        if (detected.Gpu.Length > 0) ViewModel.Gpu = detected.Gpu;
        if (detected.Memory.Length > 0) ViewModel.Memory = detected.Memory;
        if (detected.Resolution.Length > 0
            && (detected.HasGameSettings || ViewModel.Resolution.Length == 0
                || ViewModel.Resolution.Equals(_lastAutomaticResolution, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Resolution = detected.Resolution;
            _lastAutomaticResolution = detected.Resolution;
        }
        if (!_playerSelectedGraphicsSettings)
        {
            if (detected.HasGameSettings) ViewModel.GraphicsSettings = detected.GraphicsSettings;
            GraphicsReadOnly = detected.HasGameSettings;
            ConfigurationStatus = detected.Status;
            ConfigurationSource = detected.Source;
        }
        Changed(nameof(GraphicsReadOnly)); Changed(nameof(ConfigurationSource)); Changed(nameof(ConfigurationStatus));
    }

    public void Dispose()
    {
        if (_disposed) return;
        captureService.Dispose();
        _disposed = true;
    }
}
