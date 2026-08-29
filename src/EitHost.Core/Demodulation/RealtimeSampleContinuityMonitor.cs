namespace EitHost.Core.Demodulation;

public sealed class RealtimeSampleContinuityMonitor
{
    private readonly object gate = new();
    private long totalDiscontinuities;
    private long totalMissingSampleRows;
    private long totalUsbOverflows;
    private int pendingDiscontinuities;
    private long pendingMissingSampleRows;
    private int pendingUsbOverflows;
    private RealtimeSampleDiscontinuity? latest;

    public void Report(RealtimeSampleDiscontinuity discontinuity)
    {
        ArgumentNullException.ThrowIfNull(discontinuity);

        lock (gate)
        {
            totalDiscontinuities++;
            totalMissingSampleRows += discontinuity.MissingSampleRows;
            if (discontinuity.BufferOverflow)
            {
                totalUsbOverflows++;
                pendingUsbOverflows++;
            }

            pendingDiscontinuities++;
            pendingMissingSampleRows += discontinuity.MissingSampleRows;
            latest = discontinuity;
        }
    }

    public RealtimeSampleContinuitySnapshot Snapshot()
    {
        lock (gate)
        {
            return new RealtimeSampleContinuitySnapshot(
                totalDiscontinuities,
                totalMissingSampleRows,
                totalUsbOverflows,
                pendingDiscontinuities,
                latest);
        }
    }

    public bool TryDrain(out RealtimeSampleDiscontinuityBatch batch)
    {
        lock (gate)
        {
            if (pendingDiscontinuities == 0 || latest is null)
            {
                batch = default!;
                return false;
            }

            batch = new RealtimeSampleDiscontinuityBatch(
                pendingDiscontinuities,
                pendingMissingSampleRows,
                pendingUsbOverflows,
                latest);
            pendingDiscontinuities = 0;
            pendingMissingSampleRows = 0;
            pendingUsbOverflows = 0;
            return true;
        }
    }
}

public sealed record RealtimeSampleContinuitySnapshot(
    long TotalDiscontinuities,
    long TotalMissingSampleRows,
    long TotalUsbOverflows,
    int PendingDiscontinuities,
    RealtimeSampleDiscontinuity? Latest);

public sealed record RealtimeSampleDiscontinuityBatch(
    int DiscontinuityCount,
    long MissingSampleRows,
    int UsbOverflowCount,
    RealtimeSampleDiscontinuity Latest);
