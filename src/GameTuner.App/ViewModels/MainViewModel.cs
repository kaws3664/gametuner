using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;
using GameTuner.Core.Models;
using GameTuner.Core.Optimization;
using GameTuner.Core.Profiles;

namespace GameTuner.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly Func<GameProfile, ITuningSessionController> _controllerFactory;
    private readonly SettingsStore _settingsStore;
    private readonly ICredentialStore _credentialStore;
    private readonly AppSettings _settings;
    private readonly IGameProcessGuard _processGuard;
    private readonly INvidiaAppService _nvidia;
    private readonly SessionStore _sessionStore;
    private ITuningSessionController _controller;
    private CancellationTokenSource? _runCancellation;
    private TuningSession? _session;
    private ModeChoice _selectedMode;
    private GameProfile _selectedGame;
    private string _goalText = "目标 60 帧，平衡优先";
    private double _targetFps = 60;
    private string _statusTitle = "READY";
    private string _statusMessage;
    private string _averageFps = "--", _lowFps = "--", _gpuUtil = "--", _cpuUtil = "--", _vram = "--";
    private string _bottleneck = "等待采样";
    private string _roundLabel = "第 0 / 3 轮";
    private double _roundProgress;
    private bool _isBusy, _isPrecisionWarning, _isDecisionPending;
    private string _aiEndpoint = "", _aiModel = "", _aiApiKey = "", _saveAiStatus = "";
    private string _aiWireApi = "chat", _aiUserAgent = "";
    private string _nvidiaStatus = "正在检查 NVIDIA App";
    private string _telemetryLogDirectory;
    private int _captureMinutes = 5;

    public MainViewModel(
        Func<GameProfile, ITuningSessionController> controllerFactory,
        SettingsStore settingsStore,
        ICredentialStore credentialStore,
        AppSettings settings,
        IGameProcessGuard processGuard,
        INvidiaAppService nvidia,
        SessionStore sessionStore)
    {
        _controllerFactory = controllerFactory;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _settings = settings;
        _processGuard = processGuard;
        _nvidia = nvidia;
        _sessionStore = sessionStore;
        _selectedMode = ModeChoices[1];
        _selectedGame = GameProfiles.FirstLight;
        _statusMessage = _selectedGame.MethodStepDescription;
        _aiEndpoint = settings.AiEndpoint;
        _aiModel = settings.AiModel;
        _aiWireApi = settings.AiWireApi;
        _aiUserAgent = settings.AiUserAgent;
        _telemetryLogDirectory = settings.TelemetryLogDirectory;
        _captureMinutes = CaptureMinuteChoices.Contains(settings.CaptureDurationSeconds / 60)
            ? settings.CaptureDurationSeconds / 60
            : 5;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && Session is { OriginalBackupPath.Length: > 0 });
        ApplyBestCommand = new AsyncRelayCommand(() => DecideAsync(true), () => IsDecisionPending && !IsBusy);
        SkipCommand = new AsyncRelayCommand(() => DecideAsync(false), () => IsDecisionPending && !IsBusy);
        SaveAiCommand = new AsyncRelayCommand(SaveAiAsync);
        _controller = _controllerFactory(_selectedGame);
        _controller.SessionChanged += OnSessionChanged;
        _controller.ProgressChanged += OnProgressChanged;
        _controller.DecisionRequired += OnDecisionRequired;
        _controller.NotificationRequested += OnControllerNotification;
    }

    /// <summary>通知 App 弹托盘气泡（由具体控制器转发）。</summary>
    public event EventHandler<string>? NotificationRequested;

    public IReadOnlyList<GameProfile> AvailableGames => GameProfiles.All;
    public IReadOnlyList<ModeChoice> ModeChoices { get; } =
    [
        new(TuningMode.Quality, "画质优先", "保住纹理与观感，优先削减低感知开销"),
        new(TuningMode.Balanced, "平衡优先", "根据瓶颈在帧率与画质之间取舍"),
        new(TuningMode.FrameRate, "帧数优先", "优先命中平均帧与 1% Low 目标")
    ];
    public IReadOnlyList<int> CaptureMinuteChoices { get; } = [3, 5, 10, 15];
    public ObservableCollection<RoundCardViewModel> Rounds { get; } = [];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand ApplyBestCommand { get; }
    public AsyncRelayCommand SkipCommand { get; }
    public AsyncRelayCommand SaveAiCommand { get; }

    public ModeChoice SelectedMode { get => _selectedMode; set { if (SetProperty(ref _selectedMode, value)) { GoalText = $"目标 {TargetFps:0} 帧，{value.Label}"; RaisePropertyChanged(nameof(SelectedModeLabel)); } } }
    public string GoalText { get => _goalText; set => SetProperty(ref _goalText, value); }
    public double TargetFps { get => _targetFps; set { if (SetProperty(ref _targetFps, Math.Clamp(value, 15, 480))) GoalText = $"目标 {TargetFps:0} 帧，{SelectedMode.Label}"; } }
    public string StatusTitle { get => _statusTitle; private set => SetProperty(ref _statusTitle, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string AverageFps { get => _averageFps; private set => SetProperty(ref _averageFps, value); }
    public string LowFps { get => _lowFps; private set => SetProperty(ref _lowFps, value); }
    public string GpuUtil { get => _gpuUtil; private set => SetProperty(ref _gpuUtil, value); }
    public string CpuUtil { get => _cpuUtil; private set => SetProperty(ref _cpuUtil, value); }
    public string Vram { get => _vram; private set => SetProperty(ref _vram, value); }
    public string Bottleneck { get => _bottleneck; private set => SetProperty(ref _bottleneck, value); }
    public string RoundLabel { get => _roundLabel; private set => SetProperty(ref _roundLabel, value); }
    public double RoundProgress { get => _roundProgress; private set => SetProperty(ref _roundProgress, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { RaisePropertyChanged(nameof(IsSetupEnabled)); RaisePropertyChanged(nameof(IsProfileSwitchAllowed)); RefreshCommands(); } } }
    public bool IsSetupEnabled => !IsBusy;
    public bool IsPrecisionWarning { get => _isPrecisionWarning; private set => SetProperty(ref _isPrecisionWarning, value); }
    public bool IsDecisionPending { get => _isDecisionPending; private set { if (SetProperty(ref _isDecisionPending, value)) RefreshCommands(); } }
    public TuningSession? Session { get => _session; private set { if (SetProperty(ref _session, value)) { RefreshCommands(); RaisePropertyChanged(nameof(FinalCandidateLabel)); RaisePropertyChanged(nameof(FinalCandidateReason)); RaisePropertyChanged(nameof(RawConfigHash)); } } }
    public string FinalCandidateLabel => Session?.FinalCandidate?.Label ?? Session?.PendingCandidate?.Label ?? "等待实测";
    public string FinalCandidateReason => Session?.Message ?? "第一轮只建立当前配置基线，不直接套官方预设。";
    public string RawConfigHash => Session is null || Session.OriginalConfigHash.Length < 12 ? "原始配置指纹：等待开始" : $"原始配置指纹：{Session.OriginalConfigHash[..12]}…";
    public string SelectedModeLabel => SelectedMode.Label;
    public string NvidiaStatus { get => _nvidiaStatus; private set => SetProperty(ref _nvidiaStatus, value); }
    public string AiEndpoint { get => _aiEndpoint; set => SetProperty(ref _aiEndpoint, value); }
    public string AiModel { get => _aiModel; set => SetProperty(ref _aiModel, value); }
    public string AiWireApi { get => _aiWireApi; set => SetProperty(ref _aiWireApi, value); }
    public string AiUserAgent { get => _aiUserAgent; set => SetProperty(ref _aiUserAgent, value); }
    public string AiApiKey { get => _aiApiKey; set => SetProperty(ref _aiApiKey, value); }
    public string SaveAiStatus { get => _saveAiStatus; private set => SetProperty(ref _saveAiStatus, value); }
    public string TelemetryLogDirectory { get => _telemetryLogDirectory; set => SetProperty(ref _telemetryLogDirectory, value); }
    public int CaptureMinutes
    {
        get => _captureMinutes;
        set
        {
            if (SetProperty(ref _captureMinutes, value))
                RaisePropertyChanged(nameof(StepDescriptionText));
        }
    }

    // ===== 游戏选择与 profile 动态文本 =====

    public GameProfile SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (value is null || SetProperty(ref _selectedGame, value) is false)
            {
                return;
            }

            RaiseProfileTexts();
            RebuildController();
        }
    }

    public bool IsProfileSwitchAllowed => !IsBusy;
    public string HeroSubtitle => $"{SelectedGame.DisplayName.ToUpperInvariant()} / PERSONALIZED GRAPHICS";
    public string ConfigStatus => SelectedGame.ConfigDisplayName;
    public string ConfigPath => SelectedGame.ConfigPathDisplay;
    public string ConfigScopeStatus => SelectedGame.ConfigScopeDescription;
    public string DataSourceStatus => SelectedGame.DataSourceDescription;
    public string DataDirectory => SelectedGame.TelemetrySource == TelemetrySource.GameBenchmark
        ? SelectedGame.BenchmarkDirectory
        : string.IsNullOrWhiteSpace(TelemetryLogDirectory) ? @"AppData\Local\nvlog" : TelemetryLogDirectory;
    public string DataDirectoryStatus => SelectedGame.TelemetrySource == TelemetrySource.GameBenchmark
        ? "自动监控本轮生成的 Benchmark 结果文件，读取后自动退出游戏进入下一轮"
        : "实时读取本轮新生成的性能 CSV，到达所选时长自动分析";
    public string DataSourceDetail => SelectedGame.TelemetrySource == TelemetrySource.GameBenchmark
        ? "数据来源：游戏内置 Benchmark 结果文件。每轮自动应用画质档位并启动游戏；进入游戏后运行一次内置 Benchmark，工具自动监控结果文件生成并读取解析，随后自动退出游戏进入下一轮，三轮完成自动应用最终档案；GPU、CPU、显存同时由 nvidia-smi 与系统计数器采样。"
        : "数据来源：NVIDIA 浮层记录的性能 CSV。本轮开始后由你开始记录，工具实时读取，到达所选时长自动分析，无需停止记录；只读取新文件。GPU、CPU、显存同时由 nvidia-smi 与系统计数器采样。";
    public string StepDescriptionText => SelectedGame.MethodStepDescription;
    public bool IsCaptureDurationVisible => SelectedGame.TelemetrySource == TelemetrySource.NvidiaLogCsv;

    private void RaiseProfileTexts()
    {
        RaisePropertyChanged(nameof(HeroSubtitle));
        RaisePropertyChanged(nameof(ConfigStatus));
        RaisePropertyChanged(nameof(ConfigPath));
        RaisePropertyChanged(nameof(ConfigScopeStatus));
        RaisePropertyChanged(nameof(DataSourceStatus));
        RaisePropertyChanged(nameof(DataDirectory));
        RaisePropertyChanged(nameof(DataDirectoryStatus));
        RaisePropertyChanged(nameof(DataSourceDetail));
        RaisePropertyChanged(nameof(StepDescriptionText));
        RaisePropertyChanged(nameof(IsCaptureDurationVisible));
        RaisePropertyChanged(nameof(IsProfileSwitchAllowed));
    }

    public async Task InitializeAsync()
    {
        try
        {
            var apps = await _nvidia.GetApplicationsAsync(SelectedGame.Id);
            NvidiaStatus = apps.Any(app => app.Name == SelectedGame.Id && app.OptimizationSupported)
                ? $"{SelectedGame.DisplayName} 已识别 · 优先 NVIDIA 能力" : $"{SelectedGame.DisplayName} 未被 NVIDIA 识别";
        }
        catch (Exception exception) { NvidiaStatus = "NVIDIA MCP 不可用"; AppLog.Write("NVIDIA probe failed: " + exception); }
        var latest = await _sessionStore.LoadLatestAsync();
        if (latest is not null) { Session = latest; StatusMessage = latest.Message; RebuildRounds(latest); }
    }

    private async Task StartAsync()
    {
        if (_processGuard.IsAnyRunning(SelectedGame.ProcessNames))
        {
            StatusTitle = "BLOCKED";
            StatusMessage = $"请手动退出 {SelectedGame.DisplayName} 再开始，软件不会替你退出游戏。";
            return;
        }
        var goal = GoalParser.Parse(GoalText, TargetFps);
        goal.Mode = SelectedMode.Value;
        try { goal.Validate(); } catch (Exception exception) { StatusTitle = "INVALID GOAL"; StatusMessage = exception.Message; return; }
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        IsBusy = true; IsDecisionPending = false; Rounds.Clear(); RoundProgress = 0;
        StatusTitle = "ARMING";
        StatusMessage = $"备份当前配置；首轮不套官方预设。{SelectedGame.MethodStepDescription}";
        try
        {
            _settings.CaptureDurationSeconds = CaptureMinutes * 60;
            await _settingsStore.SaveAsync(_settings);
            Session = await _controller.RunAsync(goal, _runCancellation.Token);
            StatusTitle = "AWAITING YOUR DECISION";
        }
        catch (OperationCanceledException) { StatusTitle = "CANCELLED"; StatusMessage = "会话已取消，未替你退出游戏。"; }
        catch (Exception exception) { StatusTitle = "FAILED"; StatusMessage = exception.Message; }
        finally { IsBusy = false; }
    }

    private async Task DecideAsync(bool apply)
    {
        IsBusy = true; IsDecisionPending = false;
        try
        {
            Session = await _controller.DecideAsync(apply, _runCancellation?.Token ?? CancellationToken.None);
            StatusTitle = Session.State == SessionState.Completed ? "COMPLETE" : "NEXT ROUND";
            StatusMessage = Session.Message;
        }
        catch (Exception exception) { StatusTitle = "FAILED"; StatusMessage = exception.Message; IsDecisionPending = true; }
        finally { IsBusy = false; }
    }

    private Task CancelAsync() { _runCancellation?.Cancel(); StatusMessage = "正在取消；不会关闭游戏，也不会自动应用建议。"; return Task.CompletedTask; }
    private async Task RestoreAsync()
    {
        if (Session is null) return;
        IsBusy = true;
        try { await _controller.RestoreOriginalAsync(Session); StatusTitle = "RESTORED"; StatusMessage = Session.Message; }
        catch (Exception exception) { StatusTitle = "RESTORE FAILED"; StatusMessage = exception.Message; }
        finally { IsBusy = false; }
    }
    private async Task SaveAiAsync()
    {
        try
        {
            var wireApi = AiWireApi.Trim();
            if (wireApi is not ("chat" or "responses"))
            {
                throw new InvalidDataException("接口协议只能填写 chat 或 responses。");
            }

            _settings.AiEndpoint = AiEndpoint.Trim(); _settings.AiModel = AiModel.Trim();
            _settings.AiWireApi = wireApi; _settings.AiUserAgent = AiUserAgent.Trim();
            _settings.TelemetryLogDirectory = TelemetryLogDirectory.Trim();
            if (!string.IsNullOrWhiteSpace(AiApiKey)) { _credentialStore.Write(_settings.AiApiKeyCredentialName, AiApiKey.Trim()); AiApiKey = ""; }
            await _settingsStore.SaveAsync(_settings);
            SaveAiStatus = "已保存；日志路径同时生效";
            RaisePropertyChanged(nameof(DataDirectory));
        }
        catch (Exception exception) { SaveAiStatus = "保存失败：" + exception.Message; }
    }

    /// <summary>切换游戏时按新 profile 重建控制器并清空会话状态。</summary>
    private void RebuildController()
    {
        _controller.SessionChanged -= OnSessionChanged;
        _controller.ProgressChanged -= OnProgressChanged;
        _controller.DecisionRequired -= OnDecisionRequired;
        _controller.NotificationRequested -= OnControllerNotification;
        _controller = _controllerFactory(_selectedGame);
        _controller.SessionChanged += OnSessionChanged;
        _controller.ProgressChanged += OnProgressChanged;
        _controller.DecisionRequired += OnDecisionRequired;
        _controller.NotificationRequested += OnControllerNotification;
        _runCancellation?.Dispose();
        _runCancellation = null;
        Session = null;
        Rounds.Clear();
        RoundLabel = "第 0 / 3 轮";
        RoundProgress = 0;
        AverageFps = LowFps = GpuUtil = CpuUtil = Vram = "--";
        Bottleneck = "等待采样";
        IsDecisionPending = false;
        IsPrecisionWarning = false;
        StatusTitle = "READY";
        StatusMessage = SelectedGame.MethodStepDescription;
    }

    private void OnControllerNotification(object? sender, string message) =>
        NotificationRequested?.Invoke(this, message);

    private void OnDecisionRequired(object? sender, TuningDecision decision) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => { IsDecisionPending = true; StatusMessage = decision.Reason; });
    private void OnSessionChanged(object? sender, TuningSession session) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => { Session = session; RebuildRounds(session); });
    private void OnProgressChanged(object? sender, TuningProgress progress) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            StatusTitle = progress.State.ToString().ToUpperInvariant(); StatusMessage = progress.Message;
            RoundLabel = $"第 {Math.Max(1, progress.RoundNumber)} / 3 轮";
            RoundProgress = Math.Clamp(progress.RoundNumber / 3d, 0, 1);
            if (progress.LiveMetrics is not null) UpdateMetrics(progress.LiveMetrics);
        });
    private void UpdateMetrics(PerformanceMetrics metrics)
    {
        AverageFps = metrics.AverageFps > 0 ? $"{metrics.AverageFps:0.0}" : "--";
        LowFps = metrics.LowFps > 0 ? $"{metrics.LowFps:0.0}" : "等待日志";
        GpuUtil = metrics.GpuUtilization > 0 ? $"{metrics.GpuUtilization:0}%" : "--";
        CpuUtil = metrics.CpuUtilization > 0 ? $"{metrics.CpuUtilization:0}%" : "--";
        Vram = metrics.GpuMemoryTotalMb > 0 ? $"{metrics.GpuMemoryUsedMb / 1024:0.0} / {metrics.GpuMemoryTotalMb / 1024:0.0} GB" : "--";
        Bottleneck = RuleBasedAiAdvisor.DescribeBottleneck(metrics.Bottleneck);
        IsPrecisionWarning = false;
    }
    private void RebuildRounds(TuningSession session)
    {
        Rounds.Clear();
        foreach (var round in session.Rounds.OrderBy(round => round.RoundNumber)) Rounds.Add(new RoundCardViewModel(round));
        if (session.Rounds.Count > 0) { UpdateMetrics(session.Rounds[^1].Metrics); RoundLabel = $"第 {session.Rounds.Count} / 3 轮"; RoundProgress = session.Rounds.Count / 3d; }
        RefreshCommands();
    }
    private void RefreshCommands() { StartCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); RestoreCommand.RaiseCanExecuteChanged(); ApplyBestCommand.RaiseCanExecuteChanged(); SkipCommand.RaiseCanExecuteChanged(); }
}
