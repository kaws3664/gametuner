using GameTuner.Core.Adapters.Rdr2;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.Tests;

public sealed class Rdr2ConfigAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameTunerTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly AppPaths _paths;
    private readonly Rdr2ConfigAdapter _adapter;

    public Rdr2ConfigAdapterTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "system.xml");
        File.WriteAllText(_configPath, SampleXml);
        _paths = new AppPaths(Path.Combine(_root, "app"), _configPath);
        _paths.EnsureCreated();
        _adapter = new Rdr2ConfigAdapter(_paths, new NeverRunningGuard());
    }

    [Fact]
    public async Task ApplyOverrides_preserves_non_whitelisted_bytes_and_formatting()
    {
        var snapshot = await _adapter.ReadAsync();
        var original = snapshot.RawContent;

        await _adapter.ApplyOverridesAsync(
            new Dictionary<string, string>
            {
                ["shadowQuality"] = "kSettingLevel_High",
                ["anisotropicFiltering"] = "8"
            });

        var updated = await File.ReadAllTextAsync(_configPath);
        Assert.Contains("<shadowQuality>kSettingLevel_High</shadowQuality>", updated);
        Assert.Contains("<anisotropicFiltering value=\"8\" />", updated);
        Assert.Contains("<!-- keep this exact comment -->", updated);
        Assert.Contains("unknownNode value=\"untouched\"", updated);
        Assert.Equal(
            original.Replace("kSettingLevel_Ultra</shadowQuality>", "kSettingLevel_High</shadowQuality>")
                .Replace("value=\"4\"", "value=\"8\""),
            updated);
    }

    [Fact]
    public async Task ApplyOverrides_rejects_unknown_keys()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _adapter.ApplyOverridesAsync(
                new Dictionary<string, string> { ["unknownNode"] = "false" }));
    }

    [Fact]
    public async Task ApplyOverrides_rejects_out_of_range_values()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _adapter.ApplyOverridesAsync(
                new Dictionary<string, string> { ["anisotropicFiltering"] = "32" }));
    }

    [Fact]
    public async Task Restore_recovers_original_hash()
    {
        var original = await _adapter.ReadAsync();
        var backup = await _adapter.BackupAsync("test");
        await _adapter.ApplyOverridesAsync(
            new Dictionary<string, string> { ["shadowQuality"] = "kSettingLevel_Medium" });
        Assert.NotEqual(original.Hash, HashUtility.Sha256File(_configPath));

        await _adapter.RestoreAsync(backup);

        Assert.Equal(original.Hash, HashUtility.Sha256File(_configPath));
    }

    [Fact]
    public async Task ApplyOverrides_refuses_when_game_is_running()
    {
        var adapter = new Rdr2ConfigAdapter(_paths, new AlwaysRunningGuard());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ApplyOverridesAsync(
                new Dictionary<string, string> { ["shadowQuality"] = "kSettingLevel_High" }));
    }

    [Fact]
    public async Task FallbackCandidate_downgrades_expensive_settings_for_vram_pressure()
    {
        var snapshot = await _adapter.ReadAsync();
        var candidate = _adapter.CreateFallbackCandidate(
            snapshot.Settings,
            new PerformanceMetrics
            {
                AverageFps = 45,
                LowFps = 38,
                GpuUtilization = 99,
                GpuMemoryUsedMb = 7800,
                GpuMemoryTotalMb = 8192
            },
            new TuningGoal { TargetFps = 60 });

        Assert.NotEmpty(candidate.SettingOverrides);
        Assert.True(
            candidate.SettingOverrides.ContainsKey("textureQuality") ||
            candidate.SettingOverrides.ContainsKey("anisotropicFiltering"));
    }

    private const string SampleXml =
        """
        <?xml version="1.0" encoding="UTF-8" ?>
        <rage__fwuiSystemSettingsCollection>
            <version value="37" />
            <!-- keep this exact comment -->
            <graphics>
                <shadowQuality>kSettingLevel_Ultra</shadowQuality>
                <textureQuality>kSettingLevel_Ultra</textureQuality>
                <anisotropicFiltering value="4" />
            </graphics>
            <advancedGraphics>
                <unknownNode value="untouched" />
            </advancedGraphics>
        </rage__fwuiSystemSettingsCollection>
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NeverRunningGuard : IProcessGuard
    {
        public bool IsRdr2Running() => false;
        public bool IsRdr2GameRunning() => false;
        public Task WaitForRdr2StartAsync(IProgress<TimeSpan>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> WaitForRdr2ExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class AlwaysRunningGuard : IProcessGuard
    {
        public bool IsRdr2Running() => true;
        public bool IsRdr2GameRunning() => true;
        public Task WaitForRdr2StartAsync(IProgress<TimeSpan>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> WaitForRdr2ExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
