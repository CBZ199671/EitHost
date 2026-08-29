namespace EitHost.Core.Concurrency;

public sealed class LatestOnlyAsyncWorker<T> : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly Func<T, CancellationToken, ValueTask> handler;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<T, bool>? isNonReplaceable;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task processingTask;
    private T? pending;
    private T? deferred;
    private bool hasPending;
    private bool hasDeferred;
    private bool pendingIsNonReplaceable;
    private bool signalOutstanding;
    private bool completionRequested;
    private long submittedCount;
    private long processedCount;
    private long replacedCount;

    public LatestOnlyAsyncWorker(
        Func<T, CancellationToken, ValueTask> handler,
        Action<Exception>? errorHandler = null,
        Func<T, bool>? isNonReplaceable = null)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.errorHandler = errorHandler;
        this.isNonReplaceable = isNonReplaceable;
        processingTask = Task.Run(ProcessAsync);
    }

    public long SubmittedCount => Interlocked.Read(ref submittedCount);

    public long ProcessedCount => Interlocked.Read(ref processedCount);

    public long ReplacedCount => Interlocked.Read(ref replacedCount);

    public bool TryPost(T item)
    {
        var nonReplaceable = isNonReplaceable?.Invoke(item) == true;
        lock (gate)
        {
            if (completionRequested)
            {
                return false;
            }

            Interlocked.Increment(ref submittedCount);
            if (hasPending && pendingIsNonReplaceable && !nonReplaceable)
            {
                if (hasDeferred)
                {
                    Interlocked.Increment(ref replacedCount);
                }

                deferred = item;
                hasDeferred = true;
                return true;
            }

            if (hasPending)
            {
                Interlocked.Increment(ref replacedCount);
            }

            if (nonReplaceable && hasDeferred)
            {
                Interlocked.Increment(ref replacedCount);
                deferred = default;
                hasDeferred = false;
            }

            pending = item;
            hasPending = true;
            pendingIsNonReplaceable = nonReplaceable;
            if (!signalOutstanding)
            {
                signalOutstanding = true;
                signal.Release();
            }

            return true;
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            completionRequested = true;
            pending = default;
            deferred = default;
            hasPending = false;
            hasDeferred = false;
            pendingIsNonReplaceable = false;
            cancellation.Cancel();
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!completionRequested)
            {
                completionRequested = true;
                if (!signalOutstanding)
                {
                    signalOutstanding = true;
                    signal.Release();
                }
            }
        }

        await processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            cancellation.Cancel();
            cancellation.Dispose();
            signal.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(cancellation.Token).ConfigureAwait(false);
                T? item;
                lock (gate)
                {
                    signalOutstanding = false;
                    if (!hasPending)
                    {
                        if (completionRequested)
                        {
                            return;
                        }

                        continue;
                    }

                    item = pending;
                    var promoteDeferred = pendingIsNonReplaceable && hasDeferred;
                    pending = promoteDeferred ? deferred : default;
                    hasPending = promoteDeferred;
                    pendingIsNonReplaceable = false;
                    deferred = default;
                    hasDeferred = false;
                }

                try
                {
                    await handler(item!, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    errorHandler?.Invoke(ex);
                }
                finally
                {
                    Interlocked.Increment(ref processedCount);
                }

                lock (gate)
                {
                    if (completionRequested && !hasPending)
                    {
                        return;
                    }

                    if (hasPending && !signalOutstanding)
                    {
                        signalOutstanding = true;
                        signal.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Explicit cancellation is the bounded shutdown path.
        }
    }
}
