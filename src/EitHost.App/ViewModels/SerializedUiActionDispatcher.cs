using System.Windows.Threading;

namespace EitHost.App.ViewModels;

internal sealed class SerializedUiActionDispatcher(
    SynchronizationContext? synchronizationContext,
    Dispatcher? initialDispatcher,
    Func<Dispatcher?> dispatcherProvider)
{
    private readonly object gate = new();
    private Dispatcher? dispatcher = initialDispatcher;

    internal Dispatcher? Dispatcher => dispatcher ??= dispatcherProvider();

    internal void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Monitor.IsEntered(gate))
        {
            action();
            return;
        }

        var currentDispatcher = Dispatcher;
        if (currentDispatcher is not null)
        {
            if (currentDispatcher.CheckAccess())
            {
                Run(action);
            }
            else
            {
                currentDispatcher.BeginInvoke(() => Run(action), DispatcherPriority.Normal);
            }

            return;
        }

        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(
                static state =>
                {
                    var (owner, callback) = ((SerializedUiActionDispatcher, Action))state!;
                    owner.Run(callback);
                },
                (this, action));
            return;
        }

        Run(action);
    }

    internal Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Monitor.IsEntered(gate))
        {
            return RunAsync(action);
        }

        var currentDispatcher = Dispatcher;
        if (currentDispatcher is not null)
        {
            if (currentDispatcher.CheckAccess())
            {
                return RunAsync(action);
            }

            return currentDispatcher.InvokeAsync(() => Run(action), DispatcherPriority.Normal).Task;
        }

        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                synchronizationContext.Post(
                    static state =>
                    {
                        var (owner, callback, signal) =
                            ((SerializedUiActionDispatcher, Action, TaskCompletionSource<object?>))state!;
                        try
                        {
                            owner.Run(callback);
                            signal.TrySetResult(null);
                        }
                        catch (Exception ex)
                        {
                            signal.TrySetException(ex);
                        }
                    },
                    (this, action, completion));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            return completion.Task;
        }

        return RunAsync(action);
    }

    private Task RunAsync(Action action)
    {
        try
        {
            Run(action);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private void Run(Action action)
    {
        // UI callbacks must stay short and must not synchronously wait for work that posts back here.
        // Holding this reentrant gate for the callback mirrors one Dispatcher lane in tests whose
        // fallback SynchronizationContext may otherwise execute Post callbacks concurrently.
        lock (gate)
        {
            action();
        }
    }
}
