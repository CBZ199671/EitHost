using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrRobustReferenceOptions(
    int MinimumFrameCount = 100,
    double RelativeScaleFloor = 0.005,
    double AbsoluteScaleFloor = 1.0e-12,
    double FrameRmsThreshold = 4.0,
    int OutlierFilterPasses = 2,
    double HuberTuningConstant = 1.5,
    bool NormalizeCommonScale = false,
    double PhysicalAdcLsbVolts = 10.0 / ushort.MaxValue,
    bool DetrendNoiseModel = false);

public sealed record EcdCwrRobustReference(
    double[] Voltage208,
    double[] FullReal256,
    double[] FullImaginary256,
    int FrameCount,
    int RejectedFrameCount,
    EcdCwrBoundaryNoiseModel? NoiseModel = null,
    bool CommonScaleNormalized = false,
    double MedianInputCommonScale = 1.0,
    string CommonScaleNormalizationPolicy = "none");

public sealed record EcdCwrRobustReferenceObservation(
    double[] Voltage208,
    double[] FullReal256,
    double[] FullImaginary256);

public sealed class EcdCwrRobustReferenceBuilder
{
    private const double MinimumStabilityWeight = 0.05;

    public EcdCwrRobustReference Create(
        IReadOnlyList<DemodulatedFrame> frames,
        EcdCwrRobustReferenceOptions? options = null,
        IReadOnlyList<double>? stabilityWeight208 = null,
        bool allowFiniteDiagnosticFrames = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new EcdCwrRobustReferenceOptions();
        ValidateOptions(options);
        ValidateStabilityWeights(stabilityWeight208);

        var usable = frames
            .Where(allowFiniteDiagnosticFrames ? IsFiniteDiagnosticFrame : IsStrictGreenFrame)
            .Select(CreateObservation)
            .ToArray();
        if (usable.Length < options.MinimumFrameCount)
        {
            throw new InvalidOperationException(
                $"Robust reference requires at least {options.MinimumFrameCount} " +
                $"{(allowFiniteDiagnosticFrames ? "finite diagnostic" : "strict-green")} frames; found {usable.Length}.");
        }

        return CreateFromFiniteObservations(
            usable,
            options,
            stabilityWeight208,
            frames.Count);
    }

    public EcdCwrRobustReference CreateFromObservations(
        IReadOnlyList<EcdCwrRobustReferenceObservation> observations,
        EcdCwrRobustReferenceOptions? options = null,
        IReadOnlyList<double>? stabilityWeight208 = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        options ??= new EcdCwrRobustReferenceOptions();
        ValidateOptions(options);
        ValidateStabilityWeights(stabilityWeight208);

        var usable = observations.Where(IsFiniteObservation).ToArray();
        if (usable.Length < options.MinimumFrameCount)
        {
            throw new InvalidOperationException(
                $"Robust reference requires at least {options.MinimumFrameCount} finite observations; found {usable.Length}.");
        }

        return CreateFromFiniteObservations(
            usable,
            options,
            stabilityWeight208,
            observations.Count);
    }

    public static bool IsFiniteObservation(EcdCwrRobustReferenceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.Voltage208.Length == DemodulatedFrame.FlattenedMeasurementCount &&
            observation.FullReal256.Length == DemodulatedFrame.FlattenedFullMeasurementCount &&
            observation.FullImaginary256.Length == DemodulatedFrame.FlattenedFullMeasurementCount &&
            observation.Voltage208.All(double.IsFinite) &&
            observation.FullReal256.All(double.IsFinite) &&
            observation.FullImaginary256.All(double.IsFinite);
    }

    private static EcdCwrRobustReference CreateFromFiniteObservations(
        IReadOnlyList<EcdCwrRobustReferenceObservation> observations,
        EcdCwrRobustReferenceOptions options,
        IReadOnlyList<double>? stabilityWeight208,
        int inputCount)
    {
        var prepared = observations.ToArray();
        var normalizationScales = Enumerable.Repeat(1.0, prepared.Length).ToArray();
        if (options.NormalizeCommonScale)
        {
            var template = ChannelMedian(prepared.Select(item => item.Voltage208).ToArray());
            var normalized = prepared
                .Select(observation => EcdCwrCommonScaleNormalizer.NormalizeObservation(template, observation))
                .ToArray();
            prepared = normalized.Select(item => item.Observation).ToArray();
            normalizationScales = normalized.Select(item => item.CommonScale).ToArray();
        }

        var stable = FilterStableObservations(prepared, options, stabilityWeight208);
        if (stable.Length < options.MinimumFrameCount)
        {
            throw new InvalidOperationException(
                $"Robust reference retained {stable.Length}/{observations.Count} stable observations; at least {options.MinimumFrameCount} are required.");
        }

        var stableVoltage = stable.Select(observation => observation.Voltage208).ToArray();
        var robustCenter = ChannelMedian(stableVoltage);
        var robustScale = ChannelMadScale(stableVoltage, robustCenter, options);
        // V278: one scalar belongs to one physical frame in this reference
        // window. Reuse it for all 208/256 components so the estimator never
        // assembles a synthetic reference from different frames per channel.
        var frameWeights = stableVoltage
            .Select(vector => HuberWeight(
                NormalizedRms(vector, robustCenter, robustScale, stabilityWeight208),
                options.HuberTuningConstant))
            .ToArray();
        var voltage208 = WeightedChannelMean(stableVoltage, frameWeights);
        return new EcdCwrRobustReference(
            voltage208,
            WeightedChannelMean(
                stable.Select(observation => observation.FullReal256).ToArray(),
                frameWeights),
            WeightedChannelMean(
                stable.Select(observation => observation.FullImaginary256).ToArray(),
                frameWeights),
            stable.Length,
            inputCount - stable.Length,
            new EcdCwrBoundaryNoiseModelBuilder().Create(
                stableVoltage,
                new EcdCwrBoundaryNoiseModelOptions(
                    PhysicalAdcLsbVolts: options.PhysicalAdcLsbVolts,
                    DetrendLinearTrend: options.DetrendNoiseModel),
                centerVoltage208: voltage208),
            options.NormalizeCommonScale,
            Median(normalizationScales),
            options.NormalizeCommonScale
                ? EcdCwrCommonScaleNormalizer.PolicyVersion
                : "none");
    }

    public static bool IsStrictGreenFrame(DemodulatedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.FullRealComponents is not null &&
            frame.FullImaginaryComponents is not null &&
            frame.FullAmplitudes is not null &&
            frame.WindowQualities.Count == DemodulatedFrame.StimulationCount &&
            frame.WindowQualities.All(quality =>
                quality.State == DemodulatedWindowQualityState.Valid &&
                !quality.Rejected &&
                quality.Top3Contiguous &&
                quality.Top1IsTripletCenter &&
                quality.TripletCenterChannel == quality.ExpectedReferenceChannel) &&
            IsFiniteMatrix(frame.Amplitudes) &&
            IsFiniteMatrix(frame.FullAmplitudes) &&
            IsFiniteMatrix(frame.FullRealComponents) &&
            IsFiniteMatrix(frame.FullImaginaryComponents);
    }

    public static bool IsFiniteDiagnosticFrame(DemodulatedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.FullRealComponents is not null &&
            frame.FullImaginaryComponents is not null &&
            frame.FullAmplitudes is not null &&
            IsFiniteMatrix(frame.Amplitudes) &&
            IsFiniteMatrix(frame.FullAmplitudes) &&
            IsFiniteMatrix(frame.FullRealComponents) &&
            IsFiniteMatrix(frame.FullImaginaryComponents);
    }

    private static EcdCwrRobustReferenceObservation[] FilterStableObservations(
        IReadOnlyList<EcdCwrRobustReferenceObservation> observations,
        EcdCwrRobustReferenceOptions options,
        IReadOnlyList<double>? stabilityWeight208)
    {
        var current = observations.ToArray();
        for (var pass = 0; pass < options.OutlierFilterPasses; pass++)
        {
            var vectors = current.Select(observation => observation.Voltage208).ToArray();
            var center = ChannelMedian(vectors);
            var scale = ChannelMadScale(vectors, center, options);
            var filtered = current
                .Where((_, index) => NormalizedRms(
                    vectors[index],
                    center,
                    scale,
                    stabilityWeight208) <= options.FrameRmsThreshold)
                .ToArray();
            if (filtered.Length == current.Length)
            {
                break;
            }

            current = filtered;
            if (current.Length < options.MinimumFrameCount)
            {
                break;
            }
        }

        return current;
    }

    private static EcdCwrRobustReferenceObservation CreateObservation(DemodulatedFrame frame)
    {
        return new EcdCwrRobustReferenceObservation(
            frame.FlattenAmplitudesRowMajor(),
            frame.FlattenFullRealRowMajor(),
            frame.FlattenFullImaginaryRowMajor());
    }

    private static double[] ChannelMedian(IReadOnlyList<double[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return [];
        }

        var width = vectors[0].Length;
        if (vectors.Any(vector => vector.Length != width || vector.Any(value => !double.IsFinite(value))))
        {
            throw new ArgumentException("Robust reference vectors must have equal length and finite values.", nameof(vectors));
        }

        var result = new double[width];
        var column = new double[vectors.Count];
        for (var channel = 0; channel < width; channel++)
        {
            for (var frame = 0; frame < vectors.Count; frame++)
            {
                column[frame] = vectors[frame][channel];
            }

            result[channel] = Median(column);
        }

        return result;
    }

    private static double[] WeightedChannelMean(
        IReadOnlyList<double[]> vectors,
        IReadOnlyList<double> frameWeights)
    {
        if (vectors.Count == 0 || vectors.Count != frameWeights.Count)
        {
            throw new ArgumentException(
                "Huber reference vectors and frame weights must have the same non-zero count.",
                nameof(vectors));
        }

        var width = vectors[0].Length;
        if (vectors.Any(vector => vector.Length != width || vector.Any(value => !double.IsFinite(value))) ||
            frameWeights.Any(weight => !double.IsFinite(weight) || weight <= 0.0))
        {
            throw new ArgumentException(
                "Huber reference inputs must have equal finite vectors and positive finite frame weights.",
                nameof(vectors));
        }

        var weightSum = frameWeights.Sum();
        var result = new double[width];
        for (var frame = 0; frame < vectors.Count; frame++)
        {
            var weight = frameWeights[frame];
            for (var channel = 0; channel < width; channel++)
            {
                result[channel] += weight * vectors[frame][channel];
            }
        }

        for (var channel = 0; channel < width; channel++)
        {
            result[channel] /= weightSum;
        }

        return result;
    }

    private static double[] ChannelMadScale(
        IReadOnlyList<double[]> vectors,
        IReadOnlyList<double> center,
        EcdCwrRobustReferenceOptions options)
    {
        var scale = new double[center.Count];
        var residuals = new double[vectors.Count];
        for (var channel = 0; channel < center.Count; channel++)
        {
            for (var frame = 0; frame < vectors.Count; frame++)
            {
                residuals[frame] = Math.Abs(vectors[frame][channel] - center[channel]);
            }

            scale[channel] = Math.Max(
                options.AbsoluteScaleFloor,
                Math.Max(options.RelativeScaleFloor * Math.Abs(center[channel]), 1.4826 * Median(residuals)));
        }

        return scale;
    }

    private static double NormalizedRms(
        IReadOnlyList<double> vector,
        IReadOnlyList<double> center,
        IReadOnlyList<double> scale,
        IReadOnlyList<double>? stabilityWeight208)
    {
        var sum = 0.0;
        var weightSum = 0.0;
        for (var channel = 0; channel < vector.Count; channel++)
        {
            var weight = stabilityWeight208?[channel] ?? 1.0;
            if (weight < MinimumStabilityWeight)
            {
                continue;
            }

            var normalized = (vector[channel] - center[channel]) / scale[channel];
            sum += weight * normalized * normalized;
            weightSum += weight;
        }

        return weightSum <= 0.0 ? double.PositiveInfinity : Math.Sqrt(sum / weightSum);
    }

    private static double HuberWeight(double normalizedFrameRms, double tuningConstant)
    {
        if (!double.IsFinite(normalizedFrameRms) || normalizedFrameRms < 0.0)
        {
            return 0.0;
        }

        return normalizedFrameRms <= tuningConstant || normalizedFrameRms == 0.0
            ? 1.0
            : tuningConstant / normalizedFrameRms;
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

    private static bool IsFiniteMatrix(double[,] values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateOptions(EcdCwrRobustReferenceOptions options)
    {
        if (options.MinimumFrameCount <= 0 ||
            !double.IsFinite(options.RelativeScaleFloor) || options.RelativeScaleFloor <= 0.0 ||
            !double.IsFinite(options.AbsoluteScaleFloor) || options.AbsoluteScaleFloor <= 0.0 ||
            !double.IsFinite(options.FrameRmsThreshold) || options.FrameRmsThreshold <= 0.0 ||
            options.OutlierFilterPasses < 0 ||
            !double.IsFinite(options.HuberTuningConstant) || options.HuberTuningConstant <= 0.0 ||
            !double.IsFinite(options.PhysicalAdcLsbVolts) || options.PhysicalAdcLsbVolts <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static void ValidateStabilityWeights(IReadOnlyList<double>? stabilityWeight208)
    {
        if (stabilityWeight208 is null)
        {
            return;
        }

        if (stabilityWeight208.Count != DemodulatedFrame.FlattenedMeasurementCount ||
            stabilityWeight208.Any(weight => !double.IsFinite(weight) || weight < 0.0 || weight > 1.0) ||
            !stabilityWeight208.Any(weight => weight >= MinimumStabilityWeight))
        {
            throw new ArgumentException(
                "Stability weights must contain 208 finite values in [0, 1] with at least one effective channel.",
                nameof(stabilityWeight208));
        }
    }
}
