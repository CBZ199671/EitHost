namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrImageQualityEstimator
{
    private const int ElectrodeCount = 16;

    public double Estimate(EcdCwrImageQualityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ElectrodeStates.Count != ElectrodeCount)
        {
            throw new ArgumentException("Image quality estimation expects 16 electrode states.", nameof(input));
        }

        if (input.FaultTypes is not null && input.FaultTypes.Count != ElectrodeCount)
        {
            throw new ArgumentException("Image quality estimation expects 16 fault types.", nameof(input));
        }

        var weights = input.MeasurementWeights.Where(double.IsFinite).Select(value => Math.Clamp(value, 0.0, 1.0)).ToArray();
        if (weights.Length == 0)
        {
            return 0.0;
        }

        var averageWeight = weights.Average();
        var effectiveFraction = weights.Count(weight => weight >= input.EffectiveWeightThreshold) / (double)weights.Length;
        var redCount = input.ElectrodeStates.Count(IsRedLike);
        var yellowCount = input.ElectrodeStates.Count(state => state == ElectrodeContactState.Yellow);
        var uncertainCount = input.FaultTypes?.Count(type => type == ElectrodeFaultType.UncertainStructured) ?? 0;
        var maxRedArc = MaxContiguousArc(input.ElectrodeStates, IsRedLike);

        var stateFactor = 1.0 - Math.Min(
            0.85,
            (redCount * input.RedPenalty) +
            (yellowCount * input.YellowPenalty) +
            (uncertainCount * input.UncertainPenalty));
        var effectiveFactor = Math.Clamp(0.35 + (0.65 * effectiveFraction), 0.0, 1.0);
        var arcFactor = 1.0 - Math.Min(0.75, input.ContiguousRedArcPenalty * maxRedArc * maxRedArc / ElectrodeCount);
        var coverage = AnalyzeCoverage(weights, input.EffectiveWeightThreshold, input.CoverageLowThreshold);
        var coverageFloorFactor = Math.Clamp(0.5 + (0.5 * coverage.MinimumCoverageFraction), 0.0, 1.0);
        var coverageGapFactor = 1.0 - Math.Min(
            0.65,
            input.CoverageGapPenalty *
            coverage.MaxContiguousLowCoverageArc *
            coverage.MaxContiguousLowCoverageArc /
            ElectrodeCount);
        var conditionNumber = input.ConditionNumber ??
            (input.UseCoverageConditionProxy ? coverage.ConditionNumberProxy : null);
        var conditionSoftLimit = input.ConditionNumber is null
            ? input.CoverageConditionProxySoftLimit
            : input.ConditionNumberSoftLimit;
        var conditionFactor = ConditionFactor(conditionNumber, conditionSoftLimit);
        var fitQuality = ReconstructionFitQuality(
            input.VoltageFitResidualNorm,
            input.VoltageFitRelativeResidual,
            input.VoltageFitCosineSimilarity,
            input.VoltageFitResidualL1Norm,
            input.VoltageFitRelativeL1Residual,
            input.VoltageFitResidualLinfNorm,
            input.VoltageFitMeasuredNorm,
            input.VoltageFitSimulatedNorm,
            input.VoltageFitR2,
            input.ReconstructionConductivityRange);
        if (fitQuality is { } postReconstructionQuality)
        {
            return postReconstructionQuality;
        }

        return Math.Clamp(
            averageWeight *
            effectiveFactor *
            stateFactor *
            arcFactor *
            coverageFloorFactor *
            coverageGapFactor *
            conditionFactor,
            0.0,
            1.0);
    }

    public static double? ReconstructionFitQuality(
        double? residualNorm,
        double? relativeResidual,
        double? cosineSimilarity,
        double? residualL1Norm = null,
        double? relativeL1Residual = null,
        double? residualLinfNorm = null,
        double? measuredNorm = null,
        double? simulatedNorm = null,
        double? fitR2 = null,
        double? conductivityRange = null)
    {
        if (residualNorm is not { } residual ||
            relativeResidual is not { } relative ||
            cosineSimilarity is not { } cosine ||
            !double.IsFinite(residual) ||
            !double.IsFinite(relative) ||
            !double.IsFinite(cosine) ||
            residual < 0.0 ||
            relative < 0.0)
        {
            return null;
        }

        var calibrated = CalibratedReconstructionFitQuality(
            residual,
            relative,
            residualL1Norm,
            relativeL1Residual,
            residualLinfNorm,
            measuredNorm,
            simulatedNorm,
            fitR2,
            conductivityRange);
        if (calibrated is not null)
        {
            return calibrated;
        }

        var logResidual = Math.Log10(residual + 1.0e-12);
        var logRelative = Math.Log10(relative + 1.0e-12);
        var fitLogit = (-0.5 * logResidual) - (2.0 * logRelative) + Math.Clamp(cosine, -1.0, 1.0);
        return 1.0 / (1.0 + Math.Exp(-Math.Clamp(fitLogit, -60.0, 60.0)));
    }

    private static double? CalibratedReconstructionFitQuality(
        double residualNorm,
        double relativeResidual,
        double? residualL1Norm,
        double? relativeL1Residual,
        double? residualLinfNorm,
        double? measuredNorm,
        double? simulatedNorm,
        double? fitR2,
        double? conductivityRange)
    {
        if (residualL1Norm is not { } residualL1 ||
            relativeL1Residual is not { } relativeL1 ||
            residualLinfNorm is not { } residualLinf ||
            measuredNorm is not { } measured ||
            simulatedNorm is not { } simulated ||
            fitR2 is not { } r2 ||
            conductivityRange is not { } range ||
            !double.IsFinite(residualL1) ||
            !double.IsFinite(relativeL1) ||
            !double.IsFinite(residualLinf) ||
            !double.IsFinite(measured) ||
            !double.IsFinite(simulated) ||
            !double.IsFinite(r2) ||
            !double.IsFinite(range) ||
            residualL1 < 0.0 ||
            relativeL1 < 0.0 ||
            residualLinf < 0.0 ||
            measured < 0.0 ||
            simulated < 0.0 ||
            range < 0.0)
        {
            return null;
        }

        var logResL2 = Log10Floor(residualNorm);
        var logResL1 = Log10Floor(residualL1);
        var logResLinf = Log10Floor(residualLinf);
        var logRelL2 = Log10Floor(relativeResidual);
        var logRelL1 = Log10Floor(relativeL1);
        var logMeasL2 = Log10Floor(measured);
        var logSimMeasRatio = Log10Floor(simulated / Math.Max(measured, 1.0e-12));
        var logCondRange = Log10Floor(range);
        var simRatioMeasProduct = Standardize(logSimMeasRatio, -0.41732108196974793, 0.3507253726487409) *
            Standardize(logMeasL2, -0.6334534778633906, 2.5894247637862486);
        var condRangeR2Product = Standardize(logCondRange, 0.21914764409794985, 2.4559335279016077) *
            Standardize(r2, 0.34453138030575353, 0.32541644129994574);

        if (logResL2 <= -1.78018143395)
        {
            if (logRelL1 <= -0.0739865480464)
            {
                return simRatioMeasProduct <= -0.59260818732
                    ? 0.898589420
                    : 0.779401305;
            }

            return condRangeR2Product <= 0.305739772908
                ? 0.619768447
                : 0.437788666;
        }

        if (logResL1 <= -0.388417944235)
        {
            return logRelL2 <= -0.0409369670332
                ? 0.507305854
                : 0.377338489;
        }

        return logResLinf <= -1.13537617801
            ? 0.314210125
            : 0.228364386;
    }

    private static double Log10Floor(double value)
    {
        return Math.Log10(Math.Max(value, 1.0e-12));
    }

    private static double Standardize(double value, double mean, double standardDeviation)
    {
        return (value - mean) / standardDeviation;
    }

    private static bool IsRedLike(ElectrodeContactState state)
    {
        return state is ElectrodeContactState.Red
            or ElectrodeContactState.DarkRed
            or ElectrodeContactState.SystemLevel;
    }

    private static int MaxContiguousArc(
        IReadOnlyList<ElectrodeContactState> states,
        Func<ElectrodeContactState, bool> predicate)
    {
        if (states.All(predicate))
        {
            return states.Count;
        }

        var max = 0;
        var current = 0;
        for (var offset = 0; offset < states.Count * 2; offset++)
        {
            if (predicate(states[offset % states.Count]))
            {
                current++;
                max = Math.Max(max, Math.Min(current, states.Count));
            }
            else
            {
                current = 0;
            }
        }

        return max;
    }

    private static double ConditionFactor(double? conditionNumber, double softLimit)
    {
        if (conditionNumber is not { } value || !double.IsFinite(value) || value <= 1.0)
        {
            return 1.0;
        }

        var limit = Math.Max(softLimit, 10.0);
        var logValue = Math.Log10(Math.Min(value, limit));
        var logLimit = Math.Log10(limit);
        return Math.Clamp(1.0 - (0.5 * logValue / logLimit), 0.5, 1.0);
    }

    private static MeasurementCoverageSummary AnalyzeCoverage(
        IReadOnlyList<double> weights,
        double effectiveWeightThreshold,
        double lowCoverageThreshold)
    {
        if (weights.Count != ElectrodeContactBaseline.RetainedObservationCount)
        {
            return new MeasurementCoverageSummary(1.0, 0, 1.0);
        }

        var coverage = new double[ElectrodeCount];
        var possible = new double[ElectrodeCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relative = 2; relative <= 14; relative++)
            {
                var normalizedWeight = Math.Clamp(weights[offset++], 0.0, 1.0);
                foreach (var electrode in InvolvedElectrodes(stimulation, relative))
                {
                    coverage[electrode] += normalizedWeight;
                    possible[electrode] += 1.0;
                }
            }
        }

        var coverageFractions = coverage
            .Zip(possible, (value, maximum) => maximum <= 0.0 ? 1.0 : Math.Clamp(value / maximum, 0.0, 1.0))
            .ToArray();
        var minimum = coverageFractions.Min();
        var lowThreshold = Math.Clamp(lowCoverageThreshold, 0.0, 1.0);
        var lowArc = MaxContiguousArc(
            coverageFractions,
            fraction => fraction < lowThreshold);
        var max = coverageFractions.Max();
        var conditionProxy = max <= 0.0
            ? double.PositiveInfinity
            : Math.Max(1.0, max / Math.Max(minimum, Math.Max(1.0e-6, effectiveWeightThreshold * 0.01)));
        return new MeasurementCoverageSummary(minimum, lowArc, conditionProxy);
    }

    private static IEnumerable<int> InvolvedElectrodes(int stimulation, int relative)
    {
        yield return Mod(stimulation);
        yield return Mod(stimulation + 1);
        yield return Mod(stimulation + relative);
        yield return Mod(stimulation + relative + 1);
    }

    private static int MaxContiguousArc(
        IReadOnlyList<double> values,
        Func<double, bool> predicate)
    {
        if (values.All(predicate))
        {
            return values.Count;
        }

        var max = 0;
        var current = 0;
        for (var offset = 0; offset < values.Count * 2; offset++)
        {
            if (predicate(values[offset % values.Count]))
            {
                current++;
                max = Math.Max(max, Math.Min(current, values.Count));
            }
            else
            {
                current = 0;
            }
        }

        return max;
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed record EcdCwrImageQualityInput(
    IReadOnlyList<ElectrodeContactState> ElectrodeStates,
    IReadOnlyList<double> MeasurementWeights,
    IReadOnlyList<ElectrodeFaultType>? FaultTypes = null,
    double? ConditionNumber = null,
    double EffectiveWeightThreshold = 0.5,
    double RedPenalty = 0.06,
    double YellowPenalty = 0.02,
    double UncertainPenalty = 0.03,
    double ContiguousRedArcPenalty = 0.35,
    double ConditionNumberSoftLimit = 1.0e6,
    double CoverageLowThreshold = 0.65,
    double CoverageGapPenalty = 0.35,
    bool UseCoverageConditionProxy = true,
    double CoverageConditionProxySoftLimit = 50.0,
    double? VoltageFitResidualNorm = null,
    double? VoltageFitRelativeResidual = null,
    double? VoltageFitCosineSimilarity = null,
    double? VoltageFitResidualL1Norm = null,
    double? VoltageFitRelativeL1Residual = null,
    double? VoltageFitResidualLinfNorm = null,
    double? VoltageFitMeasuredNorm = null,
    double? VoltageFitSimulatedNorm = null,
    double? VoltageFitR2 = null,
    double? ReconstructionConductivityRange = null);

internal sealed record MeasurementCoverageSummary(
    double MinimumCoverageFraction,
    int MaxContiguousLowCoverageArc,
    double ConditionNumberProxy);
