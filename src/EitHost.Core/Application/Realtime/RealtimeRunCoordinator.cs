using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Application.Realtime;

public enum RealtimeRunPhase
{
    Created,
    Running,
    StopRequested,
    Stopped,
    Completed,
    Faulted,
    Disposed
}

public sealed record RealtimeRunSnapshot(
    string SetLabel,
    RealtimeRunPhase Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long TotalRawSamples,
    int DiscontinuityCount,
    int UsbOverflowCount,
    int PendingRawPersistenceCount,
    string? FailureMessage,
    long Revision)
{
    public bool IsActive => Phase is RealtimeRunPhase.Running or RealtimeRunPhase.StopRequested;
}

public abstract record RealtimeRunEvent(
    string SetLabel,
    DateTimeOffset OccurredAt,
    long Revision);

public sealed record RealtimeRunStateChanged(
    string SetLabel,
    DateTimeOffset OccurredAt,
    long Revision,
    RealtimeRunPhase Phase) : RealtimeRunEvent(SetLabel, OccurredAt, Revision);

public sealed record RealtimeRunDiscontinuityObserved(
    string SetLabel,
    DateTimeOffset OccurredAt,
    long Revision,
    RawAcquisitionDiscontinuityEvent Discontinuity) : RealtimeRunEvent(SetLabel, OccurredAt, Revision);

public sealed class RealtimeRunCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<RawAcquisitionDiscontinuityEvent> discontinuities = [];
    private readonly List<Task> rawPersistenceTasks = [];
    private RealtimeRunSnapshot snapshot;
    private Task? runTask;
    private Task? consumerTask;
    private int nextRawSegmentSequence;
    private bool disposed;

    public RealtimeRunCoordinator(string setLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        snapshot = new RealtimeRunSnapshot(
            setLabel.Trim(),
            RealtimeRunPhase.Created,
            StartedAt: null,
            EndedAt: null,
            TotalRawSamples: 0,
            DiscontinuityCount: 0,
            UsbOverflowCount: 0,
            PendingRawPersistenceCount: 0,
            FailureMessage: null,
            Revision: 0);
    }

    public event Action<RealtimeRunSnapshot>? SnapshotChanged;

    public event Action<RealtimeRunEvent>? EventPublished;

    public string SetLabel => snapshot.SetLabel;

    public CancellationTokenSource Cancellation => cancellation;

    public Task? Task
    {
        get
        {
            lock (gate)
            {
                return runTask;
            }
        }
    }

    public RealtimeRunSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public bool IsActive => Snapshot.IsActive;

    public bool IsStopRequested => cancellation.IsCancellationRequested;

    public void AttachConsumer(Task consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (gate)
        {
            ThrowIfDisposed();
            if (consumerTask is not null)
            {
                throw new InvalidOperationException($"Realtime run {SetLabel} already has a consumer task.");
            }

            consumerTask = consumer;
        }
    }

    public void EnsureConsumerRunning()
    {
        Task consumer;
        lock (gate)
        {
            consumer = consumerTask ?? throw new InvalidOperationException(
                $"Realtime run {SetLabel} has no consumer task.");
        }

        if (!consumer.IsCompleted)
        {
            return;
        }

        consumer.GetAwaiter().GetResult();
        throw new InvalidOperationException("Realtime demodulation consumer stopped unexpectedly.");
    }

    public Task WaitForConsumerAsync()
    {
        lock (gate)
        {
            return consumerTask ?? System.Threading.Tasks.Task.CompletedTask;
        }
    }

    public Task Start(Func<CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RealtimeRunSnapshot changed;
        lock (gate)
        {
            ThrowIfDisposed();
            if (runTask is not null || snapshot.Phase != RealtimeRunPhase.Created)
            {
                throw new InvalidOperationException($"Realtime run {SetLabel} has already been started.");
            }

            var startedAt = DateTimeOffset.UtcNow;
            changed = NextSnapshot(
                snapshot with
                {
                    Phase = RealtimeRunPhase.Running,
                    StartedAt = startedAt,
                    EndedAt = null,
                    FailureMessage = null
                });
            runTask = Task.Run(async () =>
            {
                await startSignal.Task.ConfigureAwait(false);
                await ObserveRunAsync(run).ConfigureAwait(false);
            });
        }

        try
        {
            Publish(changed, new RealtimeRunStateChanged(
                changed.SetLabel,
                changed.StartedAt!.Value,
                changed.Revision,
                changed.Phase));
        }
        finally
        {
            startSignal.TrySetResult();
        }

        return runTask;
    }

    public bool RequestStop()
    {
        RealtimeRunSnapshot? changed = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!snapshot.IsActive || cancellation.IsCancellationRequested)
            {
                return false;
            }

            changed = NextSnapshot(snapshot with { Phase = RealtimeRunPhase.StopRequested });
        }

        try
        {
            Publish(changed, new RealtimeRunStateChanged(
                changed.SetLabel,
                DateTimeOffset.UtcNow,
                changed.Revision,
                changed.Phase));
        }
        finally
        {
            cancellation.Cancel();
        }

        return true;
    }

    public void RecordRawProgress(long totalRawSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalRawSamples);
        RealtimeRunSnapshot changed;
        lock (gate)
        {
            ThrowIfDisposed();
            if (totalRawSamples < snapshot.TotalRawSamples)
            {
                throw new InvalidOperationException("Realtime raw sample progress must be monotonic.");
            }

            changed = NextSnapshot(snapshot with { TotalRawSamples = totalRawSamples });
        }

        SnapshotChanged?.Invoke(changed);
    }

    public void RecordAcquisitionDiscontinuity(RawAcquisitionDiscontinuityEvent discontinuity)
    {
        ArgumentNullException.ThrowIfNull(discontinuity);
        RealtimeRunSnapshot changed;
        lock (gate)
        {
            ThrowIfDisposed();
            if (discontinuities.Count > 0 &&
                discontinuity.StartSampleIndex < discontinuities[^1].EndSampleIndex)
            {
                throw new InvalidOperationException(
                    "Realtime acquisition discontinuity events must be monotonic and non-overlapping.");
            }

            discontinuities.Add(discontinuity);
            changed = NextSnapshot(snapshot with
            {
                DiscontinuityCount = discontinuities.Count,
                UsbOverflowCount = snapshot.UsbOverflowCount + 1
            });
        }

        Publish(changed, new RealtimeRunDiscontinuityObserved(
            changed.SetLabel,
            discontinuity.DetectedAt,
            changed.Revision,
            discontinuity));
    }

    public DateTimeOffset CalculateAcquiredAt(
        DateTimeOffset startedAt,
        long sampleIndex,
        int sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        var anchorSampleIndex = 0L;
        var anchorAt = startedAt;
        lock (gate)
        {
            foreach (var discontinuity in discontinuities)
            {
                if (discontinuity.EndSampleIndex > sampleIndex)
                {
                    break;
                }

                anchorSampleIndex = discontinuity.EndSampleIndex;
                anchorAt = discontinuity.DetectedAt;
            }
        }

        return anchorAt + TimeSpan.FromSeconds(
            (sampleIndex - anchorSampleIndex) / (double)sampleRateHz);
    }

    public int AllocateRawSegmentSequence()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return nextRawSegmentSequence++;
        }
    }

    public void TrackRawPersistence(Task persistenceTask)
    {
        ArgumentNullException.ThrowIfNull(persistenceTask);
        RealtimeRunSnapshot changed;
        lock (gate)
        {
            ThrowIfDisposed();
            rawPersistenceTasks.Add(persistenceTask);
            changed = NextSnapshot(snapshot with
            {
                PendingRawPersistenceCount = rawPersistenceTasks.Count(task => !task.IsCompleted)
            });
        }

        SnapshotChanged?.Invoke(changed);
    }

    public void RecordRawPersistenceQueueDepth(int pendingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCount);
        RealtimeRunSnapshot changed;
        lock (gate)
        {
            ThrowIfDisposed();
            changed = NextSnapshot(snapshot with { PendingRawPersistenceCount = pendingCount });
        }

        SnapshotChanged?.Invoke(changed);
    }

    public async Task DrainRawPersistenceAsync()
    {
        Task[] tasks;
        lock (gate)
        {
            tasks = rawPersistenceTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            RealtimeRunSnapshot changed;
            lock (gate)
            {
                rawPersistenceTasks.RemoveAll(task => task.IsCompleted);
                changed = NextSnapshot(snapshot with
                {
                    PendingRawPersistenceCount = rawPersistenceTasks.Count(task => !task.IsCompleted)
                });
            }

            SnapshotChanged?.Invoke(changed);
        }
    }

    public void Dispose()
    {
        RealtimeRunSnapshot? changed = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            changed = NextSnapshot(snapshot with
            {
                Phase = RealtimeRunPhase.Disposed,
                EndedAt = snapshot.EndedAt ?? DateTimeOffset.UtcNow
            });
        }

        cancellation.Cancel();
        cancellation.Dispose();
        SnapshotChanged?.Invoke(changed);
    }

    private async Task ObserveRunAsync(Func<CancellationToken, Task> run)
    {
        try
        {
            await run(cancellation.Token).ConfigureAwait(false);
            Complete(cancellation.IsCancellationRequested
                ? RealtimeRunPhase.Stopped
                : RealtimeRunPhase.Completed);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Complete(RealtimeRunPhase.Stopped);
        }
        catch (Exception ex)
        {
            Complete(RealtimeRunPhase.Faulted, ex.Message);
            throw;
        }
    }

    private void Complete(RealtimeRunPhase phase, string? failureMessage = null)
    {
        RealtimeRunSnapshot changed;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            changed = NextSnapshot(snapshot with
            {
                Phase = phase,
                EndedAt = DateTimeOffset.UtcNow,
                FailureMessage = failureMessage
            });
        }

        Publish(changed, new RealtimeRunStateChanged(
            changed.SetLabel,
            changed.EndedAt!.Value,
            changed.Revision,
            changed.Phase));
    }

    private RealtimeRunSnapshot NextSnapshot(RealtimeRunSnapshot next)
    {
        snapshot = next with { Revision = checked(snapshot.Revision + 1) };
        return snapshot;
    }

    private void Publish(RealtimeRunSnapshot changed, RealtimeRunEvent runEvent)
    {
        SnapshotChanged?.Invoke(changed);
        EventPublished?.Invoke(runEvent);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
