using GameTuner.App.Web;

namespace GameTuner.App.Desktop;

public interface ICaptureService : IDisposable
{
    event EventHandler? StateChanged;
    void Start(int processId, string game);
    Task<string?> StopForAnalysisAsync();
    LiveTelemetryState Snapshot();
}
