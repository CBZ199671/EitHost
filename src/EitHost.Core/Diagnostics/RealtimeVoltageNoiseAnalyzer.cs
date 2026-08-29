using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.Core.Diagnostics;

public sealed record RealtimeVoltageNoiseSummary(
    int UsableBlockCount,
    int ReferenceBlockCount,
    double DeltaRmsP50Volts,
    double DeltaRmsP95Volts,
    double DeltaRmsP99Volts,
    double DeltaRmsMaximumVolts,
    double ChannelRobustSigmaP50Volts,
    double ChannelRobustSigmaP90Volts,
    double ChannelRobustSigmaP99Volts,
    double ChannelRobustSigmaMaximumVolts);

public static class RealtimeVoltageNoiseAnalyzer
{
    public static RealtimeVoltageNoiseSummary? Analyze(
        IReadOnlyList<RealtimeDemodulatedBlock> blocks,
        int maximumReferenceBlocks = 50)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReferenceBlocks);
        var usable = blocks
            .Where(block =>
                block.IsHighQuality &&
                block.MeanAmplitude208.Count() == DemodulatedFrame.FlattenedMeasurementCount &&
                block.MeanAmplitude208.All(double.IsFinite))
            .Select(block => block.MeanAmplitude208.ToArray())
            .ToArray();
        if (usable.Length < 3)
        {
            return null;
        }

        var referenceBlockCount = Math.Min(
            maximumReferenceBlocks,
            Math.Max(2, usable.Length / 3));
        var referenceFrames = usable.Take(referenceBlockCount).ToArray();
        var noiseModel = new EcdCwrBoundaryNoiseModelBuilder().Create(referenceFrames);
        var deltaRms = usable
            .Skip(referenceBlockCount)
            .Select(vector => CalculateRmsDifference(vector, noiseModel.CenterVoltage208))
            .ToArray();
        if (deltaRms.Length == 0)
        {
            deltaRms = referenceFrames
                .Select(vector => CalculateRmsDifference(vector, noiseModel.CenterVoltage208))
                .ToArray();
        }

        return new RealtimeVoltageNoiseSummary(
            usable.Length,
            referenceBlockCount,
            Quantile(deltaRms, 0.50),
            Quantile(deltaRms, 0.95),
            Quantile(deltaRms, 0.99),
            deltaRms.Max(),
            Quantile(noiseModel.RobustScale208, 0.50),
            Quantile(noiseModel.RobustScale208, 0.90),
            Quantile(noiseModel.RobustScale208, 0.99),
            noiseModel.RobustScale208.Max());
    }

    private static double CalculateRmsDifference(
        IReadOnlyList<double> vector,
        IReadOnlyList<double> center)
    {
        var sum = 0.0;
        for (var index = 0; index < vector.Count; index++)
        {
            var difference = vector[index] - center[index];
            sum += difference * difference;
        }

        return Math.Sqrt(sum / vector.Count);
    }

    private static double Quantile(IReadOnlyList<double> values, double probability)
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
}
