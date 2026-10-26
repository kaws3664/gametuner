using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;
using GameTuner.Core.Models;
using GameTuner.Core.Profiles;
using Microsoft.Win32;
using System.Security;
using System.Runtime.Versioning;

namespace GameTuner.Core.Adapters.FirstLight;

public sealed record FirstLightProfileSnapshot(
    string FullPath,
    string RawContent,
    string Hash,
    IReadOnlyDictionary<string, string> Settings);

public interface IFirstLightProfileAdapter
{
    Task<FirstLightProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task<string> BackupAsync(string label, CancellationToken cancellationToken = default);
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<FirstLightProfileSnapshot> ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken = default);
    TuningCandidate CreateFallbackCandidate(
        IReadOnlyDictionary<string, string> currentSettings,
        PerformanceMetrics metrics,
        TuningGoal goal);
}

public sealed class FirstLightProfileAdapter(
    AppPaths paths,
    IFirstLightProcessGuard processGuard,
    IFirstLightSettingsStore? settingsStore = null) : IFirstLightProfileAdapter, IGameProfileAdapter
{
    public const string GameName = "007 First Light";
    private readonly IFirstLightSettingsStore _store = settingsStore ?? CreateStore();

    private static IFirstLightSettingsStore CreateStore()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new RegistryFirstLightSettingsStore();
    }

    public Task<FirstLightProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        cancellationToken.ThrowIfCancellationRequested();
        var values = _store.Read();
        var content = System.Text.Json.JsonSerializer.Serialize(
            values.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value));
        return Task.FromResult(new FirstLightProfileSnapshot(
            RegistryFirstLightSettingsStore.KeyPath,
            content,
            HashUtility.Sha256Text(content),
            values));
    }

    public async Task<string> BackupAsync(string label, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var snapshot = await ReadAsync(cancellationToken);
        var target = Path.Combine(
            paths.Backups,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}-007-{snapshot.Hash[..8]}.json");
        await File.WriteAllTextAsync(target, snapshot.RawContent, cancellationToken);
        return target;
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        EnsureGameClosed();
        var content = await File.ReadAllTextAsync(backupPath, cancellationToken);
        var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(content)
            ?? throw new InvalidDataException("007 配置备份为空或格式无效。");
        FirstLightSettingsCatalog.Validate(values);
        _store.Write(values);
    }

    public async Task<FirstLightProfileSnapshot> ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        EnsureGameClosed();
        FirstLightSettingsCatalog.Validate(overrides);
        _store.Write(overrides);
        var snapshot = await ReadAsync(cancellationToken);
        foreach (var pair in overrides)
        {
            if (!snapshot.Settings.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
                throw new InvalidOperationException($"007 设置回读校验失败：{pair.Key} 期望 {pair.Value}，实际 {actual}。");
        }
        return snapshot;
    }

    public TuningCandidate CreateFallbackCandidate(
        IReadOnlyDictionary<string, string> currentSettings,
        PerformanceMetrics metrics,
        TuningGoal goal) =>
        FirstLightOptimizationPlanner.NextCandidate(currentSettings, metrics, goal);

    GameProfile IGameProfileAdapter.Profile => GameProfiles.FirstLight;

    async Task<GameConfigSnapshot> IGameProfileAdapter.ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadAsync(cancellationToken);
        return new GameConfigSnapshot(snapshot.FullPath, snapshot.RawContent, snapshot.Hash, snapshot.Settings);
    }

    async Task<GameConfigSnapshot> IGameProfileAdapter.ApplyOverridesAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken)
    {
        var snapshot = await ApplyOverridesAsync(overrides, cancellationToken);
        return new GameConfigSnapshot(snapshot.FullPath, snapshot.RawContent, snapshot.Hash, snapshot.Settings);
    }

    private void EnsureGameClosed()
    {
        if (processGuard.IsRunning())
        {
            throw new InvalidOperationException("007 First Light 正在运行，禁止修改图形设置。请先退出游戏。");
        }
    }
}

public interface IFirstLightSettingsStore
{
    Dictionary<string, string> Read();
    void Write(IReadOnlyDictionary<string, string> values);
}

[SupportedOSPlatform("windows")]
public sealed class RegistryFirstLightSettingsStore : IFirstLightSettingsStore
{
    public const string KeyPath = @"Software\IO Interactive\007 First Light";

    public Dictionary<string, string> Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath)
            ?? throw new InvalidOperationException("尚未发现 007 图形设置。请先启动游戏并保存一次图形设置。");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in FirstLightSettingsCatalog.Definitions)
        {
            var value = key.GetValue(definition.Key);
            if (value is int number && key.GetValueKind(definition.Key) == RegistryValueKind.DWord)
            {
                result[definition.Key] = unchecked((uint)number).ToString();
            }
        }
        return result;
    }

    public void Write(IReadOnlyDictionary<string, string> values)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("007 图形设置注册表项不可写。");
        foreach (var pair in values)
        {
            var number = unchecked((int)uint.Parse(pair.Value));
            key.SetValue(pair.Key, number, RegistryValueKind.DWord);
        }
    }
}
