using System.Collections.ObjectModel;
using GameTuner.App.ViewModels;

namespace GameTuner.App.Processes;

public sealed class ProcessPickerViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<GameProcessInfo>> _getProcesses;
    private IReadOnlyList<GameProcessInfo> _snapshot = [];
    private string _filter = "";
    private GameProcessInfo? _selectedProcess;

    public ProcessPickerViewModel(Func<IReadOnlyList<GameProcessInfo>> getProcesses)
    {
        _getProcesses = getProcesses;
        Refresh();
    }

    public ObservableCollection<GameProcessInfo> Processes { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) UpdateProcesses();
        }
    }

    public GameProcessInfo? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public void Refresh()
    {
        _snapshot = _getProcesses()
            .OrderByDescending(process => !string.IsNullOrEmpty(process.WindowTitle))
            .ThenBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        UpdateProcesses();
    }

    private void UpdateProcesses()
    {
        var selectedProcessId = _selectedProcess?.Id;
        Processes.Clear();
        var filter = _filter.Trim();
        foreach (var process in _snapshot)
        {
            if (filter.Length == 0
                || process.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || process.WindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Processes.Add(process);
            }
        }
        SelectedProcess = selectedProcessId is int id
            ? Processes.FirstOrDefault(process => process.Id == id)
            : null;
    }
}
