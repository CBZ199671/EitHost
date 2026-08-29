namespace EitHost.Core.Hardware.Dds;

public enum DdsTimingConsistencyState
{
    Healthy,
    PendingMismatch,
    ConfirmedMismatch,
    Recovering
}

public sealed record DdsTimingConsistencyDecision(
    DdsTimingConsistencyState State,
    bool BlocksRealtimeProcessing,
    int ConsecutiveMismatches,
    int ConsecutiveMatches,
    bool JustConfirmed,
    bool JustRecovered);

public sealed class DdsTimingConsistencyMonitor
{
    public const int DefaultConfirmationCount = 3;

    private readonly int mismatchConfirmationCount;
    private readonly int recoveryConfirmationCount;
    private bool confirmed;
    private int consecutiveMismatches;
    private int consecutiveMatches;

    public DdsTimingConsistencyMonitor(
        int mismatchConfirmationCount = DefaultConfirmationCount,
        int recoveryConfirmationCount = DefaultConfirmationCount)
    {
        if (mismatchConfirmationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mismatchConfirmationCount));
        }

        if (recoveryConfirmationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryConfirmationCount));
        }

        this.mismatchConfirmationCount = mismatchConfirmationCount;
        this.recoveryConfirmationCount = recoveryConfirmationCount;
    }

    public DdsTimingConsistencyDecision Evaluate(
        DdsTimingValidationResult validation,
        bool evidenceTrusted = true)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (!evidenceTrusted)
        {
            return CurrentDecision();
        }

        if (!validation.IsMatch)
        {
            consecutiveMatches = 0;
            consecutiveMismatches++;
            var justConfirmed = !confirmed && consecutiveMismatches >= mismatchConfirmationCount;
            confirmed |= justConfirmed;
            return new DdsTimingConsistencyDecision(
                confirmed
                    ? DdsTimingConsistencyState.ConfirmedMismatch
                    : DdsTimingConsistencyState.PendingMismatch,
                confirmed,
                consecutiveMismatches,
                consecutiveMatches,
                justConfirmed,
                JustRecovered: false);
        }

        consecutiveMismatches = 0;
        if (!confirmed)
        {
            consecutiveMatches = 0;
            return HealthyDecision(justRecovered: false);
        }

        consecutiveMatches++;
        if (consecutiveMatches < recoveryConfirmationCount)
        {
            return new DdsTimingConsistencyDecision(
                DdsTimingConsistencyState.Recovering,
                BlocksRealtimeProcessing: true,
                consecutiveMismatches,
                consecutiveMatches,
                JustConfirmed: false,
                JustRecovered: false);
        }

        confirmed = false;
        consecutiveMatches = 0;
        return HealthyDecision(justRecovered: true);
    }

    public void Reset()
    {
        confirmed = false;
        consecutiveMismatches = 0;
        consecutiveMatches = 0;
    }

    private DdsTimingConsistencyDecision HealthyDecision(bool justRecovered)
    {
        return new DdsTimingConsistencyDecision(
            DdsTimingConsistencyState.Healthy,
            BlocksRealtimeProcessing: false,
            consecutiveMismatches,
            consecutiveMatches,
            JustConfirmed: false,
            justRecovered);
    }

    private DdsTimingConsistencyDecision CurrentDecision()
    {
        var state = confirmed
            ? consecutiveMatches > 0
                ? DdsTimingConsistencyState.Recovering
                : DdsTimingConsistencyState.ConfirmedMismatch
            : consecutiveMismatches > 0
                ? DdsTimingConsistencyState.PendingMismatch
                : DdsTimingConsistencyState.Healthy;
        return new DdsTimingConsistencyDecision(
            state,
            BlocksRealtimeProcessing: confirmed,
            consecutiveMismatches,
            consecutiveMatches,
            JustConfirmed: false,
            JustRecovered: false);
    }
}
