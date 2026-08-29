using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Acquisition;

public sealed class ActiveBufferedAcquisitionSession<TPairing> : IDisposable
{
    private const int MaxAutoFlushConcurrency = 2;

    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopInitialBufferWaitTimeout = TimeSpan.FromMilliseconds(300);

    private readonly object gate = new();
    private readonly object lifecycleGate = new();
    private readonly List<BufferedAdcSegment> segments = [];
    private readonly HashSet<BufferedAdcSegment> activeSegments = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource cancellation = new();
    private readonly int readValueCount;
    private readonly long autoFlushByteThreshold;
    private readonly long maxBufferedByteCount;
    private readonly TimeSpan readLoopIdleDelay;
    private readonly long compressionStartByteThreshold;
    private readonly TimeSpan compressionYieldDelay;
    private readonly Func<bool> isMemoryPressureHigh;
    private readonly Func<ActiveBufferedAcquisitionSession<TPairing>, ushort[], DateTimeOffset, string, BufferedAcquisitionAutoFlushResult> autoFlush;
    private readonly Action<ActiveBufferedAcquisitionSession<TPairing>, long, long>? valuesDropped;
    private readonly Task readerTask;
    private readonly List<Task<BufferedAcquisitionAutoFlushResult>> autoFlushTasks = [];
    private readonly List<BufferedAcquisitionAutoFlushResult> autoFlushResults = [];
    private readonly HashSet<string> autoFlushResultPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Exception> autoFlushFailures = [];
    private readonly List<Exception> compressionFailures = [];
    private readonly TaskCompletionSource<object?> firstBufferReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AutoFlushCompletion> firstAutoFlushCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Hdf5ExcitationMetadata excitation;
    private long totalValueCount;
    private long storedByteCount;
    private long rawByteCount;
    private long droppedValueCount;
    private Task? compressionTask;
    private int compressionScanIndex;
    private int activeAutoFlushCount;
    private Task? stopTask;
    private bool disposeRequested;
    private volatile bool disposed;

    public ActiveBufferedAcquisitionSession(
        TPairing pairing,
        Usb2070Session usbSession,
        Usb2070AcquisitionMetadata acquisition,
        Hdf5ExcitationMetadata excitation,
        int readValueCount,
        long autoFlushByteThreshold,
        long maxBufferedByteCount,
        TimeSpan readLoopIdleDelay,
        long compressionStartByteThreshold,
        TimeSpan compressionYieldDelay,
        Func<bool> isMemoryPressureHigh,
        Func<ActiveBufferedAcquisitionSession<TPairing>, ushort[], DateTimeOffset, string, BufferedAcquisitionAutoFlushResult> autoFlush,
        Action<ActiveBufferedAcquisitionSession<TPairing>, long, long>? valuesDropped = null)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(usbSession);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(excitation);
        ArgumentNullException.ThrowIfNull(isMemoryPressureHigh);
        ArgumentNullException.ThrowIfNull(autoFlush);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readValueCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(autoFlushByteThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedByteCount);
        ArgumentOutOfRangeException.ThrowIfNegative(compressionStartByteThreshold);

        Pairing = pairing;
        UsbSession = usbSession;
        Acquisition = acquisition;
        this.excitation = excitation;
        this.readValueCount = readValueCount;
        this.autoFlushByteThreshold = autoFlushByteThreshold;
        this.maxBufferedByteCount = maxBufferedByteCount;
        this.readLoopIdleDelay = readLoopIdleDelay;
        this.compressionStartByteThreshold = compressionStartByteThreshold;
        this.compressionYieldDelay = compressionYieldDelay;
        this.isMemoryPressureHigh = isMemoryPressureHigh;
        this.autoFlush = autoFlush;
        this.valuesDropped = valuesDropped;
        StartedAt = DateTimeOffset.Now;
        readerTask = Task.Factory.StartNew(
            ReadLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public TPairing Pairing { get; }

    public Usb2070Session UsbSession { get; }

    public Usb2070AcquisitionMetadata Acquisition { get; }

    public Hdf5ExcitationMetadata Excitation
    {
        get
        {
            lock (gate)
            {
                return excitation;
            }
        }
    }

    public DateTimeOffset StartedAt { get; }

    public long DroppedValueCount
    {
        get
        {
            lock (gate)
            {
                return droppedValueCount;
            }
        }
    }

    public long BufferedValueCount
    {
        get
        {
            lock (gate)
            {
                return totalValueCount;
            }
        }
    }

    public long StoredByteCount
    {
        get
        {
            lock (gate)
            {
                return storedByteCount;
            }
        }
    }

    public long RawByteCount
    {
        get
        {
            lock (gate)
            {
                return rawByteCount;
            }
        }
    }

    public Exception? ReaderFailure { get; private set; }

    public Exception? StopFailure { get; private set; }

    public IReadOnlyList<BufferedAcquisitionAutoFlushResult> AutoFlushResults
    {
        get
        {
            lock (gate)
            {
                return autoFlushResults.ToArray();
            }
        }
    }

    public IReadOnlyList<Exception> AutoFlushFailures
    {
        get
        {
            lock (gate)
            {
                return autoFlushFailures.ToArray();
            }
        }
    }

    public IReadOnlyList<Exception> CompressionFailures
    {
        get
        {
            lock (gate)
            {
                return compressionFailures.ToArray();
            }
        }
    }

    public void UpdateExcitationMetadata(Hdf5ExcitationMetadata updatedExcitation)
    {
        ArgumentNullException.ThrowIfNull(updatedExcitation);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            excitation = updatedExcitation;
        }
    }

    public async Task WaitForBufferedDataAsync(TimeSpan timeout)
    {
        if (HasBufferedValues() || ReaderFailure is not null)
        {
            return;
        }

        await Task.WhenAny(firstBufferReady.Task, Task.Delay(timeout)).ConfigureAwait(true);
    }

    public Task<BufferedAcquisitionAutoFlushResult> WaitForFirstAutoFlushAsync(TimeSpan timeout) =>
        AwaitFirstAutoFlushCompletionAsync().WaitAsync(timeout);

    private async Task<BufferedAcquisitionAutoFlushResult> AwaitFirstAutoFlushCompletionAsync()
    {
        var completion = await firstAutoFlushCompletion.Task.ConfigureAwait(true);
        if (completion.Result is { } result)
        {
            return result;
        }

        throw new InvalidOperationException("首次自动落盘失败。", completion.Failure);
    }

    public ushort[] SnapshotValues()
    {
        BufferedAdcSegment[] snapshot;
        long valueCount;
        lock (gate)
        {
            if (totalValueCount <= 0)
            {
                return [];
            }

            snapshot = segments.ToArray();
            valueCount = totalValueCount;
        }

        return MaterializeSegments(snapshot, valueCount);
    }

    public ushort[] SnapshotRecentValues(int maxValueCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxValueCount);

        BufferedAdcSegment[] snapshot;
        long valueCount;
        lock (gate)
        {
            if (totalValueCount <= 0)
            {
                return [];
            }

            valueCount = Math.Min(totalValueCount, maxValueCount);
            var selected = new List<BufferedAdcSegment>();
            var remaining = valueCount;
            for (var index = segments.Count - 1; index >= 0 && remaining > 0; index--)
            {
                var segment = segments[index];
                selected.Add(segment);
                remaining -= segment.ValueCount;
            }

            selected.Reverse();
            snapshot = selected.ToArray();
        }

        var selectedValueCount = snapshot.Sum(segment => segment.ValueCount);
        var values = MaterializeSegments(snapshot, selectedValueCount);
        var skip = checked((int)(selectedValueCount - valueCount));
        if (skip <= 0)
        {
            return values;
        }

        var recent = new ushort[checked((int)valueCount)];
        Array.Copy(values, skip, recent, 0, recent.Length);
        return recent;
    }

    public Task StopAsync()
    {
        lock (lifecycleGate)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            if (disposed || disposeRequested)
            {
                return Task.CompletedTask;
            }

            stopTask = StopAndFinalizeAsync();
            return stopTask;
        }
    }

    private async Task StopAndFinalizeAsync()
    {
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            var releaseResources = false;
            lock (lifecycleGate)
            {
                if (disposeRequested && !disposed)
                {
                    disposed = true;
                    releaseResources = true;
                }
            }

            if (releaseResources)
            {
                ReleaseResources();
            }
        }
    }

    private async Task StopCoreAsync()
    {

        if (!HasBufferedValues() && ReaderFailure is null)
        {
            await WaitForBufferedDataAsync(StopInitialBufferWaitTimeout).ConfigureAwait(true);
        }

        cancellation.Cancel();
        try
        {
            UsbSession.StopAcquisition();
        }
        catch (Exception ex)
        {
            StopFailure = ex;
        }

        var completed = await Task.WhenAny(readerTask, Task.Delay(StopWaitTimeout)).ConfigureAwait(true);
        if (completed != readerTask)
        {
            ReaderFailure ??= new TimeoutException("USB2070 后台读取线程停止超时。");
        }

        await WaitForAutoFlushesAsync().ConfigureAwait(true);

        if (ReaderFailure is not null && BufferedValueCount == 0 && AutoFlushResults.Count == 0)
        {
            throw ReaderFailure;
        }

        if (StopFailure is not null && BufferedValueCount == 0 && AutoFlushResults.Count == 0)
        {
            throw StopFailure;
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed || disposeRequested)
            {
                return;
            }

            disposeRequested = true;
            if (stopTask is { IsCompleted: false })
            {
                return;
            }

            disposed = true;
        }

        ReleaseResources();
    }

    private void ReleaseResources()
    {
        cancellation.Cancel();
        try
        {
            UsbSession.StopAcquisition();
        }
        catch
        {
        }

        try
        {
            readerTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
        }

        cancellation.Dispose();
        UsbSession.Dispose();
    }

    private static ushort[] MaterializeSegments(IReadOnlyList<BufferedAdcSegment> segmentSnapshot, long valueCount)
    {
        var values = new ushort[checked((int)valueCount)];
        var offset = 0;
        foreach (var segment in segmentSnapshot)
        {
            segment.CopyTo(values, offset);
            offset += segment.ValueCount;
        }

        return values;
    }

    private bool HasBufferedValues()
    {
        lock (gate)
        {
            return totalValueCount > 0;
        }
    }

    private void ReadLoop()
    {
        var buffer = new ushort[readValueCount];
        while (!cancellation.IsCancellationRequested)
        {
            int readCount;
            try
            {
                readCount = UsbSession.Read(buffer, (uint)buffer.Length);
            }
            catch (Exception ex)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    ReaderFailure = ex;
                    firstBufferReady.TrySetResult(null);
                }

                break;
            }

            if (readCount > 0)
            {
                AppendSegment(buffer, readCount);
                StartAutoFlushIfNeeded();
            }

            if (readLoopIdleDelay > TimeSpan.Zero && cancellation.Token.WaitHandle.WaitOne(readLoopIdleDelay))
            {
                break;
            }
        }
    }

    private void AppendSegment(ushort[] buffer, int readCount)
    {
        var segment = BufferedAdcSegment.FromBuffer(buffer, readCount);
        long droppedThisAppend = 0;
        long totalDropped = 0;

        lock (gate)
        {
            segments.Add(segment);
            activeSegments.Add(segment);
            totalValueCount += segment.ValueCount;
            rawByteCount += segment.RawByteCount;
            storedByteCount += segment.StoredByteCount;
            firstBufferReady.TrySetResult(null);

            while (storedByteCount > maxBufferedByteCount && segments.Count > 0)
            {
                var removed = segments[0];
                segments.RemoveAt(0);
                activeSegments.Remove(removed);
                if (compressionScanIndex > 0)
                {
                    compressionScanIndex--;
                }

                totalValueCount -= removed.ValueCount;
                rawByteCount -= removed.RawByteCount;
                storedByteCount -= removed.StoredByteCount;
                droppedThisAppend += removed.ValueCount;
                droppedValueCount += removed.ValueCount;
                totalDropped = droppedValueCount;
            }
        }

        if (droppedThisAppend > 0)
        {
            valuesDropped?.Invoke(this, droppedThisAppend, totalDropped);
        }

        StartCompressionIfNeeded();
    }

    private void StartAutoFlushIfNeeded()
    {
        var reason = string.Empty;
        DetachedAdcSegments detached;
        lock (gate)
        {
            if (activeAutoFlushCount >= MaxAutoFlushConcurrency)
            {
                return;
            }

            var memoryPressure = isMemoryPressureHigh();
            if (storedByteCount < autoFlushByteThreshold && !memoryPressure)
            {
                return;
            }

            if (totalValueCount <= 0)
            {
                return;
            }

            reason = memoryPressure ? "memory pressure" : "buffer threshold";
            detached = DetachBufferedSegmentsUnsafe();
            activeAutoFlushCount++;
        }

        var capturedAt = DateTimeOffset.Now;
        var task = Task.Factory.StartNew(
            () =>
            {
                TrySetBackgroundThreadPriority("EitHost HDF5 auto-save");
                try
                {
                    var values = MaterializeSegments(detached.Segments, detached.ValueCount);
                    return autoFlush(this, values, capturedAt, reason);
                }
                finally
                {
                    lock (gate)
                    {
                        activeAutoFlushCount--;
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        lock (gate)
        {
            autoFlushTasks.Add(task);
        }

        _ = TrackFirstAutoFlushCompletionAsync(task);
    }

    private async Task TrackFirstAutoFlushCompletionAsync(Task<BufferedAcquisitionAutoFlushResult> task)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            await result.Completion.ConfigureAwait(false);
            firstAutoFlushCompletion.TrySetResult(new AutoFlushCompletion(result, null));
        }
        catch (Exception ex)
        {
            firstAutoFlushCompletion.TrySetResult(new AutoFlushCompletion(null, ex));
        }
    }

    private DetachedAdcSegments DetachBufferedSegmentsUnsafe()
    {
        var detached = new DetachedAdcSegments(segments.ToArray(), totalValueCount);
        foreach (var segment in segments)
        {
            activeSegments.Remove(segment);
        }

        segments.Clear();
        totalValueCount = 0;
        storedByteCount = 0;
        rawByteCount = 0;
        compressionScanIndex = 0;
        return detached;
    }

    private void StartCompressionIfNeeded()
    {
        lock (gate)
        {
            if (disposed || compressionTask is { IsCompleted: false })
            {
                return;
            }

            if (storedByteCount < compressionStartByteThreshold && !isMemoryPressureHigh())
            {
                return;
            }

            compressionTask = Task.Factory.StartNew(
                CompressSegments,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private void CompressSegments()
    {
        TrySetBackgroundThreadPriority("EitHost ADC compression");
        while (true)
        {
            BufferedAdcSegment? segment = null;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                while (compressionScanIndex < segments.Count)
                {
                    var candidate = segments[compressionScanIndex++];
                    if (candidate.TryBeginCompression())
                    {
                        segment = candidate;
                        break;
                    }
                }

                if (segment is null)
                {
                    return;
                }
            }

            try
            {
                var storedDelta = segment.CompleteCompression();
                if (storedDelta != 0)
                {
                    lock (gate)
                    {
                        if (activeSegments.Contains(segment))
                        {
                            storedByteCount += storedDelta;
                            if (storedByteCount < 0)
                            {
                                storedByteCount = 0;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    compressionFailures.Add(ex);
                }
            }

            if (compressionYieldDelay > TimeSpan.Zero && cancellation.Token.WaitHandle.WaitOne(compressionYieldDelay))
            {
                return;
            }
        }
    }

    private static void TrySetBackgroundThreadPriority(string threadName)
    {
        try
        {
            if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
            {
                Thread.CurrentThread.Name = threadName;
            }

            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch
        {
        }
    }

    private async Task WaitForAutoFlushesAsync()
    {
        Task<BufferedAcquisitionAutoFlushResult>[] tasks;
        lock (gate)
        {
            tasks = autoFlushTasks.ToArray();
        }

        foreach (var task in tasks)
        {
            try
            {
                var result = await task.ConfigureAwait(true);
                await result.Completion.ConfigureAwait(true);
                lock (gate)
                {
                    if (autoFlushResultPaths.Add(result.Hdf5Path))
                    {
                        autoFlushResults.Add(result);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    autoFlushFailures.Add(ex);
                }
            }
        }
    }

    private sealed record DetachedAdcSegments(
        IReadOnlyList<BufferedAdcSegment> Segments,
        long ValueCount);

    private sealed record AutoFlushCompletion(
        BufferedAcquisitionAutoFlushResult? Result,
        Exception? Failure);
}
