using System.Diagnostics;
using System.Threading.Channels;

namespace EitHost.Core.Demodulation;

public sealed class RealtimeDemodulationPipeline : IAsyncDisposable
{
    private const int ChannelCount = DemodulatedFrame.StimulationCount;

    private readonly RealtimeBlockDemodulator demodulator;
    private readonly RealtimeDemodulationPipelineOptions options;
    private readonly Channel<RealtimeSampleChunk> sampleQueue;
    private readonly Channel<RealtimeDemodulatedBlock> blockQueue;
    private readonly List<RealtimeDemodulatedBlock> blocks = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource processingCancellation = new();
    private readonly Task processingTask;
    private long enqueuedSampleRows;
    private long submittedSampleRows;
    private long droppedSampleRows;
    private long nextSubmittedSampleIndex;
    private long expectedNextSampleIndex;
    private long processedChunkCount;
    private long processingTicks;
    private long discontinuityCount;
    private long overflowCount;
    private int queuedSampleChunkCount;
    private int sampleQueueHighWaterMark;
    private int processedBlockCount;
    private int droppedBlockCount;
    private int completionRequested;
    private int abortRequested;

    public RealtimeDemodulationPipeline(
        RealtimeDemodulationSettings settings,
        int queueCapacity = 512,
        OfflineDemodulator? offlineDemodulator = null)
        : this(
            new RealtimeBlockDemodulator(settings, offlineDemodulator),
            new RealtimeDemodulationPipelineOptions(SampleQueueCapacity: queueCapacity))
    {
    }

    public RealtimeDemodulationPipeline(
        RealtimeDemodulationSettings settings,
        RealtimeDemodulationPipelineOptions options,
        OfflineDemodulator? offlineDemodulator = null)
        : this(new RealtimeBlockDemodulator(settings, offlineDemodulator), options)
    {
    }

    public RealtimeDemodulationPipeline(
        RealtimeBlockDemodulator demodulator,
        int queueCapacity = 512)
        : this(
            demodulator,
            new RealtimeDemodulationPipelineOptions(SampleQueueCapacity: queueCapacity))
    {
    }

    public RealtimeDemodulationPipeline(
        RealtimeBlockDemodulator demodulator,
        RealtimeDemodulationPipelineOptions options)
    {
        this.demodulator = demodulator ?? throw new ArgumentNullException(nameof(demodulator));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleQueueCapacity);
        if (options.BlockQueueCapacity is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Block queue capacity must be positive when specified.");
        }

        if (options.DropOldestBlocksWhenFull && options.BlockQueueCapacity is null)
        {
            throw new ArgumentException("Drop-oldest block mode requires a bounded block queue.", nameof(options));
        }

        if (options.SampleQueueRecoveryLowWaterMark is < 0 ||
            options.SampleQueueRecoveryLowWaterMark >= options.SampleQueueCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Sample queue recovery low-water mark must be in [0, capacity).");
        }

        sampleQueue = Channel.CreateBounded<RealtimeSampleChunk>(
            new BoundedChannelOptions(options.SampleQueueCapacity)
            {
                SingleReader = !options.DropOldestSamplesWhenFull,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        blockQueue = options.BlockQueueCapacity is { } blockCapacity
            ? Channel.CreateBounded<RealtimeDemodulatedBlock>(
                new BoundedChannelOptions(blockCapacity)
                {
                    SingleReader = false,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                })
            : Channel.CreateUnbounded<RealtimeDemodulatedBlock>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = true
                });
        processingTask = Task.Factory.StartNew(
                () => ProcessOnDedicatedThread(processingCancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
    }

    public int ProcessedBlockCount => Volatile.Read(ref processedBlockCount);

    public int DroppedBlockCount => Volatile.Read(ref droppedBlockCount);

    public int CadenceRefreshRejectedCount => demodulator.CadenceRefreshRejectedCount;

    public double? LastRejectedCadenceRefreshSamples => demodulator.LastRejectedCadenceRefreshSamples;

    public double? LockedWindowSamples => demodulator.LockedWindowSamples;

    public bool Aborted => Volatile.Read(ref abortRequested) != 0;

    public long EnqueuedSampleRows => Interlocked.Read(ref enqueuedSampleRows);

    public long SubmittedSampleRows => Interlocked.Read(ref submittedSampleRows);

    public long DroppedSampleRows => Interlocked.Read(ref droppedSampleRows);

    public long DiscontinuityCount => Interlocked.Read(ref discontinuityCount);

    public long OverflowCount => Interlocked.Read(ref overflowCount);

    public int SampleQueueHighWaterMark => Volatile.Read(ref sampleQueueHighWaterMark);

    public int QueuedSampleChunkCount => Math.Max(0, Volatile.Read(ref queuedSampleChunkCount));

    public long ProcessedChunkCount => Interlocked.Read(ref processedChunkCount);

    public double TotalProcessingMilliseconds => TimeSpan
        .FromTicks(Interlocked.Read(ref processingTicks))
        .TotalMilliseconds;

    public async ValueTask EnqueueAsync(
        ushort[,] rawAdcCounts,
        CancellationToken cancellationToken = default)
    {
        ValidateChunk(rawAdcCounts);
        var rowCount = rawAdcCounts.GetLength(0);
        var startSampleIndex = Interlocked.Add(ref nextSubmittedSampleIndex, rowCount) - rowCount;
        Interlocked.Add(ref submittedSampleRows, rowCount);
        var chunk = new RealtimeSampleChunk(rawAdcCounts, startSampleIndex, BufferOverflow: false);
        await sampleQueue.Writer
            .WriteAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        RecordEnqueued(chunk);
    }

    public bool TryEnqueue(
        ushort[,] rawAdcCounts,
        long startSampleIndex,
        bool bufferOverflow = false)
    {
        ValidateChunk(rawAdcCounts);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        var rowCount = rawAdcCounts.GetLength(0);
        Interlocked.Exchange(ref nextSubmittedSampleIndex, checked(startSampleIndex + rowCount));
        Interlocked.Add(ref submittedSampleRows, rowCount);
        var chunk = new RealtimeSampleChunk(rawAdcCounts, startSampleIndex, bufferOverflow);

        while (!sampleQueue.Writer.TryWrite(chunk))
        {
            if (!options.DropOldestSamplesWhenFull)
            {
                Interlocked.Add(ref droppedSampleRows, rowCount);
                return false;
            }

            if (sampleQueue.Reader.TryRead(out var dropped))
            {
                RecordDequeued(dropped);
                Interlocked.Add(ref droppedSampleRows, dropped.RowCount);
                var recoveryLowWaterMark = options.SampleQueueRecoveryLowWaterMark ??
                    options.SampleQueueCapacity - 1;
                while (QueuedSampleChunkCount > recoveryLowWaterMark &&
                       sampleQueue.Reader.TryRead(out dropped))
                {
                    RecordDequeued(dropped);
                    Interlocked.Add(ref droppedSampleRows, dropped.RowCount);
                }

                continue;
            }

            if (Volatile.Read(ref completionRequested) != 0)
            {
                Interlocked.Add(ref droppedSampleRows, rowCount);
                return false;
            }

            Thread.Yield();
        }

        RecordEnqueued(chunk);
        return true;
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref completionRequested, 1) == 0)
        {
            sampleQueue.Writer.TryComplete();
        }

        try
        {
            await processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Aborted)
        {
            // AbortAsync intentionally discards queued realtime work.
        }
    }

    public async Task AbortAsync()
    {
        Interlocked.Exchange(ref abortRequested, 1);
        Interlocked.Exchange(ref completionRequested, 1);
        sampleQueue.Writer.TryComplete();
        processingCancellation.Cancel();
        try
        {
            await processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: pending samples and blocks are deliberately discarded.
        }
    }

    public IReadOnlyList<RealtimeDemodulatedBlock> GetBlocksSnapshot()
    {
        lock (gate)
        {
            return blocks.ToArray();
        }
    }

    public IAsyncEnumerable<RealtimeDemodulatedBlock> ReadBlocksAsync(
        CancellationToken cancellationToken = default)
    {
        return blockQueue.Reader.ReadAllAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            processingCancellation.Dispose();
        }
    }

    private void ProcessOnDedicatedThread(CancellationToken cancellationToken)
    {
        try
        {
            while (sampleQueue.Reader
                       .WaitToReadAsync(cancellationToken)
                       .AsTask()
                       .GetAwaiter()
                       .GetResult())
            {
                while (sampleQueue.Reader.TryRead(out var chunk))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RecordDequeued(chunk);
                    var expectedStart = Interlocked.Read(ref expectedNextSampleIndex);
                    var actualEnd = checked(chunk.StartSampleIndex + chunk.RowCount);
                    if (chunk.BufferOverflow || chunk.StartSampleIndex != expectedStart)
                    {
                        var gapRows = Math.Max(0, chunk.StartSampleIndex - expectedStart);
                        var reason = chunk.BufferOverflow
                            ? "usb-buffer-overflow"
                            : chunk.StartSampleIndex > expectedStart
                                ? "sample-gap"
                                : "sample-overlap";
                        demodulator.ResetForDiscontinuity(chunk.BufferOverflow ? actualEnd : chunk.StartSampleIndex);
                        Interlocked.Increment(ref discontinuityCount);
                        if (chunk.BufferOverflow)
                        {
                            Interlocked.Increment(ref overflowCount);
                        }

                        NotifyDiscontinuity(
                            new RealtimeSampleDiscontinuity(
                                reason,
                                expectedStart,
                                chunk.StartSampleIndex,
                                gapRows,
                                chunk.BufferOverflow));
                        Interlocked.Exchange(ref expectedNextSampleIndex, actualEnd);
                        if (chunk.BufferOverflow)
                        {
                            Interlocked.Increment(ref processedChunkCount);
                            continue;
                        }
                    }

                    var stopwatch = Stopwatch.StartNew();
                    demodulator.AppendSamples(chunk.RawAdcCounts);
                    var newBlocks = demodulator.ProcessAvailableBlocks();
                    stopwatch.Stop();
                    Interlocked.Exchange(ref expectedNextSampleIndex, actualEnd);

                    if (newBlocks.Count > 0)
                    {
                        if (options.RetainProcessedBlocks)
                        {
                            lock (gate)
                            {
                                blocks.AddRange(newBlocks);
                            }
                        }

                        Interlocked.Add(ref processedBlockCount, newBlocks.Count);

                        foreach (var block in newBlocks)
                        {
                            PublishBlock(block, cancellationToken);
                        }
                    }

                    Interlocked.Increment(ref processedChunkCount);
                    Interlocked.Add(ref processingTicks, stopwatch.ElapsedTicks);
                }
            }
        }
        finally
        {
            blockQueue.Writer.TryComplete();
        }
    }

    private void PublishBlock(
        RealtimeDemodulatedBlock block,
        CancellationToken cancellationToken)
    {
        if (options.BlockQueueCapacity is null)
        {
            blockQueue.Writer.TryWrite(block);
            return;
        }

        if (!options.DropOldestBlocksWhenFull)
        {
            blockQueue.Writer
                .WriteAsync(block, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return;
        }

        while (!blockQueue.Writer.TryWrite(block))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blockQueue.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref droppedBlockCount);
                continue;
            }

            Thread.Yield();
        }
    }

    private static void ValidateChunk(ushort[,] rawAdcCounts)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        if (rawAdcCounts.GetLength(0) <= 0)
        {
            throw new ArgumentException("Realtime demodulation chunk must contain at least one sample row.", nameof(rawAdcCounts));
        }

        if (rawAdcCounts.GetLength(1) != ChannelCount)
        {
            throw new ArgumentException("Realtime demodulation expects raw data shaped [sample, 16].", nameof(rawAdcCounts));
        }
    }

    private void RecordEnqueued(RealtimeSampleChunk chunk)
    {
        Interlocked.Add(ref enqueuedSampleRows, chunk.RowCount);
        var queued = Interlocked.Increment(ref queuedSampleChunkCount);
        var boundedQueued = Math.Min(queued, options.SampleQueueCapacity);
        while (true)
        {
            var currentHighWater = Volatile.Read(ref sampleQueueHighWaterMark);
            if (boundedQueued <= currentHighWater ||
                Interlocked.CompareExchange(ref sampleQueueHighWaterMark, boundedQueued, currentHighWater) == currentHighWater)
            {
                break;
            }
        }

        Volatile.Write(ref chunk.AccountingReady, 1);
    }

    private void RecordDequeued(RealtimeSampleChunk chunk)
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref chunk.AccountingReady) == 0)
        {
            spin.SpinOnce();
        }

        while (true)
        {
            var queued = Volatile.Read(ref queuedSampleChunkCount);
            if (queued <= 0 ||
                Interlocked.CompareExchange(ref queuedSampleChunkCount, queued - 1, queued) == queued)
            {
                return;
            }
        }
    }

    private void NotifyDiscontinuity(RealtimeSampleDiscontinuity discontinuity)
    {
        try
        {
            options.DiscontinuityObserver?.Invoke(discontinuity);
        }
        catch
        {
            // Telemetry/reset observers must never terminate the demodulation consumer.
        }
    }

    private sealed record RealtimeSampleChunk(
        ushort[,] RawAdcCounts,
        long StartSampleIndex,
        bool BufferOverflow)
    {
        public int RowCount => RawAdcCounts.GetLength(0);

        public int AccountingReady;
    }
}

public sealed record RealtimeDemodulationPipelineOptions(
    int SampleQueueCapacity = 512,
    int? BlockQueueCapacity = null,
    bool DropOldestBlocksWhenFull = false,
    bool RetainProcessedBlocks = true,
    bool DropOldestSamplesWhenFull = false,
    int? SampleQueueRecoveryLowWaterMark = null,
    Action<RealtimeSampleDiscontinuity>? DiscontinuityObserver = null);

public sealed record RealtimeSampleDiscontinuity(
    string Reason,
    long ExpectedStartSampleIndex,
    long ActualStartSampleIndex,
    long MissingSampleRows,
    bool BufferOverflow);
