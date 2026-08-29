using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrReciprocityAnalyzer
{
    private const int ElectrodeCount = DemodulatedFrame.StimulationCount;
    private const int RetainedRelativeChannelMin = 2;
    private const int RetainedRelativeChannelMax = 14;

    public EcdCwrReciprocityResult Analyze(
        DemodulatedFrame frame,
        EcdCwrHealthCalibration calibration,
        DemodulatedFrame? previousFrame = null,
        EcdCwrReciprocityAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibration);
        options ??= new EcdCwrReciprocityAnalyzerOptions();
        ValidateFullComplexFrame(frame, nameof(frame));
        if (previousFrame is not null)
        {
            ValidateFullComplexFrame(previousFrame, nameof(previousFrame));
        }

        var frameDeltaRms = previousFrame is null
            ? 0.0
            : CalculateRetainedComplexDeltaRms(frame, previousFrame);
        var dynamicTooFast = frameDeltaRms > options.DynamicFrameDeltaRmsThreshold;
        var measurementScores = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var pairs = new List<EcdCwrReciprocityPairScore>(calibration.ReciprocalPairs.Count);

        foreach (var statistic in calibration.ReciprocalPairs)
        {
            if (!IsRetainedRelativeChannel(statistic.RelativeChannelIndex) ||
                !IsRetainedRelativeChannel(statistic.ReciprocalRelativeChannelIndex))
            {
                continue;
            }

            var left = ReadComplex(frame, statistic.StimulationIndex, statistic.RelativeChannelIndex);
            var right = ReadComplex(frame, statistic.ReciprocalStimulationIndex, statistic.ReciprocalRelativeChannelIndex);
            var gain = new ComplexValue(
                statistic.Sign * statistic.ComplexGainReal,
                statistic.Sign * statistic.ComplexGainImaginary);
            var reciprocalDifference = left - (gain * right);
            var baseline = new ComplexValue(statistic.MeanReal, statistic.MeanImaginary);
            var residual = reciprocalDifference - baseline;
            var scale = Math.Max(
                options.ResidualNoiseFloor,
                Math.Max(statistic.MagnitudeSigma, 1.4826 * statistic.MagnitudeMad));
            var whitenedScore = residual.Magnitude / scale;
            var normalizedError = reciprocalDifference.Magnitude /
                Math.Max(options.RelativeMagnitudeFloor, 0.5 * (left.Magnitude + right.Magnitude));
            var delayWeight = AdjacentReciprocalTiming.GetNearestWindowOffset(statistic.RelativeChannelIndex) /
                (ElectrodeCount / 2.0);
            var dynamicThreshold = options.BaseViolationThreshold +
                (options.DynamicThresholdGain * frameDeltaRms * delayWeight);
            var retainedIndex = RetainedIndex(statistic.StimulationIndex, statistic.RelativeChannelIndex);
            var reciprocalRetainedIndex = RetainedIndex(
                statistic.ReciprocalStimulationIndex,
                statistic.ReciprocalRelativeChannelIndex);

            measurementScores[retainedIndex] = Math.Max(measurementScores[retainedIndex], whitenedScore);
            measurementScores[reciprocalRetainedIndex] = Math.Max(
                measurementScores[reciprocalRetainedIndex],
                whitenedScore);
            pairs.Add(new EcdCwrReciprocityPairScore(
                statistic.StimulationIndex,
                statistic.RelativeChannelIndex,
                statistic.ReciprocalStimulationIndex,
                statistic.ReciprocalRelativeChannelIndex,
                retainedIndex,
                reciprocalRetainedIndex,
                normalizedError,
                whitenedScore,
                dynamicThreshold,
                whitenedScore > dynamicThreshold));
        }

        return new EcdCwrReciprocityResult(
            pairs,
            measurementScores,
            frameDeltaRms,
            dynamicTooFast,
            Median(pairs.Select(pair => pair.WhitenedScore).ToArray()),
            pairs.Count == 0 ? 0.0 : pairs.Max(pair => pair.WhitenedScore),
            pairs.Count(pair => pair.Violated));
    }

    private static void ValidateFullComplexFrame(DemodulatedFrame frame, string name)
    {
        if (frame.FullRealComponents is null || frame.FullImaginaryComponents is null)
        {
            throw new ArgumentException("Reciprocity evidence requires full 16x16 complex observations.", name);
        }

        ElectrodeContactBaseline.ValidateFullMatrix(frame.FullRealComponents, $"{name}.{nameof(frame.FullRealComponents)}");
        ElectrodeContactBaseline.ValidateFullMatrix(
            frame.FullImaginaryComponents,
            $"{name}.{nameof(frame.FullImaginaryComponents)}");
    }

    private static bool IsRetainedRelativeChannel(int relativeChannel)
    {
        return relativeChannel is >= RetainedRelativeChannelMin and <= RetainedRelativeChannelMax;
    }

    private static int RetainedIndex(int stimulationIndex, int relativeChannel)
    {
        return checked((stimulationIndex * DemodulatedFrame.MeasurementsPerStimulation) +
            (relativeChannel - RetainedRelativeChannelMin));
    }

    private static ComplexValue ReadComplex(DemodulatedFrame frame, int stimulationIndex, int relativeChannel)
    {
        return new ComplexValue(
            frame.FullRealComponents![stimulationIndex, relativeChannel],
            frame.FullImaginaryComponents![stimulationIndex, relativeChannel]);
    }

    private static double CalculateRetainedComplexDeltaRms(DemodulatedFrame current, DemodulatedFrame previous)
    {
        var sumSquares = 0.0;
        var count = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeChannel = RetainedRelativeChannelMin;
                 relativeChannel <= RetainedRelativeChannelMax;
                 relativeChannel++)
            {
                var delta = ReadComplex(current, stimulation, relativeChannel) -
                    ReadComplex(previous, stimulation, relativeChannel);
                sumSquares += delta.Magnitude * delta.Magnitude;
                count++;
            }
        }

        return count == 0 ? 0.0 : Math.Sqrt(sumSquares / count);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return 0.0;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 1
            ? finite[middle]
            : (finite[middle - 1] + finite[middle]) / 2.0;
    }

    private readonly record struct ComplexValue(double Real, double Imaginary)
    {
        public double Magnitude => Math.Sqrt((Real * Real) + (Imaginary * Imaginary));

        public static ComplexValue operator -(ComplexValue left, ComplexValue right)
        {
            return new ComplexValue(left.Real - right.Real, left.Imaginary - right.Imaginary);
        }

        public static ComplexValue operator *(ComplexValue left, ComplexValue right)
        {
            return new ComplexValue(
                (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
                (left.Real * right.Imaginary) + (left.Imaginary * right.Real));
        }
    }
}

public sealed record EcdCwrReciprocityAnalyzerOptions(
    double ResidualNoiseFloor = 1e-9,
    double RelativeMagnitudeFloor = 1e-12,
    double BaseViolationThreshold = 3.0,
    double DynamicFrameDeltaRmsThreshold = double.PositiveInfinity,
    double DynamicThresholdGain = 1.0);

public sealed record EcdCwrReciprocityResult(
    IReadOnlyList<EcdCwrReciprocityPairScore> PairScores,
    double[] MeasurementScores208,
    double FrameDeltaRms,
    bool DynamicTooFast,
    double MedianWhitenedScore,
    double MaxWhitenedScore,
    int ViolationCount);

public sealed record EcdCwrReciprocityPairScore(
    int StimulationIndex,
    int RelativeChannelIndex,
    int ReciprocalStimulationIndex,
    int ReciprocalRelativeChannelIndex,
    int RetainedIndex,
    int ReciprocalRetainedIndex,
    double NormalizedError,
    double WhitenedScore,
    double DynamicThreshold,
    bool Violated);
