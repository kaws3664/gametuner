using GameTuner.Core.Mcp;
using GameTuner.Core.Models;

namespace GameTuner.Tests;

public sealed class McpClientTests
{
    [Fact]
    public async Task Client_initializes_and_reads_structured_tool_result()
    {
        await using var client = new McpClient(FakeServer(), TimeSpan.FromSeconds(5));
        var nvidia = new NvidiaAppService(client);

        var apps = await nvidia.GetApplicationsAsync("Red Dead Redemption 2");

        Assert.Single(apps);
        Assert.True(apps[0].OptimizationSupported);
        Assert.Contains(QualityPreset.Quality, apps[0].AvailablePresets);
    }

    [Fact]
    public async Task Client_surfaces_tool_error_codes()
    {
        await using var client = new McpClient(
            FakeServer(),
            TimeSpan.FromSeconds(5),
            ["error"]);
        var nvidia = new NvidiaAppService(client);

        Assert.False(await nvidia.IsOverlayAvailableAsync());
    }

    [Fact]
    public async Task Client_times_out_when_server_never_responds()
    {
        await using var client = new McpClient(
            FakeServer(),
            TimeSpan.FromMilliseconds(300),
            ["silent"]);

        await Assert.ThrowsAnyAsync<TimeoutException>(
            () => client.EnsureInitializedAsync());
    }

    [Fact]
    public async Task Client_reports_server_exit()
    {
        await using var client = new McpClient(
            FakeServer(),
            TimeSpan.FromSeconds(5),
            ["exit"]);

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.EnsureInitializedAsync());
    }

    private static string FakeServer()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutput.Parent!.Name;
        var testsDirectory = testOutput.Parent!.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(
            testsDirectory,
            "GameTuner.FakeMcp",
            "bin",
            configuration,
            "net9.0",
            "GameTuner.FakeMcp.exe");
        Assert.True(File.Exists(path), $"Fake MCP server not found: {path}");
        return path;
    }
}
