namespace EitHost.Core.Reconstruction;

public sealed record RealtimeDynamicKalmanStabilityDecision(
    bool ShouldFallback,
    string Reason,
    double RawSpatialRms,
    double FilteredSpatialRms,
    double SpatialRmsRatio,
    double RawRobustSpread,
    double FilteredRobustSpread,
    double RobustSpreadRatio,
    double DeviationRelative);

public static class RealtimeDynamicKalmanStabilityGuard
{
    public const double SpatialRmsRatioLimit = 1.75;
    public const double RobustSpreadRatioLimit = 2.0;
    public const double MinimumDeviationRelative = 0.01;

    public static RealtimeDynamicKalmanStabilityDecision Evaluate(
        IReadOnlyList<double>? rawConductivity,
        IReadOnlyList<double>? filteredConductivity)
    {
        if (rawConductivity is null ||
            filteredConductivity is null ||
            rawConductivity.Count == 0 ||
            filteredConductivity.Count != rawConductivity.Count)
        {
            return Invalid("shape_mismatch");
        }

        var count = rawConductivity.Count;
        var raw = new double[count];
        var filtered = new double[count];
        for (var index = 0; index < count; index++)
        {
            raw[index] = rawConductivity[index];
            filtered[index] = filteredConductivity[index];
            if (!double.IsFinite(raw[index]))
            {
                return Invalid("nonfinite_raw_state");
            }

            if (!double.IsFinite(filtered[index]))
            {
                return Invalid("nonfinite_filtered_state");
            }
        }

        var rawCenter = Median(raw);
        var filteredCenter = Median(filtered);
        var rawAbsolute = new double[count];
        var rawSpread = new double[count];
        var filteredSpread = new double[count];
        var rawSquared = 0.0;
        var filteredSquared = 0.0;
        var deviationSquared = 0.0;
        for (var index = 0; index < count; index++)
        {
            var rawCentered = raw[index] - rawCenter;
            var filteredCentered = filtered[index] - filteredCenter;
            var deviation = filteredCentered - rawCentered;
            rawAbsolute[index] = Math.Abs(raw[index]);
            rawSpread[index] = Math.Abs(rawCentered);
            filteredSpread[index] = Math.Abs(filteredCentered);
            rawSquared += rawCentered * rawCentered;
            filteredSquared += filteredCentered * filteredCentered;
            deviationSquared += deviation * deviation;
        }

        var referenceScale = Math.Max(
            Math.Max(Math.Abs(rawCenter), Median(rawAbsolute)),
            1.0e-6);
        var rawRms = Math.Sqrt(rawSquared / count);
        var filteredRms = Math.Sqrt(filteredSquared / count);
        var deviationRelative = Math.Sqrt(deviationSquared / count) / referenceScale;
        var rawRobust = Quantile(rawSpread, 0.995);
        var filteredRobust = Quantile(filteredSpread, 0.995);
        var rmsRatio = filteredRms / Math.Max(rawRms, 0.005 * referenceScale);
        var robustRatio = filteredRobust / Math.Max(rawRobust, 0.01 * referenceScale);
        var shouldFallback = deviationRelative > MinimumDeviationRelative &&
            (rmsRatio > SpatialRmsRatioLimit || robustRatio > RobustSpreadRatioLimit);

        return new RealtimeDynamicKalmanStabilityDecision(
            shouldFallback,
            shouldFallback ? "spatial_energy_amplified" : "accepted",
            rawRms,
            filteredRms,
            rmsRatio,
            rawRobust,
            filteredRobust,
            robustRatio,
            deviationRelative);
    }

    private static RealtimeDynamicKalmanStabilityDecision Invalid(string reason)
    {
        return new RealtimeDynamicKalmanStabilityDecision(
            true,
            reason,
            double.NaN,
            double.NaN,
            double.PositiveInfinity,
            double.NaN,
            double.NaN,
            double.PositiveInfinity,
            double.PositiveInfinity);
    }

    private static double Median(double[] values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? 0.5 * (sorted[middle - 1] + sorted[middle])
            : sorted[middle];
    }

    private static double Quantile(double[] values, double probability)
    {
        Array.Sort(values);
        var index = (int)Math.Round(
            probability * (values.Length - 1),
            MidpointRounding.AwayFromZero);
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}
