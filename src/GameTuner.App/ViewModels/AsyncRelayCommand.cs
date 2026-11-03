using System.Windows.Input;
using System.Windows.Threading;

namespace GameTuner.App.ViewModels;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handlers = CanExecuteChanged;
        if (handlers is null) return;
        if (_dispatcher.CheckAccess()) handlers(this, EventArgs.Empty);
        else _dispatcher.Invoke(() => handlers(this, EventArgs.Empty));
    }
}
