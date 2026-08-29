using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Demodulation;

public sealed record OfflineDemodulationSettings
{
    public OfflineDemodulationSettings(
        double sampleRateHz,
        double excitationFrequencyHz,
        int windowsPerFrame = 16,
        int trimSamples = 0,
        int detectionChannelIndex = 5,
        int minRegionWidth = 100,
        double peakRatio = 0.8,
        int maxFrames = 30,
        double minPeakToBackgroundRatio = 2.0,
        double channelCycles = 10.0,
        bool forceUniformCadence = false,
        IReadOnlyList<int>? peakLocationsOverride = null,
        bool includeCorrectedFramesInAverage = false,
        double discardLeadingCycles = 0.0,
        double discardTrailingCycles = 0.0,
        IReadOnlyList<double>? interferenceFrequencyHz = null,
        int maxDegreeOfParallelism = 1,
        double? uniformWindowSamplesOverride = null,
        Usb2070AdRange adRange = Usb2070AdRange.Bipolar5V,
        DemodulationDiscardMode discardMode = DemodulationDiscardMode.Manual)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(excitationFrequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowsPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(trimSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(detectionChannelIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minRegionWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peakRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minPeakToBackgroundRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(discardLeadingCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(discardTrailingCycles);
        if (discardLeadingCycles + discardTrailingCycles >= channelCycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discardLeadingCycles),
                "Discarded cycles must leave at least one usable excitation cycle.");
        }

        if (maxDegreeOfParallelism < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism),
                "Max degree of parallelism must be zero for automatic mode or a positive value.");
        }

        if (uniformWindowSamplesOverride is { } lockedWindowSamples &&
            (!double.IsFinite(lockedWindowSamples) || lockedWindowSamples <= 1.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(uniformWindowSamplesOverride),
                "Uniform window-sample override must be finite and greater than one.");
        }

        if (!Enum.IsDefined(adRange))
        {
            throw new ArgumentOutOfRangeException(nameof(adRange));
        }

        if (!Enum.IsDefined(discardMode))
        {
            throw new ArgumentOutOfRangeException(nameof(discardMode));
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
        WindowsPerFrame = windowsPerFrame;
        TrimSamples = trimSamples;
        DetectionChannelIndex = detectionChannelIndex;
        MinRegionWidth = minRegionWidth;
        PeakRatio = peakRatio;
        MaxFrames = maxFrames;
        MinPeakToBackgroundRatio = minPeakToBackgroundRatio;
        ChannelCycles = channelCycles;
        ForceUniformCadence = forceUniformCadence;
        PeakLocationsOverride = peakLocationsOverride?.ToArray();
        IncludeCorrectedFramesInAverage = includeCorrectedFramesInAverage;
        DiscardLeadingCycles = discardLeadingCycles;
        DiscardTrailingCycles = discardTrailingCycles;
        InterferenceFrequencyHz = interferenceFrequencies;
        MaxDegreeOfParallelism = maxDegreeOfParallelism == 0
            ? ResolveAutomaticMaxDegreeOfParallelism(maxFrames)
            : maxDegreeOfParallelism;
        UniformWindowSamplesOverride = uniformWindowSamplesOverride;
        AdRange = adRange;
        DiscardMode = discardMode;
    }

    public double SampleRateHz { get; }

    public double ExcitationFrequencyHz { get; }

    public int WindowsPerFrame { get; }

    public int TrimSamples { get; }

    public int DetectionChannelIndex { get; }

    public int MinRegionWidth { get; }

    public double PeakRatio { get; }

    public int MaxFrames { get; }

    public double MinPeakToBackgroundRatio { get; }

    public double ChannelCycles { get; }

    public bool ForceUniformCadence { get; }

    public IReadOnlyList<int>? PeakLocationsOverride { get; }

    public bool IncludeCorrectedFramesInAverage { get; }

    public double DiscardLeadingCycles { get; }

    public double DiscardTrailingCycles { get; }

    public IReadOnlyList<double> InterferenceFrequencyHz { get; }

    public int MaxDegreeOfParallelism { get; }

    public double? UniformWindowSamplesOverride { get; }

    public Usb2070AdRange AdRange { get; }

    public DemodulationDiscardMode DiscardMode { get; }

    public double AdcFullSpanVolts => Usb2070VoltageScale.GetFullSpanVolts(AdRange);

    public double AdcLsbVolts => Usb2070VoltageScale.GetLsbVolts(AdRange);

    public DemodulationWindowDiscard ResolveWindowDiscard(double windowSamples, int segmentLength)
    {
        if (!double.IsFinite(windowSamples) || windowSamples <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSamples));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(segmentLength);
        var samplesPerCycle = windowSamples / ChannelCycles;
        var leadingFromCycles = (int)Math.Round(DiscardLeadingCycles * samplesPerCycle);
        var trailingFromCycles = (int)Math.Round(DiscardTrailingCycles * samplesPerCycle);
        var automaticTrim = DiscardMode == DemodulationDiscardMode.AutomaticEightPercent
            ? (int)Math.Round(0.08 * windowSamples)
            : 0;
        var leading = Math.Max(TrimSamples, Math.Max(automaticTrim, leadingFromCycles));
        var trailing = Math.Max(TrimSamples, Math.Max(automaticTrim, trailingFromCycles));
        var maxDiscardTotal = Math.Max(0, segmentLength - 2);

        if (leading + trailing > maxDiscardTotal)
        {
            var total = leading + trailing;
            if (total <= 0 || maxDiscardTotal <= 0)
            {
                leading = 0;
                trailing = 0;
            }
            else
            {
                leading = (int)Math.Floor((double)leading / total * maxDiscardTotal);
                trailing = maxDiscardTotal - leading;
            }
        }

        return new DemodulationWindowDiscard(
            DiscardMode,
            leading,
            trailing,
            leading / samplesPerCycle,
            trailing / samplesPerCycle);
    }

    private static int ResolveAutomaticMaxDegreeOfParallelism(int maxFrames)
    {
        if (maxFrames < 16)
        {
            return 1;
        }

        var workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        return Math.Min(workerCount, maxFrames);
    }
}
