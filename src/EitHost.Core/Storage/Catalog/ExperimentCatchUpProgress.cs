namespace EitHost.Core.Storage.Catalog;

public enum ExperimentCatchUpPhase
{
    Demodulating = 0,
    Reconstructing = 1
}

/// <summary>
/// Progress for offline catch-up, which runs after every stopped experiment and can take minutes
/// on a long run. Units are raw segments while demodulating and processing blocks while
/// reconstructing; totals are known before the pass starts.
/// </summary>
public sealed record ExperimentCatchUpProgress(
    Guid ExperimentRunId,
    ExperimentCatchUpPhase Phase,
    int CompletedUnits,
    int TotalUnits)
{
    public double CompletedFraction => TotalUnits <= 0
        ? 0.0
        : Math.Clamp((double)CompletedUnits / TotalUnits, 0.0, 1.0);
}
