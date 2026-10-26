using GameTuner.Core.Models;
using GameTuner.Core.Profiles;

namespace GameTuner.Core.Adapters;

/// <summary>一份通用化的游戏配置快照，与具体存储（注册表 / XML）无关。</summary>
public sealed record GameConfigSnapshot(
    string FullPath,
    string RawContent,
    string Hash,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// 通用游戏配置适配层：一份 profile 一个实现。
/// 覆盖配置读取、备份、覆盖应用、恢复与兜底候选生成，
/// 使 TuningSessionController 不再依赖任何单一游戏的专属接口。
/// </summary>
public interface IGameProfileAdapter
{
    GameProfile Profile { get; }

    Task<GameConfigSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    Task<string> BackupAsync(string label, CancellationToken cancellationToken = default);

    Task<GameConfigSnapshot> ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);

    TuningCandidate CreateFallbackCandidate(
        IReadOnlyDictionary<string, string> currentSettings,
        PerformanceMetrics metrics,
        TuningGoal goal);
}
