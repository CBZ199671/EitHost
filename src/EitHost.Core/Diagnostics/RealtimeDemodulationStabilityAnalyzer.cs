using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics;

public sealed record RealtimeDemodulationStepStability(
    double RealCommonScaleRatio,
    double RealCommonScaleDeltaPercent,
    double ComplexScaleRatio,
    double ComplexScaleDeltaPercent,
    double ComplexPhaseDeltaDegrees,
    double RealShapeResidualPercent);

public sealed record RealtimeDemodulationStabilitySummary(
    int UsableBlockCount,
    int ConsecutiveStepCount,
    double RealCommonScaleRangePercent,
    double RealCommonScaleStepP50Percent,
    double RealCommonScaleStepP95Percent,
    double RealCommonScaleStepP99Percent,
    double RealCommonScaleStepMaximumPercent,
    double RealShapeResidualP50Percent,
    double RealShapeResidualP95Percent,
    double RealShapeResidualP99Percent,
    double RealShapeResidualMaximumPercent,
    double ComplexPhaseStepP50Degrees,
    double ComplexPhaseStepP95Degrees,
    double ComplexPhaseStepP99Degrees,
    double ComplexPhaseStepMaximumDegrees);

public static class RealtimeDemodulationStabilityAnalyzer
{
    public static RealtimeDemodulationStepStability? AnalyzeStep(
        IReadOnlyList<double> previousReal,
        IReadOnlyList<double> previousImaginary,
        IReadOnlyList<double> currentReal,
        IReadOnlyList<double> currentImaginary)
    {
        ArgumentNullException.ThrowIfNull(previousReal);
        ArgumentNullException.ThrowIfNull(previousImaginary);
        ArgumentNullException.ThrowIfNull(currentReal);
        ArgumentNullException.ThrowIfNull(currentImaginary);
        if (previousReal.Count == 0 ||
            previousReal.Count != previousImaginary.Count ||
            previousReal.Count != currentReal.Count ||
            previousReal.Count != currentImaginary.Count ||
            !AllFinite(previousReal) ||
            !AllFinite(previousImaginary) ||
            !AllFinite(currentReal) ||
            !AllFinite(currentImaginary))
        {
            return null;
        }

        var realDenominator = 0.0;
        var realDot = 0.0;
        var complexDenominator = 0.0;
        var complexReal = 0.0;
        var complexImaginary = 0.0;
        for (var index = 0; index < previousReal.Count; index++)
        {
            var previousRe = previousReal[index];
            var previousIm = previousImaginary[index];
            var currentRe = currentReal[index];
            var currentIm = currentImaginary[index];
            realDenominator += previousRe * previousRe;
            realDot += currentRe * previousRe;
            complexDenominator += previousRe * previousRe + previousIm * previousIm;
            complexReal += currentRe * previousRe + currentIm * previousIm;
            complexImaginary += currentIm * previousRe - currentRe * previousIm;
        }

        if (realDenominator <= double.Epsilon || complexDenominator <= double.Epsilon)
        {
            return null;
        }

        var realScale = realDot / realDenominator;
        var residualSquared = 0.0;
        var scaledPreviousSquared = 0.0;
        for (var index = 0; index < previousReal.Count; index++)
        {
            var scaledPrevious = realScale * previousReal[index];
            var residual = currentReal[index] - scaledPrevious;
            residualSquared += residual * residual;
            scaledPreviousSquared += scaledPrevious * scaledPrevious;
        }

        complexReal /= complexDenominator;
        complexImaginary /= complexDenominator;
        var complexScale = Math.Sqrt(complexReal * complexReal + complexImaginary * complexImaginary);
        return new RealtimeDemodulationStepStability(
            realScale,
            100.0 * (realScale - 1.0),
            complexScale,
            100.0 * (complexScale - 1.0),
            180.0 * Math.Atan2(complexImaginary, complexReal) / Math.PI,
            scaledPreviousSquared <= double.Epsilon
                ? 0.0
                : 100.0 * Math.Sqrt(residualSquared / scaledPreviousSquared));
    }

    public static RealtimeDemodulationStabilitySummary? Analyze(
        IReadOnlyList<RealtimeDemodulatedBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var usable = blocks
            .Where(IsUsable)
            .OrderBy(block => block.BlockNumber)
            .ToArray();
        if (usable.Length < 2)
        {
            return null;
        }

        var referenceReal = usable[0].MeanReal208;
        var referenceDenominator = referenceReal.Sum(value => value * value);
        if (referenceDenominator <= double.Epsilon)
        {
            return null;
        }

        var referenceScales = usable
            .Select(block => block.MeanReal208.Zip(referenceReal, (current, reference) => current * reference).Sum() /
                referenceDenominator)
            .ToArray();
        var scaleSteps = new List<double>();
        var shapeSteps = new List<double>();
        var phaseSteps = new List<double>();
        for (var index = 1; index < usable.Length; index++)
        {
            if (usable[index].BlockNumber != usable[index - 1].BlockNumber + 1)
            {
                continue;
            }

            var step = AnalyzeStep(
                usable[index - 1].MeanReal208,
                usable[index - 1].MeanImaginary208,
                usable[index].MeanReal208,
                usable[index].MeanImaginary208);
            if (step is null)
            {
                continue;
            }

            scaleSteps.Add(Math.Abs(step.RealCommonScaleDeltaPercent));
            shapeSteps.Add(step.RealShapeResidualPercent);
            phaseSteps.Add(Math.Abs(step.ComplexPhaseDeltaDegrees));
        }

        if (scaleSteps.Count == 0)
        {
            return null;
        }

        return new RealtimeDemodulationStabilitySummary(
            usable.Length,
            scaleSteps.Count,
            100.0 * (referenceScales.Max() - referenceScales.Min()),
            Percentile(scaleSteps, 0.50),
            Percentile(scaleSteps, 0.95),
            Percentile(scaleSteps, 0.99),
            scaleSteps.Max(),
            Percentile(shapeSteps, 0.50),
            Percentile(shapeSteps, 0.95),
            Percentile(shapeSteps, 0.99),
            shapeSteps.Max(),
            Percentile(phaseSteps, 0.50),
            Percentile(phaseSteps, 0.95),
            Percentile(phaseSteps, 0.99),
            phaseSteps.Max());
    }

    private static bool IsUsable(RealtimeDemodulatedBlock block) =>
        block.IsHighQuality &&
        block.MeanReal208.Length > 0 &&
        block.MeanReal208.Length == block.MeanImaginary208.Length &&
        AllFinite(block.MeanReal208) &&
        AllFinite(block.MeanImaginary208);

    private static bool AllFinite(IReadOnlyList<double> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (!double.IsFinite(values[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + (position - lower) * (ordered[upper] - ordered[lower]);
    }
}
