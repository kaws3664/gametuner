using System.Windows;
using GameTuner.App.ViewModels;
using GameTuner.Core.Analysis;
using GameTuner.Core.Infrastructure;

namespace GameTuner.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var paths = new AppPaths();
            var viewModel = new MainViewModel(new LogAnalysisService(), new JsonFileStore(), paths);
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
            await viewModel.InitializeAsync();
            AppLog.Write("Read-only log analysis window ready.");
        }
        catch (Exception exception)
        {
            AppLog.Write("Startup failed: " + exception);
            MessageBox.Show("启动失败：" + exception.Message, "FrameSmith", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
