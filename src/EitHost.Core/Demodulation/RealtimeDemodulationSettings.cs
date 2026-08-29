using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Demodulation;

public sealed record RealtimeDemodulationSettings
{
    public RealtimeDemodulationSettings(
        double sampleRateHz,
        double excitationFrequencyHz,
        double channelCycles = 10.0,
        int windowsPerFrame = DemodulatedFrame.StimulationCount,
        int framesPerBlock = 3,
        int minimumAcceptedFrames = 3,
        int searchExtraFrames = 2,
        int relockIntervalBlocks = 3,
        int trimSamples = 0,
        double discardLeadingCycles = 0.0,
        double discardTrailingCycles = 0.0,
        double minPeakToBackgroundRatio = 2.0,
        IReadOnlyList<double>? interferenceFrequencyHz = null,
        int maxDegreeOfParallelism = 0,
        Usb2070AdRange adRange = Usb2070AdRange.Bipolar5V,
        DemodulationDiscardMode discardMode = DemodulationDiscardMode.Manual)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(excitationFrequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowsPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerBlock);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumAcceptedFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(searchExtraFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relockIntervalBlocks);
        ArgumentOutOfRangeException.ThrowIfNegative(trimSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(discardLeadingCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(discardTrailingCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minPeakToBackgroundRatio);
        if (maxDegreeOfParallelism < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism),
                "Max degree of parallelism must be zero for automatic mode or a positive value.");
        }

        if (minimumAcceptedFrames > framesPerBlock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumAcceptedFrames),
                "Minimum accepted frames cannot exceed frames per block.");
        }

        if (!Enum.IsDefined(discardMode))
        {
            throw new ArgumentOutOfRangeException(nameof(discardMode));
        }

        if (discardLeadingCycles + discardTrailingCycles >= channelCycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discardLeadingCycles),
                "Discarded cycles must leave at least one usable excitation cycle.");
        }

        var interferenceFrequencies = (interferenceFrequencyHz ?? [])
            .Where(frequency => Math.Abs(frequency - excitationFrequencyHz) > 1e-9)
            .Distinct()
            .ToArray();
        if (interferenceFrequencies.Any(frequency => !double.IsFinite(frequency) || frequency <= 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interferenceFrequencyHz),
                "Interference frequencies must be finite positive values.");
        }

        SampleRateHz = sampleRateHz;
        ExcitationFrequencyHz = excitationFrequencyHz;
        ChannelCycles = channelCycles;
        WindowsPerFrame = windowsPerFrame;
        FramesPerBlock = framesPerBlock;
        MinimumAcceptedFrames = minimumAcceptedFrames;
        SearchExtraFrames = searchExtraFrames;
        RelockIntervalBlocks = relockIntervalBlocks;
        TrimSamples = trimSamples;
        DiscardLeadingCycles = discardLeadingCycles;
        DiscardTrailingCycles = discardTrailingCycles;
        MinPeakToBackgroundRatio = minPeakToBackgroundRatio;
        InterferenceFrequencyHz = interferenceFrequencies;
        MaxDegreeOfParallelism = maxDegreeOfParallelism;
        AdRange = adRange;
        DiscardMode = discardMode;
    }

    public double SampleRateHz { get; }

    public double ExcitationFrequencyHz { get; }

    public double ChannelCycles { get; }

    public int WindowsPerFrame { get; }

    public int FramesPerBlock { get; }

    public int MinimumAcceptedFrames { get; }

    public int SearchExtraFrames { get; }

    public int RelockIntervalBlocks { get; }

    public int TrimSamples { get; }

    public double DiscardLeadingCycles { get; }

    public double DiscardTrailingCycles { get; }

    public double MinPeakToBackgroundRatio { get; }

    public IReadOnlyList<double> InterferenceFrequencyHz { get; }

    public int MaxDegreeOfParallelism { get; }

    public Usb2070AdRange AdRange { get; }

    public DemodulationDiscardMode DiscardMode { get; }

    public RealtimeBlockAggregationProfile BlockAggregation =>
        RealtimeBlockAggregationProfile.Resolve(FramesPerBlock, MinimumAcceptedFrames);

    public double EstimatedBlockLatencyMilliseconds =>
        BlockAggregation.EstimateAcquisitionLatencyMilliseconds(
            ExcitationFrequencyHz,
            ChannelCycles,
            WindowsPerFrame);

    public double NominalWindowSamples => SampleRateHz / ExcitationFrequencyHz * ChannelCycles;

    public double NominalFrameSamples => NominalWindowSamples * WindowsPerFrame;

    public double StabilizeLockedWindowSamples(double estimatedWindowSamples)
    {
        if (!double.IsFinite(estimatedWindowSamples) || estimatedWindowSamples <= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimatedWindowSamples),
                "Estimated window samples must be finite and greater than one.");
        }

        var nominalSnapTolerance = Math.Max(0.5, NominalWindowSamples * 0.005);
        return Math.Abs(estimatedWindowSamples - NominalWindowSamples) <= nominalSnapTolerance
            ? NominalWindowSamples
            : estimatedWindowSamples;
    }

    public bool CanApplyBackgroundCadenceRefresh(
        double lockedWindowSamples,
        double estimatedWindowSamples)
    {
        var stabilizedLock = StabilizeLockedWindowSamples(lockedWindowSamples);
        var stabilizedCandidate = StabilizeLockedWindowSamples(estimatedWindowSamples);
        var toleranceSamples = Math.Max(1.0, stabilizedLock * 0.005);
        return Math.Abs(stabilizedCandidate - stabilizedLock) <= toleranceSamples;
    }

    public double SelectRelockedWindowSamples(
        double? currentLockSamples,
        double estimatedWindowSamples,
        bool highQuality)
    {
        var stabilizedCandidate = StabilizeLockedWindowSamples(estimatedWindowSamples);
        if (highQuality)
        {
            return stabilizedCandidate;
        }

        return currentLockSamples is { } current
            ? StabilizeLockedWindowSamples(current)
            : NominalWindowSamples;
    }

    public int RequiredBufferedSamples =>
        Math.Max(1, (int)Math.Ceiling(NominalFrameSamples * (FramesPerBlock + SearchExtraFrames)));

    public DemodulationWindowDiscard ResolveNominalWindowDiscard()
    {
        var segmentLength = Math.Max(2, (int)Math.Round(NominalWindowSamples));
        return ToOfflineSettings().ResolveWindowDiscard(NominalWindowSamples, segmentLength);
    }

    public OfflineDemodulationSettings ToOfflineSettings()
    {
        return new OfflineDemodulationSettings(
            SampleRateHz,
            ExcitationFrequencyHz,
            windowsPerFrame: WindowsPerFrame,
            trimSamples: TrimSamples,
            maxFrames: FramesPerBlock,
            minPeakToBackgroundRatio: MinPeakToBackgroundRatio,
            channelCycles: ChannelCycles,
            forceUniformCadence: true,
            includeCorrectedFramesInAverage: true,
            discardLeadingCycles: DiscardLeadingCycles,
            discardTrailingCycles: DiscardTrailingCycles,
            interferenceFrequencyHz: InterferenceFrequencyHz,
            maxDegreeOfParallelism: MaxDegreeOfParallelism,
            adRange: AdRange,
            discardMode: DiscardMode);
    }

    public OfflineDemodulationSettings ToOfflineSettingsWithPeakLocations(IReadOnlyList<int> peakLocations)
    {
        return new OfflineDemodulationSettings(
            SampleRateHz,
            ExcitationFrequencyHz,
            windowsPerFrame: WindowsPerFrame,
            trimSamples: TrimSamples,
            maxFrames: FramesPerBlock,
            minPeakToBackgroundRatio: MinPeakToBackgroundRatio,
            channelCycles: ChannelCycles,
            forceUniformCadence: false,
            peakLocationsOverride: peakLocations,
            includeCorrectedFramesInAverage: true,
            discardLeadingCycles: DiscardLeadingCycles,
            discardTrailingCycles: DiscardTrailingCycles,
            interferenceFrequencyHz: InterferenceFrequencyHz,
            maxDegreeOfParallelism: MaxDegreeOfParallelism,
            adRange: AdRange,
            discardMode: DiscardMode);
    }

    public OfflineDemodulationSettings ToOfflineSettingsWithLockedWindowSamples(double lockedWindowSamples)
    {
        return new OfflineDemodulationSettings(
            SampleRateHz,
            ExcitationFrequencyHz,
            windowsPerFrame: WindowsPerFrame,
            trimSamples: TrimSamples,
            maxFrames: FramesPerBlock,
            minPeakToBackgroundRatio: MinPeakToBackgroundRatio,
            channelCycles: ChannelCycles,
            forceUniformCadence: true,
            includeCorrectedFramesInAverage: true,
            discardLeadingCycles: DiscardLeadingCycles,
            discardTrailingCycles: DiscardTrailingCycles,
            interferenceFrequencyHz: InterferenceFrequencyHz,
            maxDegreeOfParallelism: MaxDegreeOfParallelism,
            uniformWindowSamplesOverride: StabilizeLockedWindowSamples(lockedWindowSamples),
            adRange: AdRange,
            discardMode: DiscardMode);
    }
}
