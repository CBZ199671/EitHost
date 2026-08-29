namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrCommonScaleNormalizationResult(
    double CommonScale,
    double[] Values,
    string PolicyVersion);

public sealed record EcdCwrCommonScaleNormalizedObservation(
    double CommonScale,
    EcdCwrRobustReferenceObservation Observation,
    string PolicyVersion);

/// <summary>
/// Removes one robust positive multiplicative scale from an entire physical
/// EIT frame. It never estimates or applies per-channel corrections.
/// </summary>
public static class EcdCwrCommonScaleNormalizer
{
    public const string PolicyVersion = "common_scale_normalized-v1";

    public static EcdCwrCommonScaleNormalizationResult NormalizeVector(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target)
    {
        var scale = EstimateRobustPositiveScale(reference, target);
        return new EcdCwrCommonScaleNormalizationResult(
            scale,
            Divide(target, scale),
            PolicyVersion);
    }

    public static EcdCwrCommonScaleNormalizedObservation NormalizeObservation(
        IReadOnlyList<double> referenceVoltage208,
        EcdCwrRobustReferenceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!EcdCwrRobustReferenceBuilder.IsFiniteObservation(observation))
        {
            throw new ArgumentException(
                "Common-scale normalization requires one finite 208/256/256 physical frame.",
                nameof(observation));
        }

        var scale = EstimateRobustPositiveScale(referenceVoltage208, observation.Voltage208);
        return new EcdCwrCommonScaleNormalizedObservation(
            scale,
            new EcdCwrRobustReferenceObservation(
                Divide(observation.Voltage208, scale),
                Divide(observation.FullReal256, scale),
                Divide(observation.FullImaginary256, scale)),
            PolicyVersion);
    }

    public static double EstimateRobustPositiveScale(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target)
    {
        ValidatePair(reference, target);
        if (!TryEstimateRobustPositiveScaleCore(reference, target, out var scale))
        {
            throw new InvalidOperationException(
                "Common-scale normalization requires at least three same-sign finite measurements.");
        }

        return scale;
    }

    public static bool TryEstimateRobustPositiveScale(
        IReadOnlyList<double>? reference,
        IReadOnlyList<double>? target,
        out double scale)
    {
        scale = default;
        return IsValidPair(reference, target) &&
            TryEstimateRobustPositiveScaleCore(reference!, target!, out scale);
    }

    private static bool TryEstimateRobustPositiveScaleCore(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        out double scale)
    {
        scale = default;
        var maximumReferenceMagnitude = reference.Max(Math.Abs);
        var referenceFloor = Math.Max(maximumReferenceMagnitude * 1.0e-6, 1.0e-12);
        var ratios = new List<double>(reference.Count);
        for (var index = 0; index < reference.Count; index++)
        {
            if (Math.Abs(reference[index]) < referenceFloor)
            {
                continue;
            }

            var ratio = target[index] / reference[index];
            if (double.IsFinite(ratio) && ratio > 0.0)
            {
                ratios.Add(ratio);
            }
        }

        if (ratios.Count < 3)
        {
            return false;
        }

        ratios.Sort();
        var midpoint = ratios.Count / 2;
        scale = ratios.Count % 2 == 0
            ? 0.5 * (ratios[midpoint - 1] + ratios[midpoint])
            : ratios[midpoint];
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            scale = default;
            return false;
        }

        return true;
    }

    private static double[] Divide(IReadOnlyList<double> values, double scale)
    {
        var result = new double[values.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = values[index] / scale;
        }

        return result;
    }

    private static void ValidatePair(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);
        if (!IsValidPair(reference, target))
        {
            throw new ArgumentException(
                "Common-scale normalization requires equal non-empty finite vectors.");
        }
    }

    private static bool IsValidPair(
        IReadOnlyList<double>? reference,
        IReadOnlyList<double>? target)
    {
        return reference is not null &&
            target is not null &&
            reference.Count > 0 &&
            reference.Count == target.Count &&
            !reference.Any(value => !double.IsFinite(value)) &&
            !target.Any(value => !double.IsFinite(value));
    }
}
