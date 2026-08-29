namespace EitHost.Core.Demodulation;

public enum DemodulationDiscardMode
{
    Manual = 0,
    AutomaticEightPercent = 1
}

public readonly record struct DemodulationWindowDiscard(
    DemodulationDiscardMode Mode,
    int LeadingSamples,
    int TrailingSamples,
    double LeadingCycles,
    double TrailingCycles);
