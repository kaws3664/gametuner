using System.Collections.ObjectModel;
using System.IO;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly LogAnalysisService _analysis;
    private readonly JsonFileStore _files;
    private readonly string _savePath;
    public string PlansDirectory { get; }
    private AnalysisSession _session = new() { Game = "我的游戏" };
    private string _game = "我的游戏", _logPath = "", _logDirectory;
    private double _targetFps = 60;
    private TuningMode _mode = TuningMode.Balanced;
    private ManualQualityTier _tier = ManualQualityTier.Unknown;
    private string _graphicsSettings = "", _resolution = "", _cpu = "", _gpu = "", _memory = "", _scene = "";
    private string _status = "先设定目标，填写本次画质，再导入第一份日志。";
    private string _saveStatus = "";
    private bool _isBusy, _isShareVisible;
    private AnalysisRoundViewModel? _selectedRound;

    public MainViewModel(LogAnalysisService analysis, JsonFileStore files, AppPaths paths)
    {
        _analysis = analysis;
        _files = files;
        _savePath = System.IO.Path.Combine(paths.Root, "manual-analysis.json");
        PlansDirectory = Path.Combine(paths.Root, "manual-plans");
        _logDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvlog");
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanImport && !string.IsNullOrWhiteSpace(LogPath));
        LatestLogCommand = new AsyncRelayCommand(LoadLatestAsync, () => CanImport);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && SelectedRound is not null);
        UpdateDetailsCommand = new AsyncRelayCommand(UpdateDetailsAsync, () => !IsBusy && SelectedRound is not null);
    }

    public ObservableCollection<AnalysisRoundViewModel> Rounds { get; } = [];
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand LatestLogCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand UpdateDetailsCommand { get; }
    public string Game
    {
        get => _game;
        set { if (IsGoalEditable && SetProperty(ref _game, value)) { LogPath = ""; NotifySummary(); } }
    }
    public double TargetFps
    {
        get => _targetFps;
        set { if (IsGoalEditable && double.IsFinite(value) && SetProperty(ref _targetFps, Math.Clamp(value, 15, 480))) NotifySummary(); }
    }
    public double MinimumLowFps => TargetFps * 0.9;
    public TuningMode Mode { get => _mode; set { if (IsGoalEditable && SetProperty(ref _mode, value)) NotifySummary(); } }
    public ManualQualityTier CurrentTier { get => _tier; set => SetProperty(ref _tier, value); }
    public string LogPath { get => _logPath; set { if (SetProperty(ref _logPath, value)) RefreshCommands(); } }
    public string LogDirectory { get => _logDirectory; set => SetProperty(ref _logDirectory, value); }
    public string GraphicsSettings { get => _graphicsSettings; set => SetProperty(ref _graphicsSettings, value); }
    public string Resolution { get => _resolution; set => SetProperty(ref _resolution, value); }
    public string Cpu { get => _cpu; set => SetProperty(ref _cpu, value); }
    public string Gpu { get => _gpu; set => SetProperty(ref _gpu, value); }
    public string Memory { get => _memory; set => SetProperty(ref _memory, value); }
    public string Scene { get => _scene; set => SetProperty(ref _scene, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifySummary(); } }
    public bool IsGoalEditable => !IsBusy && Rounds.Count == 0;
    public bool HasNoRounds => Rounds.Count == 0;
    public bool HasRounds => !HasNoRounds;
    public bool HasReachedTarget => _session.Rounds.Any(round => round.MeetsTarget(_session.Goal));
    public bool CanImport => !IsBusy && !HasReachedTarget;
    public string ImportLabel => HasReachedTarget ? "目标已达成" : $"分析第 {Rounds.Count + 1} 份日志";
    public string SessionLabel => HasReachedTarget ? "目标已达成 / 可以保存" : $"{Rounds.Count} 份日志 / 轮次不限";
    public string GoalLabel => $"平均 ≥ {TargetFps:0} FPS · 1% Low ≥ {MinimumLowFps:0.#}";
    public bool IsShareVisible { get => _isShareVisible; set => SetProperty(ref _isShareVisible, value); }
    public AnalysisRoundViewModel? SelectedRound
    {
        get => _selectedRound;
        set { if (SetProperty(ref _selectedRound, value)) { NotifySummary(); } }
    }
    public string AverageFps => SelectedRound?.Average ?? "--";
    public string LowFps => SelectedRound?.Low ?? "--";
    public string DeltaFps => SelectedRound?.Delta ?? "--";
    public string LowDelta => SelectedRound?.LowDelta ?? "--";
    public string ShareTitle => SelectedRound?.MeetsTarget == true ? "我的达标画质" : "我的画质实测";
    public string ShareVerdict => SelectedRound?.Verdict ?? "等待日志";
    public string ShareTier => SelectedRound?.Tier ?? "未填写";
    public string ShareSettings => SelectedRound?.Settings ?? "导入日志后，这里展示玩家填写的画面设置。";
    public string ShareResolution => AnalysisRoundViewModel.Empty(SelectedRound?.Round.Configuration.Resolution ?? "");
    public string ShareCpu => AnalysisRoundViewModel.Empty(SelectedRound?.Round.Configuration.Cpu ?? "");
    public string ShareGpu => AnalysisRoundViewModel.Empty(SelectedRound?.Round.Configuration.Gpu ?? "");
    public string ShareMemory => AnalysisRoundViewModel.Empty(SelectedRound?.Round.Configuration.Memory ?? "");
    public string ShareRound => SelectedRound?.RoundLabel ?? "等待日志";
    public string SharePrecision => SelectedRound?.Precision ?? "没有日志，不生成实测结论";
    public string ShareScene => SelectedRound?.Scene ?? "未填写";
    public string ShareDate => SelectedRound?.Round.ImportedAt.ToString("yyyy.MM.dd") ?? DateTime.Now.ToString("yyyy.MM.dd");
    public string ShareDuration => SelectedRound?.Round.Report.Metrics.Duration > TimeSpan.Zero
        ? $"{SelectedRound.Round.Report.Metrics.Duration.TotalSeconds:0} 秒"
        : "时长未记录";

    public Task InitializeAsync() => LoadPlanAsync(_savePath);

    public async Task LoadPlanAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var saved = await _files.ReadAsync<AnalysisSession>(path);
            if (saved is null || saved.Rounds.Count == 0) return;
            saved.Goal.Validate();
            if (!Guid.TryParseExact(saved.Id, "N", out _))
                throw new InvalidDataException("方案标识无效。");
            if (saved.Rounds.Any(round => !double.IsFinite(round.Report.Metrics.AverageFps)
                || round.Report.Metrics.AverageFps <= 0 || !double.IsFinite(round.Report.Metrics.LowFps)
                || round.Report.Metrics.LowFps < 0 || round.Number <= 0)
                || saved.Rounds.Select(round => round.Number).Distinct().Count() != saved.Rounds.Count)
                throw new InvalidDataException("保存的日志记录无效。");
            _session = saved;
            _game = saved.Game;
            _targetFps = saved.Goal.TargetFps;
            _mode = saved.Goal.Mode;
            RebuildRounds(saved.SavedRoundNumber ?? saved.Rounds[^1].Number);
            FillInputs(SelectedRound!.Round.Configuration);
            IsShareVisible = false;
            SaveStatus = "";
            Status = "已载入上次保存的方案记录。没有修改游戏设置；历史结果不代表本次正在采集。";
            RaisePropertyChanged(nameof(Game));
            RaisePropertyChanged(nameof(TargetFps));
            RaisePropertyChanged(nameof(Mode));
            NotifySummary();
        }
        catch (Exception exception) { Status = "方案记录未能载入：" + exception.Message; }
        finally { IsBusy = false; }
    }

    public async Task AnalyzeAsync()
    {
        if (!CanImport) return;
        IsBusy = true;
        SaveStatus = "";
        var configuration = CaptureConfiguration();
        var game = Game;
        var goal = new TuningGoal { TargetFps = TargetFps, Mode = Mode };
        try
        {
            var report = await _analysis.AnalyzeAsync(LogPath, goal, configuration.Tier);
            if (_session.Rounds.Any(round => round.Report.SourceHash.Length > 0 && round.Report.SourceHash == report.SourceHash))
                throw new InvalidOperationException("这份日志内容已导入，不重复算一轮。请录制新的游玩片段再比较。");
            if (_session.Rounds.Count == 0) _session = new AnalysisSession { Game = game, Goal = goal };
            var round = new AnalysisRound(_session.Rounds.Count + 1, report, configuration);
            _session.Rounds.Add(round);
            RebuildRounds(round.Number);
            Status = round.MeetsTarget(goal)
                ? "已达到目标！可以停在本次配置并保存分享图，不需要继续测。这是已测达标方案，不是全局最优证明。"
                : "分析完成。参考建议手动调整，填写下一次实际设置，再导入新日志比较。";
        }
        catch (Exception exception) { Status = "没有新增记录：" + exception.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadLatestAsync()
    {
        try
        {
            var latest = _analysis.FindLatestLog(LogDirectory);
            if (latest is null) { Status = "该目录没有日志，请先录制或手动选择文件。"; return; }
            LogPath = latest;
            await AnalyzeAsync();
        }
        catch (Exception exception) { Status = "读取日志目录失败：" + exception.Message; }
    }

    public void ReportExport(string path) => SaveStatus = "分享图片已保存：" + path;

    public async Task SaveAsync()
    {
        if (SelectedRound is null) return;
        IsBusy = true;
        try
        {
            _session.SavedRoundNumber = SelectedRound.Number;
            _session.SavedAt = DateTimeOffset.Now;
            await _files.WriteAtomicAsync(Path.Combine(PlansDirectory, _session.Id + ".json"), _session);
            await _files.WriteAtomicAsync(_savePath, _session);
            SaveStatus = "方案已保存到本机，下次打开可查看。只保存记录，不应用游戏配置。";
        }
        catch (Exception exception) { SaveStatus = "保存失败：" + exception.Message; }
        finally { IsBusy = false; }
    }

    private Task UpdateDetailsAsync()
    {
        if (SelectedRound is null) return Task.CompletedTask;
        var previous = SelectedRound.Round;
        var configuration = CaptureConfiguration();
        var recommendation = ManualRecommendationPlanner.Recommend(previous.Report.Metrics, _session.Goal, configuration.Tier);
        var updated = previous with { Configuration = configuration, Report = previous.Report with { Recommendation = recommendation } };
        _session.Rounds[_session.Rounds.FindIndex(round => round.Number == previous.Number)] = updated;
        RebuildRounds(updated.Number);
        SaveStatus = "本轮说明已更新；请确认这些设置确实对应本轮日志，随后保存方案。";
        return Task.CompletedTask;
    }

    public void NewGoal()
    {
        if (IsBusy) return;
        _session = new AnalysisSession { Game = Game };
        Rounds.Clear();
        SelectedRound = null;
        LogPath = "";
        IsShareVisible = false;
        SaveStatus = "";
        Status = "新对比已准备好。已保存的旧方案仍在本机，直到你保存新的方案。";
        NotifySummary();
    }

    public void FillSelectedInputs()
    {
        if (SelectedRound is not null) FillInputs(SelectedRound.Round.Configuration);
    }

    private PlayerConfiguration CaptureConfiguration() => new()
    {
        Tier = CurrentTier, GraphicsSettings = GraphicsSettings.Trim(), Resolution = Resolution.Trim(),
        Cpu = Cpu.Trim(), Gpu = Gpu.Trim(), Memory = Memory.Trim(), Scene = Scene.Trim()
    };

    private void FillInputs(PlayerConfiguration configuration)
    {
        CurrentTier = configuration.Tier; GraphicsSettings = configuration.GraphicsSettings;
        Resolution = configuration.Resolution; Cpu = configuration.Cpu; Gpu = configuration.Gpu;
        Memory = configuration.Memory; Scene = configuration.Scene;
    }

    private void RebuildRounds(int selected)
    {
        Rounds.Clear();
        for (var index = 0; index < _session.Rounds.Count; index++)
            Rounds.Add(new AnalysisRoundViewModel(_session.Rounds[index], index > 0 ? _session.Rounds[index - 1] : null,
                _session.Rounds[0], _session.Goal));
        SelectedRound = Rounds.FirstOrDefault(round => round.Number == selected) ?? Rounds.LastOrDefault();
        NotifySummary();
    }

    private void NotifySummary()
    {
        foreach (var name in new[]
        {
            nameof(IsGoalEditable), nameof(HasNoRounds), nameof(HasRounds), nameof(HasReachedTarget), nameof(CanImport),
            nameof(ImportLabel), nameof(SessionLabel), nameof(GoalLabel), nameof(MinimumLowFps), nameof(AverageFps),
            nameof(LowFps), nameof(DeltaFps), nameof(LowDelta), nameof(ShareTitle), nameof(ShareVerdict), nameof(ShareTier),
            nameof(ShareSettings), nameof(ShareResolution), nameof(ShareCpu), nameof(ShareGpu), nameof(ShareMemory),
            nameof(ShareRound), nameof(SharePrecision), nameof(ShareDate), nameof(ShareDuration), nameof(ShareScene)
        }) RaisePropertyChanged(name);
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        AnalyzeCommand.RaiseCanExecuteChanged();
        LatestLogCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        UpdateDetailsCommand.RaiseCanExecuteChanged();
    }
}
