using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed record DemodulationParameterReplayCase(
    string Label,
    DemodulationDiscardMode DiscardMode,
    double DiscardLeadingCycles,
    double DiscardTrailingCycles,
    int FramesPerBlock,
    int MinimumAcceptedFrames);

public sealed record DemodulationParameterReplayCaseReport(
    string Label,
    DemodulationDiscardMode DiscardMode,
    double ConfiguredDiscardLeadingCycles,
    double ConfiguredDiscardTrailingCycles,
    int EffectiveDiscardLeadingSamples,
    int EffectiveDiscardTrailingSamples,
    double EffectiveDiscardLeadingCycles,
    double EffectiveDiscardTrailingCycles,
    int FramesPerBlock,
    int MinimumAcceptedFrames,
    double EstimatedBlockLatencyMilliseconds,
    double EstimatedOutputRateHz,
    int BlockCount,
    int HighQualityBlockCount,
    int FiniteMeanBlockCount,
    int IntegrationStableBlockCount,
    int AcceptedFrameCount,
    int RejectedFrameCount,
    double QualityWeightP50,
    double IntegrationInstabilityP50,
    double IntegrationInstabilityP95,
    double? MeanVectorNrmsePercent,
    double? MedianChannelAbsolutePercentDifference,
    RealtimeVoltageNoiseSummary? VoltageNoise,
    RealtimeDemodulationStabilitySummary? DemodulationStability);

public sealed record DemodulationParameterReplayReport(
    DateTimeOffset GeneratedAt,
    int SampleRows,
    int ChannelCount,
    double SampleRateHz,
    double ExcitationFrequencyHz,
    double ChannelCycles,
    Usb2070AdRange AdRange,
    IReadOnlyList<DemodulationParameterReplayCaseReport> Cases);

public static class DemodulationParameterReplayRunner
{
    private const int DefaultChunkRows = 4096;

    public static DemodulationParameterReplayReport Run(
        ushort[,] rawAdcCounts,
        double sampleRateHz,
        double excitationFrequencyHz,
        double channelCycles,
        IReadOnlyList<DemodulationParameterReplayCase> cases,
        Usb2070AdRange adRange = Usb2070AdRange.Bipolar5V,
        int chunkRows = DefaultChunkRows)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(excitationFrequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkRows);
        if (rawAdcCounts.GetLength(1) != DemodulatedFrame.StimulationCount)
        {
            throw new ArgumentException("Replay input must be shaped [sample,16].", nameof(rawAdcCounts));
        }

        if (cases.Count == 0)
        {
            throw new ArgumentException("Replay requires at least one parameter case.", nameof(cases));
        }

        var drafts = cases
            .Select(item => RunCase(
                rawAdcCounts,
                sampleRateHz,
                excitationFrequencyHz,
                channelCycles,
                item,
                adRange,
                chunkRows))
            .ToArray();
        var referenceVector = drafts.FirstOrDefault(item => item.MedianAmplitude208 is not null)?.MedianAmplitude208;
        var reports = drafts
            .Select(draft => ToReport(draft, referenceVector))
            .ToArray();
        return new DemodulationParameterReplayReport(
            DateTimeOffset.Now,
            rawAdcCounts.GetLength(0),
            rawAdcCounts.GetLength(1),
            sampleRateHz,
            excitationFrequencyHz,
            channelCycles,
            adRange,
            reports);
    }

    private static ReplayCaseDraft RunCase(
        ushort[,] rawAdcCounts,
        double sampleRateHz,
        double excitationFrequencyHz,
        double channelCycles,
        DemodulationParameterReplayCase replayCase,
        Usb2070AdRange adRange,
        int chunkRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayCase.Label);
        var settings = new RealtimeDemodulationSettings(
            sampleRateHz,
            excitationFrequencyHz,
            channelCycles: channelCycles,
            framesPerBlock: replayCase.FramesPerBlock,
            minimumAcceptedFrames: replayCase.MinimumAcceptedFrames,
            relockIntervalBlocks: int.MaxValue,
            discardLeadingCycles: replayCase.DiscardLeadingCycles,
            discardTrailingCycles: replayCase.DiscardTrailingCycles,
            discardMode: replayCase.DiscardMode,
            adRange: adRange);
        var demodulator = new RealtimeBlockDemodulator(settings);
        var blocks = new List<RealtimeDemodulatedBlock>();
        for (var offset = 0; offset < rawAdcCounts.GetLength(0); offset += chunkRows)
        {
            var count = Math.Min(chunkRows, rawAdcCounts.GetLength(0) - offset);
            var chunk = new ushort[count, DemodulatedFrame.StimulationCount];
            Buffer.BlockCopy(
                rawAdcCounts,
                offset * DemodulatedFrame.StimulationCount * sizeof(ushort),
                chunk,
                0,
                count * DemodulatedFrame.StimulationCount * sizeof(ushort));
            demodulator.AppendSamples(chunk);
            blocks.AddRange(demodulator.ProcessAvailableBlocks());
        }

        blocks.AddRange(demodulator.ProcessAvailableBlocks());
        var effectiveWindowSamples = demodulator.LockedWindowSamples ?? settings.NominalWindowSamples;
        var effectiveDiscard = settings
            .ToOfflineSettings()
            .ResolveWindowDiscard(
                effectiveWindowSamples,
                Math.Max(0, (int)Math.Round(effectiveWindowSamples)));
        var finiteBlocks = blocks
            .Where(block =>
                block.MeanAmplitude208.Count() == DemodulatedFrame.FlattenedMeasurementCount &&
                block.MeanAmplitude208.All(double.IsFinite))
            .ToArray();
        var medianVector = finiteBlocks.Length == 0
            ? null
            : Enumerable.Range(0, DemodulatedFrame.FlattenedMeasurementCount)
                .Select(index => Quantile(finiteBlocks.Select(block => block.MeanAmplitude208.ElementAt(index)), 0.5))
                .ToArray();
        var qualityWeights = blocks.Select(block => block.QualityWeight).ToArray();
        var instabilities = blocks.Select(block => block.UniformIntegrationInstability).ToArray();
        var blockLatencyMilliseconds = settings.NominalFrameSamples * settings.FramesPerBlock /
            settings.SampleRateHz * 1000.0;
        return new ReplayCaseDraft(
            replayCase,
            effectiveDiscard,
            blockLatencyMilliseconds,
            blocks,
            finiteBlocks.Length,
            qualityWeights.Length == 0 ? double.NaN : Quantile(qualityWeights, 0.5),
            instabilities.Length == 0 ? double.NaN : Quantile(instabilities, 0.5),
            instabilities.Length == 0 ? double.NaN : Quantile(instabilities, 0.95),
            medianVector,
            RealtimeVoltageNoiseAnalyzer.Analyze(blocks),
            RealtimeDemodulationStabilityAnalyzer.Analyze(blocks));
    }

    private static DemodulationParameterReplayCaseReport ToReport(
        ReplayCaseDraft draft,
        IReadOnlyList<double>? referenceVector)
    {
        var (nrmse, medianAbsolutePercent) = CompareVectors(draft.MedianAmplitude208, referenceVector);
        return new DemodulationParameterReplayCaseReport(
            draft.Case.Label,
            draft.Case.DiscardMode,
            draft.Case.DiscardLeadingCycles,
            draft.Case.DiscardTrailingCycles,
            draft.EffectiveDiscard.LeadingSamples,
            draft.EffectiveDiscard.TrailingSamples,
            draft.EffectiveDiscard.LeadingCycles,
            draft.EffectiveDiscard.TrailingCycles,
            draft.Case.FramesPerBlock,
            draft.Case.MinimumAcceptedFrames,
            draft.BlockLatencyMilliseconds,
            draft.BlockLatencyMilliseconds <= 0.0 ? 0.0 : 1000.0 / draft.BlockLatencyMilliseconds,
            draft.Blocks.Count,
            draft.Blocks.Count(block => block.IsHighQuality),
            draft.FiniteMeanBlockCount,
            draft.Blocks.Count(block => block.UniformIntegrationStable),
            draft.Blocks.Sum(block => block.AcceptedFrameCount),
            draft.Blocks.Sum(block => block.RejectedFrameCount),
            draft.QualityWeightP50,
            draft.IntegrationInstabilityP50,
            draft.IntegrationInstabilityP95,
            nrmse,
            medianAbsolutePercent,
            draft.VoltageNoise,
            draft.DemodulationStability);
    }

    private static (double? NrmsePercent, double? MedianAbsolutePercent) CompareVectors(
        IReadOnlyList<double>? value,
        IReadOnlyList<double>? reference)
    {
        if (value is null || reference is null || value.Count != reference.Count || value.Count == 0)
        {
            return (null, null);
        }

        var squaredDifference = 0.0;
        var squaredReference = 0.0;
        var percentages = new List<double>(value.Count);
        for (var index = 0; index < value.Count; index++)
        {
            var difference = value[index] - reference[index];
            squaredDifference += difference * difference;
            squaredReference += reference[index] * reference[index];
            if (Math.Abs(reference[index]) > double.Epsilon)
            {
                percentages.Add(Math.Abs(difference / reference[index]) * 100.0);
            }
        }

        double? nrmse = squaredReference <= double.Epsilon
            ? null
            : 100.0 * Math.Sqrt(squaredDifference / squaredReference);
        double? median = percentages.Count == 0 ? null : Quantile(percentages, 0.5);
        return (nrmse, median);
    }

    private static double Quantile(IEnumerable<double> values, double probability)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = probability * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private sealed record ReplayCaseDraft(
        DemodulationParameterReplayCase Case,
        DemodulationWindowDiscard EffectiveDiscard,
        double BlockLatencyMilliseconds,
        IReadOnlyList<RealtimeDemodulatedBlock> Blocks,
        int FiniteMeanBlockCount,
        double QualityWeightP50,
        double IntegrationInstabilityP50,
        double IntegrationInstabilityP95,
        double[]? MedianAmplitude208,
        RealtimeVoltageNoiseSummary? VoltageNoise,
        RealtimeDemodulationStabilitySummary? DemodulationStability);
}
