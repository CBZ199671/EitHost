namespace EitHost.Core.Acquisition;

public sealed class RealtimeRawRingBuffer
{
    public const long DefaultMaximumBytes = 16L * 1024L * 1024L;
    private const int BytesPerValue = sizeof(ushort);

    private readonly object gate = new();
    private readonly Queue<RealtimeRawRingSegment> segments = new();
    private readonly int channelCount;
    private readonly int maximumValueCount;
    private int valueCount;

    public RealtimeRawRingBuffer(
        long maximumBytes = DefaultMaximumBytes,
        int channelCount = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        var rawMaximumValues = Math.Min(int.MaxValue, maximumBytes / BytesPerValue);
        maximumValueCount = checked((int)(rawMaximumValues / channelCount * channelCount));
        if (maximumValueCount < channelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                "Raw ring capacity must hold at least one complete channel row.");
        }

        this.channelCount = channelCount;
    }

    public int ValueCount
    {
        get
        {
            lock (gate)
            {
                return valueCount;
            }
        }
    }

    public long ByteCount => (long)ValueCount * BytesPerValue;

    public void Append(ushort[] source, int count, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (count < 0 || count > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return;
        }

        var alignedCount = count / channelCount * channelCount;
        if (alignedCount == 0)
        {
            return;
        }

        var retainedCount = Math.Min(alignedCount, maximumValueCount);
        var copy = new ushort[retainedCount];
        Array.Copy(source, alignedCount - retainedCount, copy, 0, retainedCount);
        lock (gate)
        {
            segments.Enqueue(new RealtimeRawRingSegment(capturedAt, copy));
            valueCount = checked(valueCount + retainedCount);
            while (valueCount > maximumValueCount && segments.Count > 0)
            {
                var excess = valueCount - maximumValueCount;
                var oldest = segments.Dequeue();
                if (oldest.Values.Length <= excess)
                {
                    valueCount -= oldest.Values.Length;
                    continue;
                }

                var retained = new ushort[oldest.Values.Length - excess];
                Array.Copy(oldest.Values, excess, retained, 0, retained.Length);
                var newer = segments.ToArray();
                segments.Clear();
                segments.Enqueue(new RealtimeRawRingSegment(oldest.CapturedAt, retained));
                foreach (var segment in newer)
                {
                    segments.Enqueue(segment);
                }

                valueCount -= excess;
            }
        }
    }

    public RealtimeRawRingSnapshot? Snapshot()
    {
        lock (gate)
        {
            if (valueCount == 0)
            {
                return null;
            }

            var snapshot = segments.ToArray();
            return new RealtimeRawRingSnapshot(
                snapshot[0].CapturedAt,
                snapshot[^1].CapturedAt,
                snapshot.Select(segment => segment.Values).ToArray(),
                valueCount);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            segments.Clear();
            valueCount = 0;
        }
    }

    private sealed record RealtimeRawRingSegment(DateTimeOffset CapturedAt, ushort[] Values);
}

public sealed record RealtimeRawRingSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<ushort[]> Segments,
    int ValueCount);
