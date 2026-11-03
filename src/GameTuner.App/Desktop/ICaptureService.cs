using GameTuner.App.Web;

namespace GameTuner.App.Desktop;

public interface ICaptureService : IDisposable
{
    void Start(int processId, string game);
    Task<string?> StopForAnalysisAsync();
    LiveTelemetryState Snapshot();
}
