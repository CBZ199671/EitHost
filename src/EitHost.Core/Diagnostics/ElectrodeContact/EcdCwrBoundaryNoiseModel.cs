using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrBoundaryNoiseModelOptions(
    double EmpiricalQuantile = 0.995,
    double ThresholdExpansionFactor = 1.10,
    double AbsoluteScaleFloor = 1.0e-12,
    double RelativeScaleFloor = 1.0e-6,
    double MinimumGlobalScoreThreshold = 1.0,
    double ChannelExcursionThreshold = 3.0,
    int MinimumCoherentChannels = 3,
    int RequiredConsecutiveChanges = 3,
    double ImmediateChangeMultiplier = 3.0,
    double MinimumPrecisionWeight = 0.05,
    double PhysicalAdcLsbVolts = 10.0 / ushort.MaxValue,
    double MinimumPostDemodulationScaleLsb = 0.02,
    bool DetrendLinearTrend = false,
    bool UseShortTermResiduals = false);

public sealed record EcdCwrBoundaryNoiseModel(
    double[] CenterVoltage208,
    double[] RobustScale208,
    double[] PrecisionWeight208,
    double GlobalScoreThreshold,
    double EmpiricalQuantile,
    int ReferenceFrameCount,
    string NoiseEstimationPolicy = "raw_reference_dispersion-v1")
{
    public double CalculateGlobalScore(IReadOnlyList<double> voltage208)
    {
        ValidateVoltage(voltage208);
        var sum = 0.0;
        for (var index = 0; index < CenterVoltage208.Length; index++)
        {
            var normalized = (voltage208[index] - CenterVoltage208[index]) / RobustScale208[index];
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / CenterVoltage208.Length);
    }

    public int CountExcursions(IReadOnlyList<double> voltage208, double threshold)
    {
        ValidateVoltage(voltage208);
        var count = 0;
        for (var index = 0; index < CenterVoltage208.Length; index++)
        {
            var normalized = Math.Abs((voltage208[index] - CenterVoltage208[index]) / RobustScale208[index]);
            if (normalized >= threshold)
            {
                count++;
            }
        }

        return count;
    }

    private void ValidateVoltage(IReadOnlyList<double> voltage208)
    {
        ArgumentNullException.ThrowIfNull(voltage208);
        if (voltage208.Count != DemodulatedFrame.FlattenedMeasurementCount ||
            voltage208.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Boundary-noise evaluation requires 208 finite voltage values.", nameof(voltage208));
        }
    }
}

public sealed class EcdCwrBoundaryNoiseModelBuilder
{
    public const string ShortTermResidualPolicy = "short_term_second_difference-v1";

    public EcdCwrBoundaryNoiseModel Create(
        IReadOnlyList<double[]> referenceVoltageFrames,
        EcdCwrBoundaryNoiseModelOptions? options = null,
        IReadOnlyList<double>? centerVoltage208 = null)
    {
        ArgumentNullException.ThrowIfNull(referenceVoltageFrames);
        options ??= new EcdCwrBoundaryNoiseModelOptions();
        ValidateOptions(options);
        if (referenceVoltageFrames.Count < (options.UseShortTermResiduals ? 3 : 2) ||
            referenceVoltageFrames.Any(vector =>
                vector.Length != DemodulatedFrame.FlattenedMeasurementCount ||
                vector.Any(value => !double.IsFinite(value))))
        {
            throw new ArgumentException(
                "Boundary-noise model requires finite 208-point reference frames (at least three for short-term residuals, otherwise two).",
                nameof(referenceVoltageFrames));
        }

        if (centerVoltage208 is not null &&
            (centerVoltage208.Count != DemodulatedFrame.FlattenedMeasurementCount ||
             centerVoltage208.Any(value => !double.IsFinite(value))))
        {
            throw new ArgumentException(
                "Boundary-noise center must contain 208 finite values.",
                nameof(centerVoltage208));
        }

        var center = centerVoltage208?.ToArray() ?? ChannelMedian(referenceVoltageFrames);
        var noiseFrames = options.UseShortTermResiduals
            ? ShortTermResiduals(referenceVoltageFrames)
            : options.DetrendLinearTrend
                ? DetrendLinear(referenceVoltageFrames)
                : referenceVoltageFrames.Select(vector => vector.ToArray()).ToArray();
        var noiseCenter = options.UseShortTermResiduals
            ? ChannelMedian(noiseFrames)
            : options.DetrendLinearTrend
                ? new double[DemodulatedFrame.FlattenedMeasurementCount]
                : center;
        var scale = ChannelScale(noiseFrames, noiseCenter, options);
        if (options.UseShortTermResiduals)
        {
            for (var channel = 0; channel < scale.Length; channel++)
            {
                scale[channel] = Math.Max(scale[channel], Math.Abs(center[channel]) * options.RelativeScaleFloor);
            }
        }
        var scores = noiseFrames
            .Select(vector => CalculateGlobalScore(vector, noiseCenter, scale))
            .ToArray();
        if (options.UseShortTermResiduals)
        {
            // Sparse reference transients are not stationary noise. A raw P99.5
            // of their scores otherwise turns an immutable noise gate blind.
            var medianScore = Quantile(scores, 0.5);
            var scoreMad = Quantile(scores.Select(score => Math.Abs(score - medianScore)).ToArray(), 0.5);
            var transientCutoff = Math.Max(options.MinimumGlobalScoreThreshold, medianScore + 4.5 * 1.4826 * scoreMad);
            scores = scores.Where(score => score <= transientCutoff).ToArray();
        }
        var threshold = Math.Max(
            options.MinimumGlobalScoreThreshold,
            Quantile(scores, options.EmpiricalQuantile) * options.ThresholdExpansionFactor);
        var medianScale = Quantile(scale, 0.5);
        var precisionWeight = scale
            .Select(value => Math.Clamp(
                (medianScale * medianScale) / (value * value),
                options.MinimumPrecisionWeight,
                1.0))
            .ToArray();

        return new EcdCwrBoundaryNoiseModel(
            center,
            scale,
            precisionWeight,
            threshold,
            options.EmpiricalQuantile,
            referenceVoltageFrames.Count,
            options.UseShortTermResiduals
                ? ShortTermResidualPolicy
                : options.DetrendLinearTrend
                    ? "linear_detrended_residual-v1"
                    : "raw_reference_dispersion-v1");
    }

    private static double[][] ShortTermResiduals(IReadOnlyList<double[]> vectors)
    {
        // The centered second difference removes a local linear trend; its
        // independent-noise variance is (1 + 4 + 1) sigma². This affects noise
        // training only. The frozen reference and later physical targets stay intact.
        var normalization = Math.Sqrt(6.0);
        var residuals = new double[vectors.Count - 2][];
        for (var frame = 1; frame < vectors.Count - 1; frame++)
        {
            var residual = new double[DemodulatedFrame.FlattenedMeasurementCount];
            for (var channel = 0; channel < residual.Length; channel++)
            {
                residual[channel] = (vectors[frame - 1][channel] - 2 * vectors[frame][channel] +
                    vectors[frame + 1][channel]) / normalization;
            }
            residuals[frame - 1] = residual;
        }
        return residuals;
    }

    private static double[][] DetrendLinear(IReadOnlyList<double[]> vectors)
    {
        var count = vectors.Count;
        var meanX = (count - 1) / 2.0;
        var denominator = Enumerable.Range(0, count)
            .Sum(index => Math.Pow(index - meanX, 2.0));
        var residuals = Enumerable.Range(0, count)
            .Select(_ => new double[DemodulatedFrame.FlattenedMeasurementCount])
            .ToArray();
        for (var channel = 0; channel < DemodulatedFrame.FlattenedMeasurementCount; channel++)
        {
            var meanY = vectors.Average(vector => vector[channel]);
            var numerator = 0.0;
            for (var frame = 0; frame < count; frame++)
            {
                numerator += (frame - meanX) * (vectors[frame][channel] - meanY);
            }

            var slope = denominator > 0.0 ? numerator / denominator : 0.0;
            for (var frame = 0; frame < count; frame++)
            {
                residuals[frame][channel] =
                    vectors[frame][channel] - (meanY + (slope * (frame - meanX)));
            }
        }

        return residuals;
    }

    private static double[] ChannelMedian(IReadOnlyList<double[]> vectors)
    {
        var result = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var column = new double[vectors.Count];
        for (var channel = 0; channel < result.Length; channel++)
        {
            for (var frame = 0; frame < vectors.Count; frame++)
            {
                column[frame] = vectors[frame][channel];
            }

            result[channel] = Quantile(column, 0.5);
        }

        return result;
    }

    private static double[] ChannelScale(
        IReadOnlyList<double[]> vectors,
        IReadOnlyList<double> center,
        EcdCwrBoundaryNoiseModelOptions options)
    {
        var result = new double[center.Count];
        var deviations = new double[vectors.Count];
        for (var channel = 0; channel < result.Length; channel++)
        {
            for (var frame = 0; frame < vectors.Count; frame++)
            {
                deviations[frame] = Math.Abs(vectors[frame][channel] - center[channel]);
            }

            result[channel] = Math.Max(
                options.AbsoluteScaleFloor,
                Math.Max(
                    options.PhysicalAdcLsbVolts * options.MinimumPostDemodulationScaleLsb,
                    Math.Max(
                        Math.Abs(center[channel]) * options.RelativeScaleFloor,
                        1.4826 * Quantile(deviations, 0.5))));
        }

        return result;
    }

    private static double CalculateGlobalScore(
        IReadOnlyList<double> vector,
        IReadOnlyList<double> center,
        IReadOnlyList<double> scale)
    {
        var sum = 0.0;
        for (var channel = 0; channel < vector.Count; channel++)
        {
            var normalized = (vector[channel] - center[channel]) / scale[channel];
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / vector.Count);
    }

    private static double Quantile(IReadOnlyList<double> values, double probability)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
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

    private static void ValidateOptions(EcdCwrBoundaryNoiseModelOptions options)
    {
        if (!double.IsFinite(options.EmpiricalQuantile) || options.EmpiricalQuantile is <= 0.5 or >= 1.0 ||
            !double.IsFinite(options.ThresholdExpansionFactor) || options.ThresholdExpansionFactor < 1.0 ||
            !double.IsFinite(options.AbsoluteScaleFloor) || options.AbsoluteScaleFloor <= 0.0 ||
            !double.IsFinite(options.RelativeScaleFloor) || options.RelativeScaleFloor <= 0.0 ||
            !double.IsFinite(options.MinimumGlobalScoreThreshold) || options.MinimumGlobalScoreThreshold <= 0.0 ||
            !double.IsFinite(options.ChannelExcursionThreshold) || options.ChannelExcursionThreshold <= 0.0 ||
            options.MinimumCoherentChannels <= 0 ||
            options.RequiredConsecutiveChanges <= 0 ||
            !double.IsFinite(options.ImmediateChangeMultiplier) || options.ImmediateChangeMultiplier <= 1.0 ||
            !double.IsFinite(options.MinimumPrecisionWeight) || options.MinimumPrecisionWeight is <= 0.0 or > 1.0 ||
            !double.IsFinite(options.PhysicalAdcLsbVolts) || options.PhysicalAdcLsbVolts <= 0.0 ||
            !double.IsFinite(options.MinimumPostDemodulationScaleLsb) || options.MinimumPostDemodulationScaleLsb <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}

public enum EcdCwrBoundaryChangeAction
{
    NoChange,
    PendingChange,
    Change
}

public sealed record EcdCwrBoundaryChangeDecision(
    EcdCwrBoundaryChangeAction Action,
    double GlobalScore,
    double Threshold,
    int ExcursionCount,
    int ConsecutiveChangeCount);

public sealed record EcdCwrBoundaryChangeReconstructionDisposition(
    bool ScheduleInverseReconstruction,
    bool RenderNeutralTrustedImage,
    bool HoldDynamicState,
    bool UseZeroDifferenceInput)
{
    public static EcdCwrBoundaryChangeReconstructionDisposition FromDecision(
        EcdCwrBoundaryChangeDecision decision,
        bool requireCurrentNeutralLayer = false)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var trustedChange = decision.Action == EcdCwrBoundaryChangeAction.Change;
        return new EcdCwrBoundaryChangeReconstructionDisposition(
            ScheduleInverseReconstruction: trustedChange || requireCurrentNeutralLayer,
            RenderNeutralTrustedImage: !trustedChange,
            HoldDynamicState: !trustedChange,
            UseZeroDifferenceInput: !trustedChange);
    }

    public double[] CreateTrustedTarget(
        IReadOnlyList<double> referenceVoltage208,
        IReadOnlyList<double> targetVoltage208)
    {
        ValidateInverseVector(referenceVoltage208, nameof(referenceVoltage208));
        ValidateInverseVector(targetVoltage208, nameof(targetVoltage208));
        return UseZeroDifferenceInput
            ? referenceVoltage208.ToArray()
            : targetVoltage208.ToArray();
    }

    private static void ValidateInverseVector(IReadOnlyList<double> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count != DemodulatedFrame.FlattenedMeasurementCount || values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Trusted inverse input requires 208 finite voltage values.", parameterName);
        }
    }
}

public sealed class EcdCwrBoundaryChangeGate
{
    private readonly EcdCwrBoundaryNoiseModel model;
    private readonly EcdCwrBoundaryNoiseModelOptions options;
    private int consecutiveChanges;

    public EcdCwrBoundaryChangeGate(
        EcdCwrBoundaryNoiseModel model,
        EcdCwrBoundaryNoiseModelOptions? options = null)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.options = options ?? new EcdCwrBoundaryNoiseModelOptions();
    }

    public EcdCwrBoundaryChangeDecision Evaluate(IReadOnlyList<double> targetVoltage208)
    {
        var score = model.CalculateGlobalScore(targetVoltage208);
        var excursions = model.CountExcursions(targetVoltage208, options.ChannelExcursionThreshold);
        var coherent = excursions >= options.MinimumCoherentChannels;
        var aboveFloor = coherent && score > model.GlobalScoreThreshold;
        if (!aboveFloor)
        {
            consecutiveChanges = 0;
            return new EcdCwrBoundaryChangeDecision(
                EcdCwrBoundaryChangeAction.NoChange,
                score,
                model.GlobalScoreThreshold,
                excursions,
                consecutiveChanges);
        }

        consecutiveChanges++;
        var immediate = score >= model.GlobalScoreThreshold * options.ImmediateChangeMultiplier;
        var action = immediate || consecutiveChanges >= options.RequiredConsecutiveChanges
            ? EcdCwrBoundaryChangeAction.Change
            : EcdCwrBoundaryChangeAction.PendingChange;
        return new EcdCwrBoundaryChangeDecision(
            action,
            score,
            model.GlobalScoreThreshold,
            excursions,
            consecutiveChanges);
    }

    public void Reset()
    {
        consecutiveChanges = 0;
    }
}
