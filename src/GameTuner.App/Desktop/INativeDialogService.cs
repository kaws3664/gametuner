using GameTuner.App.Processes;

namespace GameTuner.App.Desktop;

public interface INativeDialogService
{
    GameProcessInfo? PickProcess();
    string? OpenLog();
    string? OpenSettings();
    string? OpenPlan(string initialDirectory);
    string? SavePng();
    bool ConfirmNewComparison();
}
