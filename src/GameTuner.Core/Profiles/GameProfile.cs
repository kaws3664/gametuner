using System.IO;

namespace GameTuner.Core.Profiles;

/// <summary>调优遥测数据来源，决定 TuningSessionController 的运行链路。</summary>
public enum TelemetrySource
{
    /// <summary>007 First Light：NVIDIA 浮层记录的性能 CSV（AppData\Local\nvlog）。</summary>
    NvidiaLogCsv,

    /// <summary>Red Dead Redemption 2：游戏内置 Benchmark 结果文件（Benchmarks 目录）。</summary>
    GameBenchmark
}

/// <summary>
/// 游戏档案（Profile）：一份 profile 定义一款游戏接入通用调优框架所需的全部元数据。
/// 新增游戏只需实现 IGameProfileAdapter / IGameTelemetryCollector 并注册一份 GameProfile。
/// </summary>
public sealed record GameProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ProcessNames,
    TelemetrySource TelemetrySource,
    string ConfigDisplayName,
    string ConfigPathDisplay,
    string BenchmarkDirectory,
    string DataSourceDescription,
    string ConfigScopeDescription,
    string MethodStepDescription)
{
    /// <summary>启动后用于检测游戏进程的主进程名。</summary>
    public string PrimaryProcessName => ProcessNames.Count > 0 ? ProcessNames[0] : DisplayName;

    public bool UsesNvidiaLaunch => TelemetrySource == TelemetrySource.NvidiaLogCsv;
}

public static class GameProfiles
{
    private static readonly string MyDocuments =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static GameProfile FirstLight { get; } = new(
        Id: "007 First Light",
        DisplayName: "007 First Light",
        ProcessNames: ["007FirstLight"],
        TelemetrySource: TelemetrySource.NvidiaLogCsv,
        ConfigDisplayName: "007 注册表白名单",
        ConfigPathDisplay: @"HKCU\Software\IO Interactive\007 First Light",
        BenchmarkDirectory: "",
        DataSourceDescription: "NVIDIA 浮层 CSV（AppData\\Local\\nvlog）",
        ConfigScopeDescription: "只备份/修改 007 图形注册表白名单，绝不自动退游戏",
        MethodStepDescription: "首轮保留当前配置，启动后由你记录 NVIDIA 日志；到点自动分析，由你手动退出后确认应用。");

    public static GameProfile RedDeadRedemption2 { get; } = new(
        Id: "Red Dead Redemption 2",
        DisplayName: "Red Dead Redemption 2",
        ProcessNames: ["RDR2", "PlayRDR2"],
        TelemetrySource: TelemetrySource.GameBenchmark,
        ConfigDisplayName: "RDR2 system.xml 白名单",
        ConfigPathDisplay: Path.Combine(
            MyDocuments,
            "Rockstar Games",
            "Red Dead Redemption 2",
            "Settings",
            "system.xml"),
        BenchmarkDirectory: Path.Combine(
            MyDocuments,
            "Rockstar Games",
            "Red Dead Redemption 2",
            "Benchmarks"),
        DataSourceDescription: "RDR2 内置 Benchmark（Benchmarks 目录）",
        ConfigScopeDescription: "只备份/修改 RDR2 图形白名单；每轮结束自动退出游戏进入下一轮，可配置关闭",
        MethodStepDescription: "每轮自动应用画质档位并启动游戏；进入游戏后运行一次内置 Benchmark，工具自动读取结果并自动退出进入下一轮；三轮结束自动应用最终档案。");

    public static IReadOnlyList<GameProfile> All { get; } =
        [FirstLight, RedDeadRedemption2];
}
