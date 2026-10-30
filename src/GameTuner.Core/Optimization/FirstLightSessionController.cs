using System.Diagnostics;
using GameTuner.Core.Adapters.FirstLight;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;
using GameTuner.Core.Models;
using GameTuner.Core.Telemetry;

namespace GameTuner.Core.Optimization;

public sealed record FirstLightDecision(TuningCandidate Candidate, string Reason, bool IsFinal);

public interface IFirstLightSessionController
{
    event EventHandler<TuningSession>? SessionChanged;
    event EventHandler<TuningProgress>? ProgressChanged;
    event EventHandler<FirstLightDecision>? DecisionRequired;
    event EventHandler<string>? NotificationRequested;
    Task<TuningSession> RunAsync(TuningGoal goal, CancellationToken cancellationToken = default);
    Task<TuningSession> DecideAsync(bool apply, CancellationToken cancellationToken = default);
    Task RestoreOriginalAsync(TuningSession session, CancellationToken cancellationToken = default);
}

public sealed class FirstLightSessionController(
    INvidiaAppService nvidia,
    IFirstLightProfileAdapter config,
    INvidiaLogTelemetryCollector telemetry,
    IFirstLightProcessGuard processGuard,
    TuningEngine engine,
    SessionStore sessionStore,
    AppSettings settings) : IFirstLightSessionController
{
    private TuningSession? _session;
    private IReadOnlyDictionary<string, string> _currentSettings = new Dictionary<string, string>();

    public event EventHandler<TuningSession>? SessionChanged;
    public event EventHandler<TuningProgress>? ProgressChanged;
    public event EventHandler<FirstLightDecision>? DecisionRequired;
    public event EventHandler<string>? NotificationRequested;

    public async Task<TuningSession> RunAsync(TuningGoal goal, CancellationToken cancellationToken = default)
    {
        goal.Validate();
        EnsureGameClosed();
        var original = await config.ReadAsync(cancellationToken);
        if (original.Settings.Count == 0)
            throw new InvalidOperationException("请先启动一次 007 并保存图形设置。");
        _session = new TuningSession
        {
            Goal = goal,
            OriginalConfigPath = original.FullPath,
            OriginalConfigHash = original.Hash,
            OriginalBackupPath = await config.BackupAsync("original", cancellationToken),
            Message = "先采集当前自定义配置，不套用官方预设。退出游戏始终由你手动操作。"
        };
        _currentSettings = original.Settings;
        Publish(_session);
        try { return await MeasureNextRoundAsync(cancellationToken); }
        catch (Exception exception) { return await FailAsync(_session, exception); }
    }

    public async Task<TuningSession> DecideAsync(bool apply, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("当前没有等待确认的调优会话。");
        EnsureGameClosed();
        var candidate = session.PendingCandidate ?? throw new InvalidOperationException("没有待确认的个性化设置。");
        if (session.Rounds.Count == 0) throw new InvalidOperationException("还没有完成有效采集。");
        var isFinal = session.Rounds.Count >= 3 || session.FinalCandidate is not null;
        if (isFinal && apply)
        {
            await config.RestoreAsync(session.OriginalBackupPath, cancellationToken);
            if (candidate.Preset is not null)
                await nvidia.SetGamePresetAsync(FirstLightProfileAdapter.GameName, candidate.Preset.Value, cancellationToken);
            if (candidate.SettingOverrides.Count > 0)
                await config.ApplyOverridesAsync(candidate.SettingOverrides, cancellationToken);
            session.Message = $"已应用最终自定义档案：{candidate.Label}。";
        }
        else if (apply)
        {
            if (candidate.Preset is not null)
                await nvidia.SetGamePresetAsync(FirstLightProfileAdapter.GameName, candidate.Preset.Value, cancellationToken);
            await config.ApplyOverridesAsync(candidate.SettingOverrides, cancellationToken);
            _currentSettings = (await config.ReadAsync(cancellationToken)).Settings;
            session.Message = $"已应用：{candidate.Label}。请开始下一轮 5 分钟采集。";
        }
        else
        {
            session.Message = isFinal ? "未应用最终建议，保留当前游戏设置。" : "已跳过建议，保留当前配置并结束调优。";
        }
        session.PendingCandidate = null;
        if (isFinal || !apply)
        {
            session.State = SessionState.Completed;
            session.CompletedAt = DateTimeOffset.Now;
            Publish(session);
            await sessionStore.SaveAsync(session, cancellationToken);
            Notify(session.Message);
            return session;
        }
        Publish(session);
        await sessionStore.SaveAsync(session, cancellationToken);
        if (session.Rounds.Count == 2)
        {
            var best = engine.SelectBest(session.Rounds, session.Goal).Candidate.Clone();
            if (best.CacheKey != _currentSettingsCacheKey())
            {
                await config.RestoreAsync(session.OriginalBackupPath, cancellationToken);
                await config.ApplyOverridesAsync(best.SettingOverrides, cancellationToken);
                _currentSettings = (await config.ReadAsync(cancellationToken)).Settings;
            }
        }
        return await MeasureNextRoundAsync(cancellationToken);
    }

    private async Task<TuningSession> MeasureNextRoundAsync(CancellationToken cancellationToken)
    {
        var session = _session!;
        try
        {
            var roundNumber = session.Rounds.Count + 1;
            session.State = SessionState.WaitingForGame;
            Publish(session);
            Report(SessionState.WaitingForGame, $"第 {roundNumber} 轮：无限等待启动，不会自动关闭游戏。", roundNumber);
            await nvidia.LaunchGameAsync(FirstLightProfileAdapter.GameName, cancellationToken);
            await processGuard.WaitForStartAsync(
                new Progress<TimeSpan>(elapsed => Report(SessionState.WaitingForGame,
                    $"等待 007 启动 {Elapsed(elapsed)}，启动慢不会超时。", roundNumber, elapsed: elapsed)),
                cancellationToken);
            var logBaseline = DateTimeOffset.Now;
            var clock = Stopwatch.StartNew();
            var captureDuration = TimeSpan.FromSeconds(settings.CaptureDurationSeconds);
            session.State = SessionState.Capturing;
            Publish(session);
            Notify($"第 {roundNumber} 轮：请用 NVIDIA 浮层开始记录统计数据。工具实时读取，满 {captureDuration.TotalMinutes:0} 分钟自动分析，无需手动停止。");
            var metrics = await telemetry.CollectAsync(
                new LogCaptureOptions(
                    settings.TelemetryLogDirectory,
                    logBaseline,
                    TimeSpan.FromSeconds(settings.CaptureDurationSeconds)),
                new Progress<PerformanceMetrics>(live =>
                {
                    Report(SessionState.Capturing,
                        $"第 {roundNumber} 轮 NVIDIA 日志实时采集中 {Elapsed(clock.Elapsed)}。满 {captureDuration.TotalMinutes:0} 分钟自动分析，无需停止记录。",
                        roundNumber, live, clock.Elapsed);
                }), cancellationToken);
            if (!metrics.HasUsableFrames) throw new InvalidOperationException("日志有效帧数据不足，无法比较画质。");

            var snapshot = await config.ReadAsync(cancellationToken);
            var candidate = new TuningCandidate
            {
                Label = roundNumber == 1 ? "当前自定义配置（基线）" : $"第 {roundNumber} 轮自定义配置",
                SettingOverrides = new Dictionary<string, string>(_currentSettings, StringComparer.Ordinal),
                Rationale = "基于本机实际运行数据，而非通用官方档位。"
            };
            var round = new RoundResult
            {
                RoundNumber = roundNumber,
                Candidate = candidate,
                Metrics = metrics,
                Outcome = engine.Evaluate(session.Goal, metrics).Outcome,
                ConfigHash = snapshot.Hash,
                Explanation = Explain(session.Goal, metrics)
            };
            if (roundNumber == 3)
            {
                var previousBest = engine.SelectBest(session.Rounds.Take(2).ToArray(), session.Goal);
                candidate.Id = previousBest.Candidate.Id;
                candidate.Label = previousBest.Candidate.Label + "（复测）";
            }
            round.Score = engine.ScoreCandidate(session.Goal, candidate, metrics);
            session.Rounds.Add(round);
            var isFinal = roundNumber >= 3;
            if (isFinal)
            {
                var best = engine.SelectBest(session.Rounds, session.Goal);
                session.FinalCandidate = best.Candidate.Clone();
                session.PendingCandidate = session.FinalCandidate.Clone();
                session.Message = $"三轮完成，建议采用 {best.Candidate.Label}：{best.Explanation}";
            }
            else if (TuningEngine.IsCpuBound(metrics))
            {
                session.FinalCandidate = round.Candidate.Clone();
                session.PendingCandidate = round.Candidate.Clone();
                session.Message = "检测到 CPU 瓶颈，继续降低 GPU 画质收益有限。建议保留当前配置。";
                isFinal = true;
            }
            else
            {
                session.PendingCandidate = config.CreateFallbackCandidate(_currentSettings, metrics, session.Goal);
                if (session.PendingCandidate.SettingOverrides.Count == 0)
                    throw new InvalidOperationException("当前设置已到保守下限。请结束采集，不会自动进一步降画质。");
                session.Message = $"{Explain(session.Goal, metrics)} 建议：{session.PendingCandidate.Label}。{session.PendingCandidate.Rationale}";
            }
            session.State = SessionState.AwaitingDecision;
            Publish(session);
            session.State = SessionState.WaitingForExit;
            Publish(session);
            Report(SessionState.WaitingForExit, session.Message, roundNumber, metrics);
            Notify($"{captureDuration.TotalMinutes:0} 分钟日志已收集，正在后台生成个性化建议。请你自行退出游戏；退出后软件会询问是否应用，不会自动退出游戏。");
            await processGuard.WaitForExitAsync(Timeout.InfiniteTimeSpan, cancellationToken);
            DecisionRequired?.Invoke(this, new FirstLightDecision(session.PendingCandidate, session.Message, isFinal));
            await sessionStore.SaveAsync(session, cancellationToken);
            return session;
        }
        catch (Exception exception) { return await FailAsync(session, exception); }
    }

    public async Task RestoreOriginalAsync(TuningSession session, CancellationToken cancellationToken = default)
    {
        EnsureGameClosed();
        await config.RestoreAsync(session.OriginalBackupPath, cancellationToken);
        session.State = SessionState.Idle;
        session.Message = "已恢复调优前的原始图形设置。";
        Publish(session);
        await sessionStore.SaveAsync(session, cancellationToken);
    }

    private async Task<TuningSession> FailAsync(TuningSession session, Exception exception)
    {
        session.State = exception is OperationCanceledException ? SessionState.Cancelled : SessionState.Failed;
        session.Message = exception.Message;
        Publish(session);
        await sessionStore.SaveAsync(session, CancellationToken.None);
        throw exception;
    }

    private void EnsureGameClosed()
    {
        if (processGuard.IsRunning())
            throw new InvalidOperationException("007 仍在运行。请你手动退出游戏，软件不会代为关闭。");
    }

    private static string Explain(TuningGoal goal, PerformanceMetrics metrics) =>
        $"平均 {metrics.AverageFps:0.0} / 目标 {goal.TargetFps:0}；1% Low {metrics.LowFps:0.0} / 底线 {goal.MinimumLowFps:0}。" +
        RuleBasedAiAdvisor.DescribeBottleneck(metrics.Bottleneck) + "。";
    private void Report(SessionState state, string message, int round, PerformanceMetrics? metrics = null, TimeSpan? elapsed = null) =>
        ProgressChanged?.Invoke(this, new TuningProgress(state, message, round, metrics, elapsed));
    private void Notify(string message) => NotificationRequested?.Invoke(this, message);
    private void Publish(TuningSession session) => SessionChanged?.Invoke(this, session);
    private static string Elapsed(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    private string _currentSettingsCacheKey() => string.Join(';',
        _currentSettings.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
}
