using GameTuner.App.Desktop;
using GameTuner.App.Processes;
using System.IO;

namespace GameTuner.UiTests;

public sealed class DesktopStatusTests
{
    [Fact]
    public void Process_and_capture_status_are_observable_and_report_real_lifecycle()
    {
        var paths = new GameTuner.Core.Infrastructure.AppPaths(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GameTuner-StatusTests", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        try
        {
            var capture = new FakeCaptureService(paths);
            using var session = new DesktopSessionController(
                new GameTuner.App.ViewModels.MainViewModel(new GameTuner.Core.Analysis.LogAnalysisService(),
                    new GameTuner.Core.Infrastructure.JsonFileStore(), paths),
                new FakeConfigurationProvider(), capture);
            var changes = new List<string>();
            session.PropertyChanged += (_, args) => changes.Add(args.PropertyName ?? "");

            Assert.False(session.IsBusy);
            Assert.Contains("选择", session.ProcessStatus);
            session.SelectProcess(new GameProcessInfo(Environment.ProcessId, "testgame", "Test"));
            Assert.Contains("testgame", session.ProcessStatus);
            Assert.Contains(nameof(DesktopSessionController.ProcessStatus), changes);

            session.StartCapture();
            Assert.True(session.IsRecording);
            Assert.Contains("正在", session.CaptureStatus);
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true); }
    }
}
