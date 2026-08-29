using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace EitHost.Core.Application.Realtime;

public sealed class RealtimePersistenceQueue<T> : IAsyncDisposable
    where T : notnull
{
    private readonly Channel<T> channel;
    private readonly Func<T, Task> processor;
    private readonly Action<T>? releaseUnprocessed;
    private readonly Action<int>? depthChanged;
    private readonly Task worker;
    private ExceptionDispatchInfo? failure;
    private int pendingCount;
    private int highWaterMark;
    private int completionStarted;

    public RealtimePersistenceQueue(
        int capacity,
        Func<T, Task> processor,
        Action<T>? releaseUnprocessed = null,
        Action<int>? depthChanged = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.releaseUnprocessed = releaseUnprocessed;
        this.depthChanged = depthChanged;
        channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(ProcessAsync);
    }

    public int PendingCount => Math.Max(0, Volatile.Read(ref pendingCount));

    public int HighWaterMark => Volatile.Read(ref highWaterMark);

    public bool TryEnqueue(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfUnavailable();
        var pending = Interlocked.Increment(ref pendingCount);
        if (channel.Writer.TryWrite(item))
        {
            ObservePending(pending);
            return true;
        }

        DecrementPending();
        ThrowIfUnavailable();
        return false;
    }

    public async ValueTask EnqueueAsync(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfUnavailable();
        IncrementPending();
        try
        {
            await channel.Writer.WriteAsync(item).ConfigureAwait(false);
        }
        catch
        {
            DecrementPending();
            ThrowIfUnavailable();
            throw;
        }
    }

    public void ThrowIfFaulted() => failure?.Throw();

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref completionStarted, 1) == 0)
        {
            channel.Writer.TryComplete();
        }

        await worker.ConfigureAwait(false);
        failure?.Throw();
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await processor(item).ConfigureAwait(false);
                }
                finally
                {
                    DecrementPending();
                }
            }
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
            channel.Writer.TryComplete(ex);
            while (channel.Reader.TryRead(out var abandoned))
            {
                try
                {
                    releaseUnprocessed?.Invoke(abandoned);
                }
                finally
                {
                    DecrementPending();
                }
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        failure?.Throw();
        if (Volatile.Read(ref completionStarted) != 0)
        {
            throw new InvalidOperationException("Realtime persistence queue is completing.");
        }
    }

    private void IncrementPending()
    {
        var current = Interlocked.Increment(ref pendingCount);
        ObservePending(current);
    }

    private void ObservePending(int current)
    {
        var observed = Volatile.Read(ref highWaterMark);
        while (current > observed)
        {
            var prior = Interlocked.CompareExchange(ref highWaterMark, current, observed);
            if (prior == observed)
            {
                break;
            }

            observed = prior;
        }

        NotifyDepth(PendingCount);
    }

    private void DecrementPending()
    {
        var current = Interlocked.Decrement(ref pendingCount);
        NotifyDepth(Math.Max(0, current));
    }

    private void NotifyDepth(int depth)
    {
        try
        {
            depthChanged?.Invoke(depth);
        }
        catch
        {
            // Queue integrity cannot depend on an observability callback.
        }
    }
}
