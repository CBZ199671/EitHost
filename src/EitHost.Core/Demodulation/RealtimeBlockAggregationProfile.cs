namespace EitHost.Core.Demodulation;

public enum RealtimeBlockAggregationMode
{
    Fast,
    Balanced,
    Stable,
    Tolerant,
    Custom
}

public sealed record RealtimeBlockAggregationProfile(
    RealtimeBlockAggregationMode Mode,
    int FramesPerBlock,
    int MinimumAcceptedFrames)
{
    private static readonly RealtimeBlockAggregationProfile[] PresetValues =
    [
        new(RealtimeBlockAggregationMode.Fast, 1, 1),
        new(RealtimeBlockAggregationMode.Balanced, 2, 2),
        new(RealtimeBlockAggregationMode.Stable, 3, 3),
        new(RealtimeBlockAggregationMode.Tolerant, 3, 2)
    ];

    public static IReadOnlyList<RealtimeBlockAggregationProfile> Presets => PresetValues;

    public string Code => Mode switch
    {
        RealtimeBlockAggregationMode.Fast => "fast",
        RealtimeBlockAggregationMode.Balanced => "balanced",
        RealtimeBlockAggregationMode.Stable => "stable",
        RealtimeBlockAggregationMode.Tolerant => "tolerant",
        _ => "custom"
    };

    public static RealtimeBlockAggregationProfile FromMode(RealtimeBlockAggregationMode mode)
    {
        return PresetValues.FirstOrDefault(profile => profile.Mode == mode)
            ?? throw new ArgumentOutOfRangeException(nameof(mode), "Custom mode has no fixed frame counts.");
    }

    public static RealtimeBlockAggregationProfile FromCode(string? code)
    {
        var normalized = code?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "fast" => FromMode(RealtimeBlockAggregationMode.Fast),
            "balanced" => FromMode(RealtimeBlockAggregationMode.Balanced),
            "stable" => FromMode(RealtimeBlockAggregationMode.Stable),
            "tolerant" => FromMode(RealtimeBlockAggregationMode.Tolerant),
            _ => throw new ArgumentException($"Unknown realtime block mode: {code}", nameof(code))
        };
    }

    public static RealtimeBlockAggregationProfile Resolve(int framesPerBlock, int minimumAcceptedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumAcceptedFrames);
        if (minimumAcceptedFrames > framesPerBlock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumAcceptedFrames),
                "Minimum accepted frames cannot exceed frames per block.");
        }

        return PresetValues.FirstOrDefault(profile =>
                profile.FramesPerBlock == framesPerBlock &&
                profile.MinimumAcceptedFrames == minimumAcceptedFrames)
            ?? new RealtimeBlockAggregationProfile(
                RealtimeBlockAggregationMode.Custom,
                framesPerBlock,
                minimumAcceptedFrames);
    }

    public double EstimateAcquisitionLatencyMilliseconds(
        double excitationFrequencyHz,
        double channelCycles,
        int windowsPerFrame = DemodulatedFrame.StimulationCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(excitationFrequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowsPerFrame);
        return FramesPerBlock * windowsPerFrame * channelCycles * 1000.0 / excitationFrequencyHz;
    }
}
