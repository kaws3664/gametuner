using System.Diagnostics;
using GameTuner.Core.Adapters;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;
using GameTuner.Core.Models;
using GameTuner.Core.Profiles;
using GameTuner.Core.Telemetry;

namespace GameTuner.Core.Optimization;

public sealed record TuningProgress(
    SessionState State,
    string Message,
    int RoundNumber,
    PerformanceMetrics? LiveMetrics = null,
    TimeSpan? StageElapsed = null);

public sealed record TuningDecision(
    TuningCandidate Candidate,
    string Reason,
    bool IsFinal);

public interface ITuningSessionController
{
    event EventHandler<TuningSession>? SessionChanged;
    event EventHandler<TuningProgress>? ProgressChanged;
    event EventHandler<TuningDecision>? DecisionRequired;
    event EventHandler<string>? NotificationRequested;

    Task<TuningSession> RunAsync(TuningGoal goal, CancellationToken cancellationToken = default);
    Task<TuningSession> DecideAsync(bool apply, CancellationToken cancellationToken = default);
    Task RestoreOriginalAsync(TuningSession session, CancellationToken cancellationToken = default);
}

/// <summary>
/// 通用调优会话控制器：一份 GameProfile 驱动一条链路。
/// - NvidiaLogCsv：启动游戏 → NVIDIA 浮层日志采样 → 等待用户手动退出 → 询问是否应用单项建议。
/// - GameBenchmark：启动游戏 → 内置 Benchmark 采样 → 连续三轮 → 自动应用最终档案。
/// 不绑定任何单一游戏，新增游戏只需注册 profile 并实现通用接口。
/// </summary>
public sealed class GameTuningSessionController(
    INvidiaAppService nvidia,
    IGameProfileAdapter config,
    IGameTelemetryCollector telemetry,
    IGameProcessGuard processGuard,
    TuningEngine engine,
    IAiAdvisor aiAdvisor,
    SessionStore sessionStore,
    AppSettings settings,
    AppPaths paths) : ITuningSessionController
{
    private TuningSession? _session;
    private IReadOnlyDictionary<string, string> _currentSettings = new Dictionary<string, string>();

    public GameProfile Profile => config.Profile;

    public event EventHandler<TuningSession>? SessionChanged;
    public event EventHandler<TuningProgress>? ProgressChanged;
    public event EventHandler<TuningDecision>? DecisionRequired;
    public event EventHandler<string>? NotificationRequested;

    public async Task<TuningSession> RunAsync(
        TuningGoal goal,
        CancellationToken cancellationToken = default)
    {
        goal.Validate();
        EnsureGameClosed();
        var snapshot = await config.ReadAsync(cancellationToken);
        if (snapshot.Settings.Count == 0)
        {
            throw new InvalidOperationException(
                $"尚未发现 {Profile.DisplayName} 的图形设置。请先启动游戏并保存一次图形设置。");
        }

        _session = new TuningSession
        {
            Goal = goal,
            OriginalConfigPath = snapshot.FullPath,
            OriginalConfigHash = snapshot.Hash,
            OriginalBackupPath = await config.BackupAsync("original", cancellationToken),
            Message = "先采集当前自定义配置，不套用官方预设。退出游戏始终由你手动操作。"
        };
        _currentSettings = snapshot.Settings;
        Publish(_session);

        try
        {
            return Profile.TelemetrySource switch
            {
                TelemetrySource.NvidiaLogCsv => await RunNvidiaLogProfileAsync(cancellationToken),
                TelemetrySource.GameBenchmark => await RunBenchmarkProfileAsync(cancellationToken),
                _ => throw new NotSupportedException($"未知的遥测数据来源：{Profile.TelemetrySource}")
            };
        }
        catch (Exception exception)
        {
            return await FailAsync(_session, exception);
        }
    }

    public async Task<TuningSession> DecideAsync(
        bool apply,
        CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("当前没有等待确认的调优会话。");
        if (Profile.TelemetrySource != TelemetrySource.NvidiaLogCsv)
        {
            throw new InvalidOperationException($"{Profile.DisplayName} 链路会自动应用最终档案，无需人工确认。");
        }

        EnsureGameClosed();
        var candidate = session.PendingCandidate
            ?? throw new InvalidOperationException("没有待确认的个性化设置。");
        if (session.Rounds.Count == 0)
        {
            throw new InvalidOperationException("还没有完成有效采集。");
        }

        var isFinal = session.Rounds.Count >= 3 || session.FinalCandidate is not null;
        if (isFinal && apply)
        {
            await config.RestoreAsync(session.OriginalBackupPath, cancellationToken);
            if (candidate.Preset is not null)
            {
                await nvidia.SetGamePresetAsync(Profile.Id, candidate.Preset.Value, cancellationToken);
            }

            if (candidate.SettingOverrides.Count > 0)
            {
                await config.ApplyOverridesAsync(candidate.SettingOverrides, cancellationToken);
            }

            session.Message = $"已应用最终自定义档案：{candidate.Label}。";
        }
        else if (apply)
        {
            if (candidate.Preset is not null)
            {
                await nvidia.SetGamePresetAsync(Profile.Id, candidate.Preset.Value, cancellationToken);
            }

            await config.ApplyOverridesAsync(candidate.SettingOverrides, cancellationToken);
            _currentSettings = (await config.ReadAsync(cancellationToken)).Settings;
            session.Message = $"已应用：{candidate.Label}。请开始下一轮 {settings.CaptureDurationSeconds / 60} 分钟采集。";
        }
        else
        {
            session.Message = isFinal
                ? "未应用最终建议，保留当前游戏设置。"
                : "已跳过建议，保留当前配置并结束调优。";
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

        return await RunNvidiaLogProfileAsync(cancellationToken);
    }

    public async Task RestoreOriginalAsync(
        TuningSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.OriginalBackupPath))
        {
            throw new InvalidOperationException("该会话没有可用的原始配置备份。");
        }

        EnsureGameClosed();
        await config.RestoreAsync(session.OriginalBackupPath, cancellationToken);
        session.State = SessionState.Idle;
        session.Message = "已恢复开始调优前的原始配置。";
        Publish(session);
        await sessionStore.SaveAsync(session, cancellationToken);
    }

    private async Task<TuningSession> RunNvidiaLogProfileAsync(CancellationToken cancellationToken)
    {
        var session = _session!;
        var roundNumber = session.Rounds.Count + 1;
        session.State = SessionState.WaitingForGame;
        Publish(session);
        Report(
            SessionState.WaitingForGame,
            $"第 {roundNumber} 轮：无限等待启动，不会自动关闭游戏。",
            roundNumber);
        await nvidia.LaunchGameAsync(Profile.Id, cancellationToken);
        await processGuard.WaitForStartAsync(
            Profile.PrimaryProcessName,
            new Progress<TimeSpan>(elapsed => Report(
                SessionState.WaitingForGame,
                $"等待 {Profile.DisplayName} 启动 {Elapsed(elapsed)}，启动慢不会超时。",
                roundNumber)),
            cancellationToken);
        var logBaseline = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var captureDuration = TimeSpan.FromSeconds(settings.CaptureDurationSeconds);
        session.State = SessionState.Capturing;
        Publish(session);
        Notify($"第 {roundNumber} 轮：请用 NVIDIA 浮层开始记录统计数据。工具实时读取，满 {captureDuration.TotalMinutes:0} 分钟自动分析，无需手动停止。");
        var metrics = await telemetry.CollectAsync(
            new GameTelemetryOptions(
                Profile.PrimaryProcessName,
                settings.TelemetryLogDirectory,
                MinimumDuration: captureDuration,
                LogBaseline: logBaseline),
            new Progress<PerformanceMetrics>(live =>
                Report(
                    SessionState.Capturing,
                    $"第 {roundNumber} 轮 NVIDIA 日志实时采集中 {Elapsed(clock.Elapsed)}。满 {captureDuration.TotalMinutes:0} 分钟自动分析，无需停止记录。",
                    roundNumber,
                    live,
                    clock.Elapsed)),
            cancellationToken);
        if (!metrics.HasUsableFrames)
        {
            throw new InvalidOperationException("日志有效帧数据不足，无法比较画质。");
        }

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
            session.PendingCandidate = config.CreateFallbackCandidate(
                _currentSettings,
                metrics,
                session.Goal);
            if (session.PendingCandidate.SettingOverrides.Count == 0)
            {
                throw new InvalidOperationException("当前设置已到保守下限。请结束采集，不会自动进一步降画质。");
            }

            session.Message =
                $"{Explain(session.Goal, metrics)} 建议：{session.PendingCandidate.Label}。{session.PendingCandidate.Rationale}";
        }

        session.State = SessionState.AwaitingDecision;
        Publish(session);
        session.State = SessionState.WaitingForExit;
        Publish(session);
        Report(SessionState.WaitingForExit, session.Message, roundNumber, metrics);
        Notify($"{captureDuration.TotalMinutes:0} 分钟日志已收集，正在后台生成个性化建议。请你自行退出游戏；退出后软件会询问是否应用，不会自动退出游戏。");
        await processGuard.WaitForExitAsync(
            Profile.ProcessNames,
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        DecisionRequired?.Invoke(
            this,
            new TuningDecision(session.PendingCandidate, session.Message, isFinal));
        await sessionStore.SaveAsync(session, cancellationToken);
        return session;
    }

    private async Task<TuningSession> RunBenchmarkProfileAsync(CancellationToken cancellationToken)
    {
        var session = _session!;
        var applications = await nvidia.GetApplicationsAsync(Profile.Id, cancellationToken);
        var application = applications.FirstOrDefault(item =>
            string.Equals(item.Name, Profile.Id, StringComparison.OrdinalIgnoreCase));
        if (application is null)
        {
            throw new InvalidOperationException($"NVIDIA App 未识别 {Profile.DisplayName}。");
        }

        if (!application.OptimizationSupported || application.AvailablePresets.Count == 0)
        {
            throw new InvalidOperationException($"当前 NVIDIA App 未向 {Profile.DisplayName} 提供可用优化预设。");
        }

        var rounds = new List<RoundResult>();
        string? earlyStopReason = null;
        var current = engine.InitialCandidate(session.Goal, application.AvailablePresets);

        for (var roundNumber = 1; roundNumber <= 3; roundNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(SessionState.Applying, $"第 {roundNumber} 轮：正在应用 {current.Label}", roundNumber);
            SetState(session, SessionState.Applying);

            var baseSnapshot = rounds.Count == 0
                ? session.OriginalBackupPath
                : rounds[^1].ConfigSnapshotPath;
            await ApplyCandidateAsync(current, baseSnapshot, cancellationToken);
            var appliedConfig = await config.ReadAsync(cancellationToken);
            var snapshotPath = await SaveRoundSnapshotAsync(
                session,
                roundNumber,
                appliedConfig.RawContent,
                cancellationToken);
            var benchmarkBaseline = BenchmarkBaseline.Capture(Profile.BenchmarkDirectory);
            await nvidia.LaunchGameAsync(Profile.Id, cancellationToken);
            Report(
                SessionState.WaitingForGame,
                $"启动命令已发出，正在等待 {Profile.DisplayName}。检测到游戏后才开始本轮计时，启动较慢不会超时。",
                roundNumber);
            SetState(session, SessionState.WaitingForGame);
            NotificationRequested?.Invoke(
                this,
                $"正在等待 {Profile.DisplayName} 启动。检测到游戏后才开始计时，请进入游戏后运行内置 Benchmark。");

            var gameStartProgress = new Progress<TimeSpan>(elapsed =>
                Report(
                    SessionState.WaitingForGame,
                    $"等待游戏启动 {FormatElapsed(elapsed)}，检测到 {Profile.PrimaryProcessName}.exe 后才开始计时。",
                    roundNumber,
                    stageElapsed: elapsed));
            await processGuard.WaitForStartAsync(
                Profile.PrimaryProcessName,
                gameStartProgress,
                cancellationToken);
            var detectedAt = Stopwatch.StartNew();
            Report(
                SessionState.WaitingForBenchmark,
                $"已检测到 {Profile.DisplayName}，本轮计时已开始。请进入“设置 > 图形 > 运行基准测试”。",
                roundNumber,
                stageElapsed: TimeSpan.Zero);
            NotificationRequested?.Invoke(
                this,
                $"已检测到 {Profile.DisplayName}，本轮计时已开始。请运行内置 Benchmark，完成后会自动记录。");
            SetState(session, SessionState.WaitingForBenchmark);

            var reminderSent = false;
            var liveProgress = new Progress<PerformanceMetrics>(metrics =>
            {
                Report(
                    SessionState.WaitingForBenchmark,
                    $"等待 Benchmark 结果 · 本轮已计时 {FormatElapsed(detectedAt.Elapsed)}",
                    roundNumber,
                    metrics,
                    detectedAt.Elapsed);
                if (!reminderSent &&
                    settings.BenchmarkReminderSeconds > 0 &&
                    metrics.Duration >= TimeSpan.FromSeconds(settings.BenchmarkReminderSeconds))
                {
                    reminderSent = true;
                    NotificationRequested?.Invoke(
                        this,
                        $"已等待 {settings.BenchmarkReminderSeconds / 60} 分钟仍未检测到新的 Benchmark 结果。请确认已进入“设置 > 图形 > 运行基准测试”，完成后工具会自动继续。");
                }
            });
            var metrics = await telemetry.CollectAsync(
                new GameTelemetryOptions(
                    Profile.PrimaryProcessName,
                    Profile.BenchmarkDirectory,
                    benchmarkBaseline),
                liveProgress,
                cancellationToken);
            if (metrics.Bottleneck == BottleneckKind.Unknown)
            {
                metrics.Bottleneck = Rdr2TelemetryCollector.ClassifyBottleneck(metrics);
            }

            var (outcome, performanceScore) = engine.Evaluate(session.Goal, metrics);
            var score = engine.ScoreCandidate(session.Goal, current, metrics);
            var round = new RoundResult
            {
                RoundNumber = roundNumber,
                Candidate = current.Clone(),
                Metrics = metrics,
                Outcome = outcome,
                Score = score,
                ConfigHash = appliedConfig.Hash,
                ConfigSnapshotPath = snapshotPath,
                Explanation = BuildRoundExplanation(session.Goal, current, metrics, outcome, performanceScore)
            };
            rounds.Add(round);
            session.Rounds.Add(round);
            PublishSession(session);
            Report(
                SessionState.WaitingForExit,
                $"第 {roundNumber} 轮完成：平均 {metrics.AverageFps:0.0} FPS，1% Low {metrics.LowFps:0.0} FPS。请退出游戏。",
                roundNumber,
                metrics);

            if (outcome == RoundOutcome.Invalid)
            {
                throw new InvalidOperationException(
                    $"本轮没有采集到可用帧数据。请确认 {Profile.DisplayName} 内置 Benchmark 已完整跑完，并检查 {Profile.BenchmarkDirectory} 是否生成了结果文件。");
            }

            SetState(session, SessionState.WaitingForExit);
            await ExitGameAfterRoundAsync(session, roundNumber, cancellationToken);

            if (TuningEngine.IsCpuBound(metrics))
            {
                earlyStopReason = "检测到 CPU 瓶颈，继续降低画质收益有限，已提前结束搜索。";
                session.Message = earlyStopReason;
                NotificationRequested?.Invoke(
                    this,
                    "检测到 CPU 受限。继续降低画质通常不会明显提升帧率，已提前结束本轮搜索。");
                break;
            }

            if (roundNumber == 3)
            {
                break;
            }

            current = DetermineNextCandidate(
                session.Goal,
                current,
                round,
                rounds,
                application.AvailablePresets,
                appliedConfig.Settings);
        }

        var best = await SelectFinalCandidateAsync(session.Goal, rounds, cancellationToken);
        session.FinalCandidate = best.Candidate.Clone();
        session.Message = best.Explanation;
        SetState(session, SessionState.Applying);
        Report(SessionState.Applying, "三轮完成，正在应用最终档案", rounds.Count);
        await ApplyRoundSnapshotAsync(best, cancellationToken);
        session.State = SessionState.Completed;
        session.CompletedAt = DateTimeOffset.Now;
        session.Message =
            $"最终选择 {session.FinalCandidate.Label}。" +
            $"平均 {best.Metrics.AverageFps:0.0} FPS，1% Low {best.Metrics.LowFps:0.0} FPS。" +
            (best.Explanation.Length > 0 ? $" {best.Explanation}" : "") +
            (earlyStopReason is null ? "" : $" {earlyStopReason}");
        PublishSession(session);
        Report(SessionState.Completed, session.Message, rounds.Count);
        NotificationRequested?.Invoke(this, "三轮调优完成，最终画质档案已应用。");
        await sessionStore.SaveAsync(session, cancellationToken);
        return session;
    }

    private TuningCandidate DetermineNextCandidate(
        TuningGoal goal,
        TuningCandidate current,
        RoundResult latest,
        IReadOnlyList<RoundResult> rounds,
        IReadOnlyCollection<QualityPreset> availablePresets,
        IReadOnlyDictionary<string, string> fallbackSettings)
    {
        if (rounds.Count == 1)
        {
            var adjacent = engine.NextPresetCandidate(goal, current, latest.Metrics, availablePresets);
            if (adjacent.CacheKey != current.CacheKey)
            {
                return adjacent;
            }

            return config.CreateFallbackCandidate(fallbackSettings, latest.Metrics, goal);
        }

        // Round three is a confirmation run of the best candidate so the final
        // choice is not based on a single noisy Benchmark.
        return engine.SelectBest(rounds, goal).Candidate.Clone();
    }

    private async Task<RoundResult> SelectFinalCandidateAsync(
        TuningGoal goal,
        IReadOnlyList<RoundResult> rounds,
        CancellationToken cancellationToken)
    {
        if (rounds.Count == 0)
        {
            throw new InvalidOperationException("没有完成任何有效轮次。");
        }

        var valid = rounds.Where(round => round.Metrics.HasUsableFrames).ToArray();
        if (valid.Length == 0)
        {
            throw new InvalidOperationException("所有轮次都没有可用数据。");
        }

        var targetMeeting = valid
            .Where(round => engine.Evaluate(goal, round.Metrics).Outcome == RoundOutcome.MeetsTarget)
            .ToArray();
        var allowed = targetMeeting.Length > 0 ? targetMeeting : valid;
        var allowedCandidates = allowed.Select(round => round.Candidate).ToArray();
        var advice = await aiAdvisor.ReviewAsync(
            goal,
            allowed
                .OrderByDescending(round => round.Score)
                .First()
                .Metrics,
            allowedCandidates,
            cancellationToken);
        var selected = allowed.FirstOrDefault(round =>
            round.Candidate.Id == advice.CandidateId);
        selected ??= engine.SelectBest(allowed, goal);
        selected.Explanation = advice.Explanation;
        selected.Score = engine.ScoreCandidate(goal, selected.Candidate, selected.Metrics);
        return selected;
    }

    /// <summary>
    /// 轮次结束后关闭游戏进入下一轮：
    /// BenchmarkExitGraceSeconds &gt; 0 时先给用户手动退出的宽限期，超时后自动请求关闭并强制结束进程（全自动循环）；
    /// = 0 时完全等待用户手动退出（旧行为，ExitWaitTimeoutSeconds 超时则失败）。
    /// </summary>
    private async Task ExitGameAfterRoundAsync(
        TuningSession session,
        int roundNumber,
        CancellationToken cancellationToken)
    {
        var grace = TimeSpan.FromSeconds(settings.BenchmarkExitGraceSeconds);
        if (grace <= TimeSpan.Zero)
        {
            NotificationRequested?.Invoke(this, "本轮采样完成，请完全退出游戏。");
            var exited = await processGuard.WaitForExitAsync(
                Profile.ProcessNames,
                TimeSpan.FromSeconds(settings.ExitWaitTimeoutSeconds),
                cancellationToken);
            if (!exited)
            {
                throw new TimeoutException("等待游戏退出超时。配置未进入下一轮。");
            }

            return;
        }

        NotificationRequested?.Invoke(
            this,
            $"本轮采样完成，进入自动退出流程：{settings.BenchmarkExitGraceSeconds} 秒内手动退出则立即继续，超时后自动关闭游戏进入下一轮。");
        var exitedManually = await processGuard.WaitForExitAsync(
            Profile.ProcessNames,
            grace,
            cancellationToken);
        if (exitedManually)
        {
            Report(
                SessionState.WaitingForExit,
                $"第 {roundNumber} 轮完成，已检测到游戏退出，进入下一轮。",
                roundNumber);
            return;
        }

        NotificationRequested?.Invoke(this, "未检测到手动退出，正在自动关闭游戏。");
        var stopped = await processGuard.StopAsync(
            Profile.ProcessNames,
            grace,
            cancellationToken);
        if (!stopped)
        {
            throw new TimeoutException("自动关闭游戏失败，进程仍在运行。请手动退出后重新开始。");
        }

        Report(
            SessionState.WaitingForExit,
            $"第 {roundNumber} 轮完成，已自动退出游戏，进入下一轮。",
            roundNumber);
    }

    private async Task ApplyCandidateAsync(
        TuningCandidate candidate,
        string? baseSnapshotPath,
        CancellationToken cancellationToken)
    {
        EnsureGameClosed();
        if (!string.IsNullOrWhiteSpace(baseSnapshotPath) && File.Exists(baseSnapshotPath))
        {
            await config.RestoreAsync(baseSnapshotPath, cancellationToken);
        }

        if (candidate.Preset is not null)
        {
            await nvidia.SetGamePresetAsync(Profile.Id, candidate.Preset.Value, cancellationToken);
        }

        if (candidate.SettingOverrides.Count > 0)
        {
            await config.ApplyOverridesAsync(candidate.SettingOverrides, cancellationToken);
        }
    }

    private async Task ApplyRoundSnapshotAsync(
        RoundResult round,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(round.ConfigSnapshotPath) ||
            !File.Exists(round.ConfigSnapshotPath))
        {
            throw new InvalidOperationException("最终轮次没有可用的配置快照。");
        }

        EnsureGameClosed();
        await config.RestoreAsync(round.ConfigSnapshotPath, cancellationToken);
    }

    private async Task<string> SaveRoundSnapshotAsync(
        TuningSession session,
        int roundNumber,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(paths.Sessions, session.Id.ToString("N") + "-configs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"round-{roundNumber}.xml");
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return path;
    }

    private static string BuildRoundExplanation(
        TuningGoal goal,
        TuningCandidate candidate,
        PerformanceMetrics metrics,
        RoundOutcome outcome,
        double performanceScore)
    {
        var verdict = outcome == RoundOutcome.MeetsTarget ? "达到目标" : "未达到目标";
        var bottleneck = RuleBasedAiAdvisor.DescribeBottleneck(metrics.Bottleneck);
        return $"{verdict}：平均 {metrics.AverageFps:0.0} / 目标 {goal.TargetFps:0}，" +
               $"1% Low {metrics.LowFps:0.0} / 底线 {goal.MinimumLowFps:0}。" +
               $"{bottleneck}。综合分 {performanceScore:0.00}。";
    }

    private static string Explain(TuningGoal goal, PerformanceMetrics metrics) =>
        $"平均 {metrics.AverageFps:0.0} / 目标 {goal.TargetFps:0}；1% Low {metrics.LowFps:0.0} / 底线 {goal.MinimumLowFps:0}。" +
        RuleBasedAiAdvisor.DescribeBottleneck(metrics.Bottleneck) + "。";

    private void EnsureGameClosed()
    {
        if (processGuard.IsAnyRunning(Profile.ProcessNames))
        {
            throw new InvalidOperationException(
                $"{Profile.DisplayName} 仍在运行。请你手动退出游戏，软件不会代为关闭。");
        }
    }

    private async Task<TuningSession> FailAsync(TuningSession session, Exception exception)
    {
        session.State = exception is OperationCanceledException
            ? SessionState.Cancelled
            : SessionState.Failed;
        session.Message = exception.Message;
        Publish(session);
        await sessionStore.SaveAsync(session, CancellationToken.None);
        throw exception;
    }

    private void Report(
        SessionState state,
        string message,
        int roundNumber,
        PerformanceMetrics? liveMetrics = null,
        TimeSpan? stageElapsed = null)
    {
        ProgressChanged?.Invoke(
            this,
            new TuningProgress(state, message, roundNumber, liveMetrics, stageElapsed));
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    private static string Elapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    private void Notify(string message) => NotificationRequested?.Invoke(this, message);

    private void Publish(TuningSession session) => SessionChanged?.Invoke(this, session);

    private void SetState(TuningSession session, SessionState state)
    {
        session.State = state;
        Publish(session);
    }

    private void PublishSession(TuningSession session) => SessionChanged?.Invoke(this, session);

    private string _currentSettingsCacheKey() => string.Join(';',
        _currentSettings.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
}
