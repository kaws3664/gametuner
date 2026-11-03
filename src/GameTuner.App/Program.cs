using System.IO;
using GameTuner.App.Desktop;
using GameTuner.App.ViewModels;
using GameTuner.App.Web;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;

namespace GameTuner.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var paths = new AppPaths(Option(args, "--data-root"));
            paths.EnsureCreated();
            var app = new App();
            app.InitializeComponent();
            app.ApplyTheme(paths.SettingsFile);
            var model = new MainViewModel(new LogAnalysisService(), new JsonFileStore(), paths);
            var session = new DesktopSessionController(model,
                new WindowsLocalConfigurationProvider(paths), new LiveGameTelemetryMonitor(paths),
                new Processes.GameProcessService());
            var window = new MainWindow(session);
            app.MainWindow = window;
            window.Loaded += async (_, _) =>
            {
                try { await session.InitializeAsync(); }
                catch (Exception exception)
                {
                    session.OperationStatus = "操作未完成：" + exception.Message;
                    AppLog.Write("Initialization failed: " + exception);
                }
                // Record what startup resolved so a mis-detection is diagnosable from the
                // log instead of only being visible on screen.
                AppLog.Write($"Startup ready. {session.ProcessStatus} | {session.CaptureStatus}");
            };
            AppLog.Write("Native desktop workspace ready.");
            return app.Run(window);
        }
        catch (Exception exception)
        {
            AppLog.Write("Startup failed: " + exception);
            System.Windows.MessageBox.Show("启动失败：" + exception.Message, "GameTuner",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

}
