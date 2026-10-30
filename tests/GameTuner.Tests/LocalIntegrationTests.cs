using GameTuner.Core.Adapters.Rdr2;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;

namespace GameTuner.Tests;

public sealed class LocalIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_nvidia_mcp_reports_first_light_presets()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GAMETUNER_RUN_REAL_MCP"), "1", StringComparison.Ordinal))
            return; // Opt-in machine integration; unit tests must not depend on a running desktop bridge.
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation",
            "NVIDIA App",
            "McpServer",
            "NvAppMcpServer.exe");
        Assert.True(File.Exists(executable), "NVIDIA App MCP bridge not installed.");

        IReadOnlyList<NvidiaApplication>? apps = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await using var client = new McpClient(executable);
                var nvidia = new NvidiaAppService(client);
                apps = await nvidia.GetApplicationsAsync("007 First Light");
                break;
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                // NVIDIA App's bridge can briefly reject a new client while
                // another short-lived client is closing its named pipe.
                await Task.Delay(750);
            }
        }

        var game = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<NvidiaApplication>>(apps));

        Assert.True(game.OptimizationSupported);
        Assert.NotEmpty(game.AvailablePresets);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_rdr2_config_is_parseable_without_writing()
    {
        var paths = new AppPaths();
        if (!File.Exists(paths.Rdr2ConfigFile))
        {
            return;
        }

        var adapter = new Rdr2ConfigAdapter(paths, new NotRunningGuard());
        var snapshot = await adapter.ReadAsync();

        Assert.Contains("shadowQuality", snapshot.Settings.Keys);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
    }

    private sealed class NotRunningGuard : IProcessGuard
    {
        public bool IsRdr2Running() => false;
        public bool IsRdr2GameRunning() => false;
        public Task WaitForRdr2StartAsync(IProgress<TimeSpan>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> WaitForRdr2ExitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
