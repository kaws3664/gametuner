using System.Windows.Input;

namespace GameTuner.App.Desktop;

public sealed class NativeAsyncCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? errorHandler = null) : ICommand
{
    private bool _executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_executing && (canExecute?.Invoke() ?? true);
    public Task ExecuteAsync()
    {
        if (!CanExecute(null)) throw new InvalidOperationException("当前操作尚未完成。");
        _executing = true;
        return ExecuteCoreAsync();
    }
    public async void Execute(object? parameter)
    {
        try { await ExecuteAsync(); }
        catch (Exception exception) { errorHandler?.Invoke(exception); }
    }
    private async Task ExecuteCoreAsync()
    {
        try
        {
            RaiseCanExecuteChanged();
            await execute();
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
