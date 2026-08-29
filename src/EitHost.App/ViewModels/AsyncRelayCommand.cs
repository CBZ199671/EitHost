using System.Windows.Input;

namespace EitHost.App.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public Task? ExecutionTask { get; private set; }

    public bool CanExecute(object? parameter)
    {
        return !isExecuting && (canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter).ConfigureAwait(true);
    }

    public Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return Task.CompletedTask;
        }

        isExecuting = true;
        RaiseCanExecuteChanged();
        ExecutionTask = ExecuteCoreAsync();
        return ExecutionTask;
    }

    private async Task ExecuteCoreAsync()
    {
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
