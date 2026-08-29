using System.Buffers;

namespace EitHost.Core.Application.Realtime;

public sealed class RealtimeRawBatch<TContext> : IDisposable
    where TContext : notnull
{
    private readonly IReadOnlyList<RealtimeRawBatchSegment> segments;
    private readonly ArrayPool<ushort>? segmentPool;
    private int disposed;

    public RealtimeRawBatch(
        TContext context,
        IReadOnlyList<ushort[]> segments,
        int valueCount,
        int segmentSequence,
        long startSampleIndex,
        long endSampleIndex,
        DateTimeOffset capturedAt,
        string reason,
        IReadOnlyList<Storage.Hdf5.RawAcquisitionDiscontinuityEvent> discontinuities)
        : this(
            context,
            segments?.Select(segment => new RealtimeRawBatchSegment(segment, segment.Length)).ToArray()
                ?? throw new ArgumentNullException(nameof(segments)),
            valueCount,
            segmentSequence,
            startSampleIndex,
            endSampleIndex,
            capturedAt,
            reason,
            discontinuities,
            segmentPool: null,
            returnSegmentsToPool: false)
    {
    }

    internal RealtimeRawBatch(
        TContext context,
        IReadOnlyList<RealtimeRawBatchSegment> segments,
        int valueCount,
        int segmentSequence,
        long startSampleIndex,
        long endSampleIndex,
        DateTimeOffset capturedAt,
        string reason,
        IReadOnlyList<Storage.Hdf5.RawAcquisitionDiscontinuityEvent> discontinuities,
        ArrayPool<ushort> segmentPool)
        : this(
            context,
            segments,
            valueCount,
            segmentSequence,
            startSampleIndex,
            endSampleIndex,
            capturedAt,
            reason,
            discontinuities,
            segmentPool: segmentPool ?? throw new ArgumentNullException(nameof(segmentPool)),
            returnSegmentsToPool: true)
    {
    }

    private RealtimeRawBatch(
        TContext context,
        IReadOnlyList<RealtimeRawBatchSegment> segments,
        int valueCount,
        int segmentSequence,
        long startSampleIndex,
        long endSampleIndex,
        DateTimeOffset capturedAt,
        string reason,
        IReadOnlyList<Storage.Hdf5.RawAcquisitionDiscontinuityEvent> discontinuities,
        ArrayPool<ushort>? segmentPool,
        bool returnSegmentsToPool)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(discontinuities);
        if (segments.Sum(segment => segment.Count) != valueCount)
        {
            throw new ArgumentException("Realtime raw batch segment lengths must equal value count.", nameof(segments));
        }

        Context = context;
        this.segments = segments;
        ValueCount = valueCount;
        SegmentSequence = segmentSequence;
        StartSampleIndex = startSampleIndex;
        EndSampleIndex = endSampleIndex;
        CapturedAt = capturedAt;
        Reason = reason.Trim();
        Discontinuities = discontinuities;
        this.segmentPool = returnSegmentsToPool ? segmentPool : null;
    }

    public TContext Context { get; }
    public int ValueCount { get; }
    public int SegmentSequence { get; }
    public long StartSampleIndex { get; }
    public long EndSampleIndex { get; }
    public DateTimeOffset CapturedAt { get; }
    public string Reason { get; }
    public IReadOnlyList<Storage.Hdf5.RawAcquisitionDiscontinuityEvent> Discontinuities { get; }

    public ushort[] MaterializeValues()
    {
        var values = new ushort[ValueCount];
        CopyValuesTo(values);
        return values;
    }

    public void CopyValuesTo(Span<ushort> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (destination.Length < ValueCount)
        {
            throw new ArgumentException("Destination is smaller than realtime raw batch.", nameof(destination));
        }

        var offset = 0;
        foreach (var segment in segments)
        {
            segment.Buffer.AsSpan(0, segment.Count).CopyTo(destination[offset..]);
            offset += segment.Count;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || segmentPool is null)
        {
            return;
        }

        foreach (var segment in segments)
        {
            segmentPool.Return(segment.Buffer);
        }
    }
}

internal readonly record struct RealtimeRawBatchSegment(ushort[] Buffer, int Count);

public sealed class RealtimeRawBatchCollector<TContext> : IDisposable
    where TContext : notnull
{
    private readonly TContext context;
    private readonly int channelCount;
    private readonly long flushValueThreshold;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly ArrayPool<ushort> segmentPool;
    private readonly List<RealtimeRawBatchSegment> segments = [];
    private readonly List<Storage.Hdf5.RawAcquisitionDiscontinuityEvent> discontinuities = [];
    private int valueCount;
    private int nextSegmentSequence;
    private long batchStartSampleIndex;
    private DateTimeOffset batchStartedAt;
    private bool disposed;

    public RealtimeRawBatchCollector(
        TContext context,
        int channelCount,
        long bytesPerValue,
        long flushByteThreshold,
        Func<DateTimeOffset>? utcNow = null,
        ArrayPool<ushort>? segmentPool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushByteThreshold);
        this.context = context;
        this.channelCount = channelCount;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.segmentPool = segmentPool ?? ArrayPool<ushort>.Shared;
        flushValueThreshold = Math.Max(channelCount, flushByteThreshold / bytesPerValue);
    }

    public RealtimeRawBatch<TContext>? Append(
        ushort[] source,
        int count,
        long startSampleIndex,
        Storage.Hdf5.RawAcquisitionDiscontinuityEvent? discontinuity = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        if (count <= 0)
        {
            return null;
        }

        if (count > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Realtime raw batch count cannot exceed source length.");
        }

        if (count % channelCount != 0)
        {
            throw new ArgumentException("Realtime raw batch must contain complete channel rows.", nameof(count));
        }

        var expectedStart = batchStartSampleIndex + (valueCount / channelCount);
        if (valueCount == 0)
        {
            batchStartSampleIndex = startSampleIndex;
            batchStartedAt = utcNow();
        }
        else if (startSampleIndex != expectedStart)
        {
            throw new InvalidOperationException(
                $"Realtime raw sample discontinuity: expected {expectedStart}, actual {startSampleIndex}.");
        }

        if (discontinuity is not null)
        {
            var readEndSampleIndex = checked(startSampleIndex + count / channelCount);
            if (discontinuity.StartSampleIndex != startSampleIndex ||
                discontinuity.EndSampleIndex != readEndSampleIndex)
            {
                throw new ArgumentException(
                    "Realtime discontinuity must cover exactly the affected USB read.",
                    nameof(discontinuity));
            }
        }

        var copy = segmentPool.Rent(count);
        source.AsSpan(0, count).CopyTo(copy);
        segments.Add(new RealtimeRawBatchSegment(copy, count));
        valueCount = checked(valueCount + count);
        if (discontinuity is not null)
        {
            discontinuities.Add(discontinuity);
        }

        return valueCount >= flushValueThreshold ? Detach("threshold") : null;
    }

    public RealtimeRawBatch<TContext>? Detach(string reason)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (valueCount <= 0)
        {
            return null;
        }

        var detached = segments.ToArray();
        var detachedCount = valueCount;
        var capturedAt = batchStartedAt;
        var startSampleIndex = batchStartSampleIndex;
        var endSampleIndex = checked(startSampleIndex + detachedCount / channelCount);
        var segmentSequence = nextSegmentSequence++;
        var detachedDiscontinuities = discontinuities.ToArray();
        segments.Clear();
        discontinuities.Clear();
        valueCount = 0;
        batchStartSampleIndex = endSampleIndex;
        return new RealtimeRawBatch<TContext>(
            context,
            detached,
            detachedCount,
            segmentSequence,
            startSampleIndex,
            endSampleIndex,
            capturedAt,
            reason,
            detachedDiscontinuities,
            segmentPool);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var segment in segments)
        {
            segmentPool.Return(segment.Buffer);
        }

        segments.Clear();
        discontinuities.Clear();
        valueCount = 0;
    }
}
