using System.Diagnostics;

namespace EitHost.Core.Reconstruction;

public sealed class RealtimeAdaptiveCadence
{
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMilliseconds(75);
    public static readonly TimeSpan DefaultMaximumInterval = TimeSpan.FromMilliseconds(500);

    private readonly double targetUtilization;
    private readonly double smoothingFactor;
    private long lastScheduleTicks;
    private double backendEwmaMilliseconds;
    private double currentIntervalMilliseconds;

    public RealtimeAdaptiveCadence(
        TimeSpan? minimumInterval = null,
        TimeSpan? maximumInterval = null,
        double targetUtilization = 0.80,
        double smoothingFactor = 0.25)
    {
        MinimumInterval = minimumInterval ?? DefaultMinimumInterval;
        MaximumInterval = maximumInterval ?? DefaultMaximumInterval;
        if (MinimumInterval <= TimeSpan.Zero || MaximumInterval < MinimumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        if (!double.IsFinite(targetUtilization) || targetUtilization <= 0.0 || targetUtilization > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetUtilization));
        }

        if (!double.IsFinite(smoothingFactor) || smoothingFactor <= 0.0 || smoothingFactor > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(smoothingFactor));
        }

        this.targetUtilization = targetUtilization;
        this.smoothingFactor = smoothingFactor;
        currentIntervalMilliseconds = MinimumInterval.TotalMilliseconds;
    }

    public TimeSpan MinimumInterval { get; }

    public TimeSpan MaximumInterval { get; }

    public TimeSpan CurrentInterval => TimeSpan.FromMilliseconds(Volatile.Read(ref currentIntervalMilliseconds));

    public double BackendEwmaMilliseconds => Volatile.Read(ref backendEwmaMilliseconds);

    public double TargetFramesPerSecond => 1000.0 / Math.Max(CurrentInterval.TotalMilliseconds, 1.0);

    public bool TrySchedule()
    {
        return TryScheduleAt(Stopwatch.GetTimestamp());
    }

    public bool TryScheduleAt(long nowTicks)
    {
        var previous = Interlocked.Read(ref lastScheduleTicks);
        var requiredTicks = (long)(CurrentInterval.TotalSeconds * Stopwatch.Frequency);
        if (previous != 0 && nowTicks - previous < requiredTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref lastScheduleTicks, nowTicks, previous) == previous;
    }

    public void ObserveWarmBackend(TimeSpan elapsed)
    {
        var milliseconds = elapsed.TotalMilliseconds;
        if (!double.IsFinite(milliseconds) || milliseconds <= 0.0)
        {
            return;
        }

        var previous = Volatile.Read(ref backendEwmaMilliseconds);
        var ewma = previous <= 0.0
            ? milliseconds
            : previous + smoothingFactor * (milliseconds - previous);
        var interval = Math.Clamp(
            ewma / targetUtilization,
            MinimumInterval.TotalMilliseconds,
            MaximumInterval.TotalMilliseconds);
        Volatile.Write(ref backendEwmaMilliseconds, ewma);
        Volatile.Write(ref currentIntervalMilliseconds, interval);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref lastScheduleTicks, 0);
        Volatile.Write(ref backendEwmaMilliseconds, 0.0);
        Volatile.Write(ref currentIntervalMilliseconds, MinimumInterval.TotalMilliseconds);
    }
}
