using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTuner.App;
using GameTuner.App.ViewModels;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.UiTests;

public sealed class WorkspaceTests(WpfHost host) : IClassFixture<WpfHost>
{
    [Fact]
    public Task Empty_workspace_opens_without_fake_metrics_or_game_automation_actions() => host.Run(() =>
    {
        using var workspace = new Workspace();
        Assert.Equal("--", workspace.Control<TextBlock>("AverageMetric").Text);
        Assert.Equal("--", workspace.Control<TextBlock>("LowMetric").Text);
        Assert.True(workspace.Control<Border>("EmptyRounds").IsVisible);
        Assert.False(workspace.Control<Button>("AnalyzeButton").IsEnabled);
        Assert.False(workspace.Control<Button>("SaveButton").IsEnabled);
        Assert.False(workspace.Control<Button>("ShareButton").IsEnabled);
        Assert.Empty(VisualDescendants<ComboBox>(workspace.Window));
        var labels = VisualDescendants<Button>(workspace.Window).Select(button => button.Content?.ToString() ?? "");
        Assert.DoesNotContain(labels, label => label.Contains("\u5e94\u7528") || label.Contains("\u9000\u51fa\u6e38\u620f"));
        workspace.Screenshot("offline-01-empty");
    });

    [Fact]
    public Task Player_can_name_any_game_and_use_direct_target_and_tier_buttons() => host.Run(() =>
    {
        using var workspace = new Workspace();
        workspace.Control<TextBox>("GameInput").Text = "Any Game / Not in NVIDIA optimization catalog";
        workspace.Flush();
        Assert.Equal("Any Game / Not in NVIDIA optimization catalog", workspace.Control<TextBlock>("GameHeading").Text);
        workspace.Invoke("Fps120Button");
        workspace.Flush();
        Assert.Equal("120", workspace.Control<TextBox>("TargetInput").Text);
        Assert.Contains("108", workspace.Control<TextBlock>("GoalThreshold").Text);
        workspace.Select("HighTierChoice");
        workspace.Select("QualityChoice");
        Assert.Equal(ManualQualityTier.High, workspace.ViewModel.CurrentTier);
        Assert.Equal(TuningMode.Quality, workspace.ViewModel.Mode);
        workspace.Screenshot("offline-02-direct-buttons");
    });

    [Fact]
    public Task Actual_import_button_reads_file_and_shows_concrete_manual_advice() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.Select("HighTierChoice");
        var source = workspace.Log(1, 40, 30);
        var before = File.ReadAllBytes(source);
        workspace.Control<TextBox>("LogPathInput").Text = source;
        workspace.Invoke("AnalyzeButton");
        await workspace.WaitFor(() => workspace.ViewModel.Rounds.Count == 1 && !workspace.ViewModel.IsBusy);
        Assert.Equal("40.0", workspace.Control<TextBlock>("AverageMetric").Text);
        Assert.Equal("30.0", workspace.Control<TextBlock>("LowMetric").Text);
        Assert.False(workspace.Control<Border>("EmptyRounds").IsVisible);
        Assert.Contains("\u4e2d\u6863", workspace.Control<TextBlock>("RecommendationTitle").Text);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(workspace.Paths.Root, "manual-analysis.json")));
        Assert.False(workspace.Control<TextBox>("GameInput").IsEnabled);
        workspace.Screenshot("offline-03-first-import-test-data");
    });

    [Fact]
    public Task Fifth_and_sixth_log_still_work_and_each_round_keeps_its_actual_configuration() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.Game = "An arbitrary offline game";
        for (var index = 1; index <= 6; index++)
        {
            workspace.ViewModel.CurrentTier = index == 1 ? ManualQualityTier.Ultra : ManualQualityTier.Medium;
            workspace.ViewModel.GraphicsSettings = $"Recorded configuration {index}";
            workspace.ViewModel.LogPath = workspace.Log(index, 30 + index, 20 + index);
            await workspace.ViewModel.AnalyzeAsync();
        }
        workspace.Flush();
        Assert.Equal(6, workspace.ViewModel.Rounds.Count);
        Assert.True(workspace.Control<Button>("AnalyzeButton").IsEnabled);
        Assert.Contains("7", workspace.ViewModel.ImportLabel);
        Assert.Equal("Recorded configuration 1", workspace.ViewModel.Rounds[0].Round.Configuration.GraphicsSettings);
        Assert.Equal(ManualQualityTier.Ultra, workspace.ViewModel.Rounds[0].Round.Configuration.Tier);
        Assert.Equal("+1.0 FPS", workspace.ViewModel.Rounds[5].Delta);
        Assert.Equal("+1.0 FPS", workspace.ViewModel.Rounds[5].LowDelta);
        var roundButtons = VisualDescendants<Button>(workspace.Control<ItemsControl>("RoundList")).ToArray();
        Assert.Equal(6, roundButtons.Length);
        Invoke(roundButtons[0]);
        workspace.Flush();
        Assert.Equal(1, workspace.ViewModel.SelectedRound!.Number);
        Assert.Equal("31.0", workspace.Control<TextBlock>("AverageMetric").Text);
        Assert.Equal("Recorded configuration 1", workspace.ViewModel.ShareSettings);
        workspace.Control<ScrollViewer>("OverviewScroll").ScrollToVerticalOffset(450);
        workspace.Flush();
        workspace.Screenshot("offline-04-six-rounds-test-data");
    });

    [Fact]
    public Task Meeting_both_targets_stops_imports_and_save_reload_keeps_the_measured_settings() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.Game = "My arbitrary game";
        workspace.ViewModel.CurrentTier = ManualQualityTier.High;
        workspace.ViewModel.LogPath = workspace.Log(1, 40, 30);
        await workspace.ViewModel.AnalyzeAsync();
        workspace.ViewModel.CurrentTier = ManualQualityTier.Medium;
        workspace.ViewModel.Gpu = "TEST GPU / player entered";
        workspace.ViewModel.Cpu = "TEST CPU / player entered";
        workspace.ViewModel.Memory = "32 GB RAM / 12 GB VRAM";
        workspace.ViewModel.Resolution = "2560 x 1440 / Quality upscaling";
        workspace.ViewModel.GraphicsSettings = "Textures: High\nShadows: Medium\nVolumetrics: Low\nRay tracing: Off";
        workspace.ViewModel.Scene = "Same benchmark route / test data";
        workspace.ViewModel.LogPath = workspace.Log(2, 65, 58);
        await workspace.ViewModel.AnalyzeAsync();
        workspace.Flush();
        Assert.True(workspace.ViewModel.HasReachedTarget);
        Assert.False(workspace.Control<Button>("AnalyzeButton").IsEnabled);
        Assert.False(workspace.Control<Button>("LatestLogButton").IsEnabled);
        Assert.Equal("65.0", workspace.ViewModel.AverageFps);
        Assert.Equal(ManualQualityTier.Medium, workspace.ViewModel.SelectedRound!.Round.Report.Recommendation.SuggestedTier);
        workspace.Invoke("SaveButton");
        await workspace.WaitFor(() => workspace.ViewModel.SaveStatus.Length > 0 && !workspace.ViewModel.IsBusy);
        var restored = workspace.CreateViewModel();
        await restored.InitializeAsync();
        Assert.Equal("My arbitrary game", restored.Game);
        Assert.Equal(2, restored.Rounds.Count);
        Assert.True(restored.HasReachedTarget);
        Assert.Equal("TEST GPU / player entered", restored.ShareGpu);
        Assert.Equal(workspace.ViewModel.ShareSettings, restored.ShareSettings);
        Assert.Equal("65.0", restored.AverageFps);
        Assert.Single(Directory.GetFiles(workspace.ViewModel.PlansDirectory, "*.json"));
        workspace.Invoke("ShareButton");
        workspace.Flush();
        Assert.True(workspace.Control<ShareCard>("SharePreview").IsVisible);
        workspace.Screenshot("offline-05-share-preview-test-data");
        var png = Path.Combine(workspace.Paths.Root, "share.png");
        ShareCard.ExportPng(workspace.ViewModel, png);
        var image = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(1760, image.PixelWidth);
        Assert.True(image.PixelHeight > 1400);
        Assert.True(new FileInfo(png).Length > 10000);
        CopyArtifact(png, "offline-06-share-card-test-data.png");
    });

    [Fact]
    public Task Average_only_or_p99_summary_is_not_enough_to_declare_target_met() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        var source = Path.Combine(workspace.Paths.Logs, "Benchmark-test.txt");
        await File.WriteAllTextAsync(source, "Frames Per Second\nPass 0, 50.00, 100.00, 90.00\n99%, 12.00\n");
        workspace.ViewModel.LogPath = source;
        await workspace.ViewModel.AnalyzeAsync();
        workspace.Flush();
        Assert.False(workspace.ViewModel.HasReachedTarget);
        Assert.Equal("90.0", workspace.ViewModel.AverageFps);
        Assert.Contains("\u5f85\u786e\u8ba4", workspace.ViewModel.ShareVerdict);
        Assert.True(workspace.Control<Button>("AnalyzeButton").IsEnabled);
    });

    [Fact]
    public Task Invalid_or_duplicate_content_does_not_add_fake_rounds() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.LogPath = workspace.Log(1, 40, 30);
        await workspace.ViewModel.AnalyzeAsync();
        var duplicate = Path.Combine(workspace.Paths.Logs, "renamed-copy.csv");
        File.Copy(workspace.ViewModel.LogPath, duplicate);
        workspace.ViewModel.LogPath = duplicate;
        await workspace.ViewModel.AnalyzeAsync();
        Assert.Single(workspace.ViewModel.Rounds);
        Assert.Contains("\u5df2\u5bfc\u5165", workspace.ViewModel.Status);
        var bad = Path.Combine(workspace.Paths.Logs, "invalid.csv");
        File.WriteAllText(bad, "not a performance log");
        workspace.ViewModel.LogPath = bad;
        await workspace.ViewModel.AnalyzeAsync();
        Assert.Single(workspace.ViewModel.Rounds);
        Assert.Contains("\u6ca1\u6709\u65b0\u589e", workspace.ViewModel.Status);
        Assert.Equal("40.0", workspace.ViewModel.AverageFps);
    });

    [Fact]
    public Task Editing_next_round_inputs_does_not_silently_change_the_saved_measurement() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.GraphicsSettings = "Original measured settings";
        workspace.ViewModel.LogPath = workspace.Log(1, 40, 30);
        await workspace.ViewModel.AnalyzeAsync();
        workspace.ViewModel.GraphicsSettings = "Next round proposed settings";
        Assert.Equal("Original measured settings", workspace.ViewModel.ShareSettings);
        workspace.Invoke("UpdateDetailsButton");
        workspace.Flush();
        Assert.Equal("Next round proposed settings", workspace.ViewModel.ShareSettings);
        Assert.Equal("40.0", workspace.ViewModel.AverageFps);
    });

    [Fact]
    public Task New_goal_preserves_archived_plan_and_can_measure_a_different_game() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.LogPath = workspace.Log(1, 40, 30);
        await workspace.ViewModel.AnalyzeAsync();
        await workspace.ViewModel.SaveAsync();
        workspace.ViewModel.NewGoal();
        workspace.ViewModel.Game = "Another arbitrary game";
        workspace.ViewModel.TargetFps = 90;
        workspace.ViewModel.LogPath = workspace.Log(2, 45, 35);
        await workspace.ViewModel.AnalyzeAsync();
        await workspace.ViewModel.SaveAsync();
        Assert.Equal(2, Directory.GetFiles(workspace.ViewModel.PlansDirectory, "*.json").Length);
        Assert.Single(workspace.ViewModel.Rounds);
        Assert.Equal("Another arbitrary game", workspace.ViewModel.Game);
        var oldPlan = Directory.GetFiles(workspace.ViewModel.PlansDirectory, "*.json")
            .Select(path => (path, content: File.ReadAllText(path)))
            .Single(plan => !plan.content.Contains("Another arbitrary game")).path;
        await workspace.ViewModel.LoadPlanAsync(oldPlan);
        Assert.Equal("\u6211\u7684\u6e38\u620f", workspace.ViewModel.Game);
        Assert.Equal("40.0", workspace.ViewModel.AverageFps);
    });

    [Fact]
    public Task Unknown_hardware_is_labeled_unknown_in_share_card_and_never_guessed() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.LogPath = workspace.Log(1, 40, 30);
        await workspace.ViewModel.AnalyzeAsync();
        workspace.Invoke("ShareButton");
        workspace.Flush();
        var card = workspace.Control<ShareCard>("SharePreview");
        Assert.Equal("\u672a\u586b\u5199", ((TextBlock)card.FindName("ShareGpuText")).Text);
        Assert.DoesNotContain("RTX", workspace.ViewModel.ShareGpu);
        Assert.Equal(workspace.ViewModel.ShareSettings, ((TextBlock)card.FindName("ShareSettingsText")).Text);
    });

    [Theory]
    [InlineData(960, 720, 2)]
    [InlineData(1280, 900, 4)]
    [InlineData(1600, 1000, 4)]
    public Task Metrics_and_share_buttons_fit_supported_window_sizes(double width, double height, int columns) => host.Run(() =>
    {
        using var workspace = new Workspace(width, height);
        var metrics = workspace.Control<UniformGrid>("MetricsGrid");
        Assert.Equal(columns, metrics.Columns);
        foreach (var text in VisualDescendants<TextBlock>(metrics))
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(text.DesiredSize.Width <= text.ActualWidth + 1,
                $"Metric '{text.Text}' is clipped at {width}: desired {text.DesiredSize.Width}, actual {text.ActualWidth}.");
        }
        workspace.Screenshot($"offline-07-layout-{width:0}x{height:0}");
    });

    [Fact]
    public Task Slider_and_target_input_stay_in_sync() => host.Run(() =>
    {
        using var workspace = new Workspace();
        var peer = new SliderAutomationPeer(workspace.Control<Slider>("TargetSlider"));
        ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)).SetValue(90);
        workspace.Flush();
        Assert.Equal("90", workspace.Control<TextBox>("TargetInput").Text);
        workspace.Control<TextBox>("TargetInput").Text = "120";
        workspace.Flush();
        Assert.Equal(120, workspace.Control<Slider>("TargetSlider").Value);
    });

    [Fact]
    public Task Different_log_precision_is_not_claimed_as_a_verified_performance_improvement() => host.Run(async () =>
    {
        using var workspace = new Workspace();
        workspace.ViewModel.LogPath = workspace.Log(1, 40, 30);
        await workspace.ViewModel.AnalyzeAsync();
        var frames = Path.Combine(workspace.Paths.Logs, "frame.csv");
        File.WriteAllText(frames, "MsBetweenPresents\n" + string.Join('\n', Enumerable.Repeat("25", 100)));
        workspace.ViewModel.LogPath = frames;
        await workspace.ViewModel.AnalyzeAsync();
        Assert.Contains("\u53e3\u5f84\u4e0d\u540c", workspace.ViewModel.DeltaFps);
        Assert.Equal("\u4e0d\u53ef\u76f4\u63a5\u6bd4\u8f83", workspace.ViewModel.LowDelta);
    });

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static void CopyArtifact(string source, string name)
    {
        var directory = Environment.GetEnvironmentVariable("GAMETUNER_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(directory)) File.Copy(source, Path.Combine(directory, name), overwrite: true);
    }
}

public sealed class WpfHost : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BindingErrors _errors = new();

    public WpfHost()
    {
        _thread = new Thread(() =>
        {
            try
            {
                var app = new GameTuner.App.App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                PresentationTraceSources.DataBindingSource.Listeners.Add(_errors);
                _ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { _ready.TrySetException(exception); }
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task Run(Action test) => Run(() => { test(); return Task.CompletedTask; });
    public async Task Run(Func<Task> test)
    {
        var dispatcher = await _ready.Task;
        await dispatcher.InvokeAsync(async () =>
        {
            _errors.Messages.Clear();
            await test();
            Assert.True(_errors.Messages.Count == 0, string.Join(Environment.NewLine, _errors.Messages));
        }).Task.Unwrap();
    }

    public void Dispose()
    {
        _ready.Task.GetAwaiter().GetResult().BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (message is not null) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}

internal sealed class Workspace : IDisposable
{
    public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "FrameSmith-UiTests", Guid.NewGuid().ToString("N")));
    public MainViewModel ViewModel { get; }
    public MainWindow Window { get; }
    public Workspace(double width = 1280, double height = 900)
    {
        Paths.EnsureCreated();
        ViewModel = CreateViewModel();
        Window = new MainWindow(ViewModel)
        {
            Width = width, Height = height, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        Window.Show();
        Flush();
    }

    public MainViewModel CreateViewModel() => new(new LogAnalysisService(), new JsonFileStore(), Paths);
    public T Control<T>(string name) where T : FrameworkElement => Assert.IsType<T>(Window.FindName(name));
    public void Invoke(string name) =>
        ((IInvokeProvider)new ButtonAutomationPeer(Control<Button>(name)).GetPattern(PatternInterface.Invoke)).Invoke();
    public void Select(string name)
    {
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(Control<RadioButton>(name)).GetPattern(PatternInterface.SelectionItem)).Select();
        Flush();
    }

    public string Log(int number, double fps, double low)
    {
        var path = Path.Combine(Paths.Logs, $"performance-{number}.csv");
        File.WriteAllText(path, FormattableString.Invariant(
            $"Timestamp (Elapsed time in seconds),PID,FPS,FPS 1(%) Low,CPU Utilization(%),GPU1 Utilization(%)\n0,77,{fps},{low},55,97\n300,77,{fps},{low},55,97\n"));
        return path;
    }

    public async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), ViewModel.Status);
        Flush();
    }
    public void Flush()
    {
        Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
    }
    public void Screenshot(string name)
    {
        var directory = Environment.GetEnvironmentVariable("GAMETUNER_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        Flush();
        var content = (FrameworkElement)Window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right),
            (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
    public void Dispose()
    {
        Window.Close();
        Directory.Delete(Paths.Root, recursive: true);
    }
}
