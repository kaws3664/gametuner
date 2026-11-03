using System.IO;
using System.Windows.Media.Imaging;
using GameTuner.App;
using GameTuner.App.Desktop;
using GameTuner.App.ViewModels;
using GameTuner.App.Web;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.UiTests;

public sealed class NativeWorkspaceTests
{
    [Fact]
    public void Empty_workspace_is_native_and_contains_no_measurements() => Sta.Run(() =>
    {
        using var workspace = new Workspace();
        var window = workspace.Window;

        Assert.False(workspace.Model.HasRounds);
        Assert.Equal("--", workspace.Model.AverageFps);
        Assert.Equal("--", workspace.Model.LowFps);
        Assert.Equal("GameTuner · 离线性能工作台", window.Title);
        Assert.NotNull(window.FindName("WorkspaceScroll"));
        Assert.Empty(workspace.Capture.StartedPaths);
    });

    [Fact]
    public async Task Selected_process_populates_hardware_resolution_and_read_only_game_settings()
    {
        var detected = new LocalConfigurationSnapshot
        {
            Cpu = "Detected CPU", Gpu = "Detected GPU", Memory = "32 GB RAM / 8 GB VRAM",
            Resolution = "2560 × 1440", GraphicsSettings = "纹理质量：高\n阴影质量：中",
            HasGameSettings = true, Source = "RDR2 system.xml", Status = "后台读取完成",
            DetectedGame = "Red Dead Redemption 2", GameProcessStatus = "已选择游戏进程：RDR2.exe"
        };
        using var workspace = new Workspace(detected);
        workspace.SelectProcess();
        workspace.Model.GraphicsSettings = "stale manual text";
        workspace.Model.Resolution = "manual resolution";
        await workspace.Import(workspace.Log(1, 40, 30));

        Assert.Equal("Detected CPU", workspace.Model.Cpu);
        Assert.Equal("Detected GPU", workspace.Model.Gpu);
        Assert.Equal("32 GB RAM / 8 GB VRAM", workspace.Model.Memory);
        Assert.Equal("2560 × 1440", workspace.Model.Resolution);
        Assert.Equal(detected.GraphicsSettings, workspace.Model.GraphicsSettings);
        Assert.True(workspace.Session.GraphicsReadOnly);
        Assert.Equal("后台读取完成", workspace.Session.ConfigurationStatus);
        Assert.Equal("2560 × 1440", workspace.Model.SelectedRound!.Round.Configuration.Resolution);
        Assert.Equal(detected.GraphicsSettings, workspace.Model.ShareSettings);
    }

    [Fact]
    public async Task Rdr2_reader_extracts_actual_settings_without_modifying_the_file()
    {
        using var workspace = new Workspace();
        var config = Path.Combine(workspace.Paths.Root, "system.xml");
        const string xml = "<rage__fwuiSystemSettingsCollection><graphics><textureQuality>kSettingLevel_High</textureQuality><shadowQuality>kSettingLevel_Medium</shadowQuality><screenWidth value=\"3440\" /><screenHeight value=\"1440\" /></graphics></rage__fwuiSystemSettingsCollection>";
        await File.WriteAllTextAsync(config, xml);
        var paths = new AppPaths(Path.Combine(workspace.Paths.Root, "data"), config);

        var result = await new WindowsLocalConfigurationProvider(paths).ReadAsync(null, "Red Dead Redemption 2", false, "");

        Assert.Equal("3440 × 1440", result.Resolution);
        Assert.Contains("纹理质量：高", result.GraphicsSettings);
        Assert.Contains("阴影质量：中", result.GraphicsSettings);
        Assert.True(result.HasGameSettings);
        Assert.Equal(xml, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task PresentMon_monitor_starts_and_stops_cleanly_without_launching_a_game()
    {
        using var workspace = new Workspace();
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon", "PresentMon.exe"),
            workspace.Paths.PresentMonExecutable);
        Assert.True(File.Exists(workspace.Paths.PresentMonExecutable), "发布包必须携带 PresentMon，不能使用玩家电脑上的 RTSS 或 NVIDIA App。");
        using var monitor = new LiveGameTelemetryMonitor(workspace.Paths);

        monitor.Start(Environment.ProcessId, "IntegrationTest");
        Assert.DoesNotContain("启动失败", monitor.Snapshot().Status);
        await Task.Delay(2500);
        var log = await monitor.StopForAnalysisAsync();

        Assert.False(monitor.Snapshot().IsRecording);
        Assert.Empty(monitor.Snapshot().LogPath);
        Assert.Null(await monitor.StopForAnalysisAsync());
        if (log is not null)
        {
            var header = File.ReadLines(log).First();
            Assert.Contains("MsBetweenPresents", header);
            Assert.Contains("CPU Utilization(%)", header);
            Assert.Contains("GPU Utilization(%)", header);
        }
    }

    [Fact]
    public async Task Native_import_reads_file_and_shows_concrete_manual_advice()
    {
        using var workspace = new Workspace();
        var source = workspace.Log(1, 40, 30);
        var before = await File.ReadAllBytesAsync(source);
        workspace.Model.CurrentTier = ManualQualityTier.High;

        await workspace.Import(source);

        Assert.Equal(40, double.Parse(workspace.Model.AverageFps));
        Assert.Equal(30, double.Parse(workspace.Model.LowFps));
        Assert.Single(workspace.Model.Rounds);
        Assert.Contains("中档", workspace.Model.Rounds[0].RecommendationTitle);
        Assert.False(workspace.Model.IsGoalEditable);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(Path.Combine(workspace.Paths.Root, "manual-analysis.json")));
    }

    [Fact]
    public async Task Sixth_log_and_round_selection_keep_their_actual_configuration()
    {
        using var workspace = new Workspace();
        for (var index = 1; index <= 6; index++)
        {
            workspace.Model.Game = "An arbitrary offline game";
            workspace.Model.CurrentTier = index == 1 ? ManualQualityTier.Ultra : ManualQualityTier.Medium;
            workspace.Model.GraphicsSettings = $"Recorded configuration {index}";
            await workspace.Import(workspace.Log(index, 30 + index, 20 + index));
        }

        Assert.Equal(6, workspace.Model.Rounds.Count);
        Assert.Contains("7", workspace.Model.ImportLabel);
        Assert.Equal("+1.0 FPS", workspace.Model.Rounds[^1].Delta);
        Assert.Equal("+1.0 FPS", workspace.Model.Rounds[^1].LowDelta);
        workspace.Model.SelectedRound = workspace.Model.Rounds[0];
        workspace.Model.FillSelectedInputs();
        Assert.Equal(1, workspace.Model.SelectedRound.Number);
        Assert.Equal("Recorded configuration 1", workspace.Model.ShareSettings);
        Assert.Equal("极高档", workspace.Model.Rounds[0].Tier);
    }

    [Fact]
    public async Task Meeting_targets_saves_reloads_and_exports_real_png() => await Sta.RunAsync(async () =>
    {
        using var workspace = new Workspace();
        await workspace.Import(workspace.Log(1, 40, 30));
        workspace.Model.Game = "My arbitrary game";
        workspace.Model.CurrentTier = ManualQualityTier.Medium;
        workspace.Model.Gpu = "TEST GPU / player entered";
        workspace.Model.Cpu = "TEST CPU / player entered";
        workspace.Model.Memory = "32 GB RAM / 12 GB VRAM";
        workspace.Model.Resolution = "2560 x 1440 / Quality upscaling";
        workspace.Model.GraphicsSettings = "Textures: High\nShadows: Medium\nVolumetrics: Low\nRay tracing: Off";
        workspace.Model.Scene = "Same benchmark route / test data";
        await workspace.Import(workspace.Log(2, 65, 58));

        Assert.True(workspace.Model.HasReachedTarget);
        Assert.False(workspace.Model.CanImport);
        await workspace.Model.SaveAsync();
        await workspace.Model.LoadPlanAsync(Directory.GetFiles(workspace.Model.PlansDirectory, "*.json").Single());
        Assert.Equal("TEST GPU / player entered", workspace.Model.ShareGpu);
        Assert.Equal(workspace.Model.GraphicsSettings, workspace.Model.ShareSettings);
        var png = Path.Combine(workspace.Paths.Root, "share.png");
        await workspace.Window.ExportCommand.ExecuteAsync();
        using (var stream = File.OpenRead(png))
        {
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(1760, image.PixelWidth);
            Assert.True(image.PixelHeight > 1400);
        }
        Assert.True(new FileInfo(png).Length > 10000);
        CopyArtifact(png, "native-share-card-test-data.png");
    });

    [Fact]
    public async Task Duplicate_and_invalid_log_paths_do_not_add_fake_rounds()
    {
        using var workspace = new Workspace();
        var source = workspace.Log(1, 40, 30);
        await workspace.Import(source);
        var renamed = Path.Combine(workspace.Paths.Logs, "renamed-copy.csv");
        File.Copy(source, renamed);
        await workspace.Import(renamed);

        Assert.Single(workspace.Model.Rounds);
        Assert.Contains("已导入", workspace.Model.Status);
        await workspace.Import("relative.csv");
        Assert.Single(workspace.Model.Rounds);
        Assert.Contains("没有新增记录", workspace.Model.Status);
        Assert.Contains("存在的性能日志文件", workspace.Model.Status);
    }

    [Fact]
    public async Task Updating_details_is_explicit_and_new_goal_preserves_archives()
    {
        using var workspace = new Workspace();
        workspace.Model.GraphicsSettings = "Original measured settings";
        await workspace.Import(workspace.Log(1, 40, 30));
        await workspace.Model.SaveAsync();
        workspace.Model.GraphicsSettings = "Next round proposed settings";

        Assert.Equal("Original measured settings", workspace.Model.ShareSettings);
        await workspace.Model.UpdateDetailsAsync();
        Assert.Equal("Next round proposed settings", workspace.Model.ShareSettings);
        await workspace.Model.SaveAsync();
        workspace.Model.NewGoal();
        Assert.Empty(workspace.Model.Rounds);
        workspace.Model.Game = "Another arbitrary game";
        workspace.Model.TargetFps = 90;
        await workspace.Import(workspace.Log(2, 45, 35));
        await workspace.Model.SaveAsync();
        Assert.Equal(2, Directory.GetFiles(workspace.Model.PlansDirectory, "*.json").Length);
    }

    [Fact]
    public async Task Updating_details_keeps_ray_tracing_recommendation_evidence()
    {
        using var workspace = new Workspace();
        workspace.Model.GraphicsSettings = "光线追踪：开启";
        await workspace.Import(workspace.Log(1, 40, 30));

        await workspace.Model.UpdateDetailsAsync();

        Assert.Contains(workspace.Model.Rounds.Single().Suggestions,
            item => item.Setting == "光线追踪" && item.SuggestedValue.Contains("关闭"));
    }

    [Fact]
    public async Task Latest_log_and_different_precision_use_existing_analysis_rules()
    {
        using var workspace = new Workspace();
        workspace.Model.LogDirectory = workspace.Paths.Logs;
        workspace.Log(1, 40, 30);

        await workspace.Model.LoadLatestAsync();

        Assert.Single(workspace.Model.Rounds);
        var frames = Path.Combine(workspace.Paths.Logs, "frame.csv");
        await File.WriteAllTextAsync(frames, "MsBetweenPresents\n" + string.Join('\n', Enumerable.Repeat("25", 100)));
        await workspace.Import(frames);
        Assert.Contains("口径不同", workspace.Model.DeltaFps);
        Assert.Equal("不可直接比较", workspace.Model.LowDelta);
    }

    private static void CopyArtifact(string source, string name)
    {
        var directory = Environment.GetEnvironmentVariable("GAMETUNER_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.Copy(source, Path.Combine(directory, name), true);
        }
    }
}

internal sealed class Workspace : IDisposable
{
    private MainWindow? _window;

    public Workspace(LocalConfigurationSnapshot? snapshot = null)
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "GameTuner-NativeWorkspaceTests", Guid.NewGuid().ToString("N")));
        Paths.EnsureCreated();
        Capture = new FakeCaptureService(Paths);
        Session = new DesktopSessionController(
            new MainViewModel(new LogAnalysisService(), new JsonFileStore(), Paths),
            new FakeConfigurationProvider { Snapshot = snapshot ?? new LocalConfigurationSnapshot() }, Capture);
    }

    public AppPaths Paths { get; }
    public FakeCaptureService Capture { get; }
    public DesktopSessionController Session { get; }
    public MainViewModel Model => Session.ViewModel;
    public MainWindow Window => _window ??= new MainWindow(Session, new TestDialogs(this));
    public string PngPath => Path.Combine(Paths.Root, "share.png");

    public void SelectProcess() => Session.SelectProcess(new(Environment.ProcessId, "testgame", "Native test process"));

    public Task Import(string path) => Session.ImportExternalLogAsync(path);

    public string Log(int number, double fps, double low)
    {
        var path = Path.Combine(Paths.Logs, $"performance-{number}.csv");
        File.WriteAllText(path, FormattableString.Invariant(
            $"Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)\n0,77,{fps},{low},55,97\n300,77,{fps},{low},55,97\n"));
        return path;
    }

    public void Dispose()
    {
        _window?.Close();
        Session.Dispose();
        if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, recursive: true);
    }

    private sealed class TestDialogs(Workspace workspace) : INativeDialogService
    {
        public GameTuner.App.Processes.GameProcessInfo? PickProcess() => null;
        public string? OpenLog() => null;
        public string? OpenSettings() => null;
        public string? OpenPlan(string initialDirectory) => null;
        public string? SavePng() => workspace.PngPath;
        public bool ConfirmNewComparison() => true;
    }
}
