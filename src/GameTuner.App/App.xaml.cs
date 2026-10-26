using System.Drawing;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using GameTuner.App.ViewModels;
using GameTuner.Core.Adapters.FirstLight;
using GameTuner.Core.Adapters.Rdr2;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Mcp;
using GameTuner.Core.Models;
using GameTuner.Core.Optimization;
using GameTuner.Core.Profiles;
using GameTuner.Core.Telemetry;

namespace GameTuner.App;

public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;
    private McpClient? _mcpClient;
    private HttpClient? _httpClient;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Write("OnStartup begin");
        try
        {
            var paths = new AppPaths();
            paths.EnsureCreated();
            AppLog.Write("Paths ready");
            var files = new JsonFileStore();
            var settingsStore = new SettingsStore(paths, files);
            var settings = await settingsStore.LoadAsync();
            AppLog.Write("Settings loaded");
            var credentials = new WindowsCredentialStore();
            var processGuard = new GameProcessGuard();
            var engine = new TuningEngine();
            var sessionStore = new SessionStore(paths, files);

            _mcpClient = new McpClient();
            var nvidia = new NvidiaAppService(_mcpClient);
            var ruleAdvisor = new RuleBasedAiAdvisor();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
            var aiAdvisor = new OpenAiCompatibleAdvisor(
                _httpClient,
                settings,
                credentials,
                ruleAdvisor);
            var viewModel = new MainViewModel(
                profile => BuildController(
                    profile,
                    nvidia,
                    processGuard,
                    engine,
                    aiAdvisor,
                    sessionStore,
                    settings,
                    paths),
                settingsStore,
                credentials,
                settings,
                processGuard,
                nvidia,
                sessionStore);

            var window = new MainWindow(viewModel);
            AppLog.Write("MainWindow constructed");
            MainWindow = window;
            ConfigureTrayIcon(window);
            viewModel.NotificationRequested += (_, message) =>
            {
                if (_trayIcon is null)
                {
                    return;
                }

                _trayIcon.BalloonTipTitle = "FrameSmith";
                _trayIcon.BalloonTipText = message;
                _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                _trayIcon.ShowBalloonTip(5000);
            };

            window.Show();
            AppLog.Write("MainWindow shown");
            await viewModel.InitializeAsync();
            AppLog.Write("ViewModel initialized");
        }
        catch (Exception exception)
        {
            AppLog.Write("Startup failed: " + exception);
            System.Windows.MessageBox.Show(
                "启动失败：" + exception.Message,
                "FrameSmith",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 组合根：把一份 GameProfile 解析为具体的适配器、遥测收集器与调优控制器。
    /// 新增游戏时在这里注册实现即可，界面与引擎层不感知具体游戏。
    /// </summary>
    private static ITuningSessionController BuildController(
        GameProfile profile,
        INvidiaAppService nvidia,
        IGameProcessGuard processGuard,
        TuningEngine engine,
        IAiAdvisor aiAdvisor,
        SessionStore sessionStore,
        AppSettings settings,
        AppPaths paths)
    {
        return profile.TelemetrySource switch
        {
            TelemetrySource.NvidiaLogCsv => new GameTuningSessionController(
                nvidia,
                new FirstLightProfileAdapter(paths, new FirstLightProcessGuard()),
                new NvidiaLogTelemetryCollector(new FirstLightProcessGuard()),
                processGuard,
                engine,
                aiAdvisor,
                sessionStore,
                settings,
                paths),
            TelemetrySource.GameBenchmark => new GameTuningSessionController(
                nvidia,
                new Rdr2ConfigAdapter(paths, new Rdr2ProcessGuard()),
                new Rdr2TelemetryCollector(),
                processGuard,
                engine,
                aiAdvisor,
                sessionStore,
                settings,
                paths),
            _ => throw new NotSupportedException($"未注册 {profile.DisplayName} 的适配器。")
        };
    }

    private void ConfigureTrayIcon(MainWindow window)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 FrameSmith", null, (_, _) => ShowWindow(window));
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "FrameSmith - 游戏画质调优",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow(window);
    }

    private static void ShowWindow(MainWindow window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void ExitApplication()
    {
        _exiting = true;
        if (MainWindow is MainWindow window)
        {
            window.AllowClose();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        _trayIcon?.Dispose();
        _httpClient?.Dispose();
        _mcpClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
