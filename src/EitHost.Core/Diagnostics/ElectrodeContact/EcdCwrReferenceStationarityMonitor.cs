using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public enum EcdCwrReferenceStationarityState
{
    Warming,
    Drifting,
    Stable
}

public sealed record EcdCwrReferenceStationarityOptions(
    double EvaluationWindowSeconds = 60.0,
    double MinimumDurationSeconds = 55.0,
    int MinimumObservationCount = 32,
    double EdgeFraction = 0.25,
    double MaximumAbsoluteCommonScaleDriftPerMinute = 5.0e-5,
    double MaximumShapeResidualPerMinute = 1.0e-4,
    int RequiredStableUpdates = 5,
    double AdaptiveConfidenceZ = 2.576,
    double MaximumAdaptiveCommonScaleDriftPerMinute = 1.5e-4,
    double MaximumAdaptiveShapeResidualPerMinute = 2.5e-4,
    int MinimumStepDetectionObservations = 32,
    double CoherentStepSigmaThreshold = 6.0,
    double CoherentStepMinimumChannelFraction = 0.10,
    double AdaptiveEvaluationIntervalSeconds = 1.0,
    bool AllowCommonScaleDrift = false);

public sealed record EcdCwrReferenceStationarityResult(
    EcdCwrReferenceStationarityState State,
    bool CanLock,
    int ObservationCount,
    double DurationSeconds,
    double CommonScale,
    double CommonScaleDriftPerMinute,
    double ShapeResidual,
    double ShapeResidualPerMinute,
    int RequiredObservationCount,
    double RequiredDurationSeconds,
    int ConsecutiveStableUpdates,
    int RequiredStableUpdates,
    bool MeetsDriftThresholds,
    double EffectiveCommonScaleDriftLimitPerMinute,
    double EffectiveShapeResidualLimitPerMinute,
    double CommonScaleNoiseUncertaintyPerMinute,
    double ShapeNoiseUncertaintyPerMinute,
    bool AdaptiveNoiseReady,
    bool AdaptiveThresholdLimitedBySafetyCeiling,
    int QuietWindowRestartCount,
    bool QuietWindowRestarted,
    bool CommonScaleNormalizedMode);

/// <summary>
/// Determines whether a pre-reference boundary-voltage stream has stopped
/// moving in time. Relative mode treats one common multiplicative scale as a
/// nuisance parameter but never performs per-channel or moving-reference compensation.
/// </summary>
public sealed class EcdCwrReferenceStationarityMonitor
{
    private const double MadNormalConsistencyFactor = 1.4826;
    private const double MedianNormalStandardErrorFactor = 1.2533141373155;
    private const int StepProfileRefreshObservationCount = 16;

    private readonly EcdCwrReferenceStationarityOptions options;
    private readonly List<Observation> observations = [];
    private int consecutiveStableUpdates;
    private double[]? stepDetectionCenter;
    private double[]? stepDetectionScale;
    private int observationsSinceStepProfileRefresh;
    private int quietWindowRestartCount;
    private double lastAdaptiveEvaluationSeconds = double.NegativeInfinity;
    private EcdCwrReferenceStationarityResult? lastAdaptiveResult;

    public EcdCwrReferenceStationarityMonitor(
        EcdCwrReferenceStationarityOptions? options = null)
    {
        this.options = options ?? new EcdCwrReferenceStationarityOptions();
        ValidateOptions(this.options);
    }

    public EcdCwrReferenceStationarityResult Update(
        double elapsedSeconds,
        IReadOnlyList<double> voltage208)
    {
        if (!double.IsFinite(elapsedSeconds) ||
            elapsedSeconds < 0.0 ||
            voltage208.Count != DemodulatedFrame.FlattenedMeasurementCount ||
            voltage208.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Reference stationarity requires finite elapsed time and 208 finite voltages.");
        }

        if (observations.Count > 0 && elapsedSeconds <= observations[^1].ElapsedSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedSeconds),
                "Reference stationarity timestamps must increase.");
        }

        var quietWindowRestarted = IsCoherentStep(voltage208);
        if (quietWindowRestarted)
        {
            observations.Clear();
            consecutiveStableUpdates = 0;
            stepDetectionCenter = null;
            stepDetectionScale = null;
            observationsSinceStepProfileRefresh = 0;
            quietWindowRestartCount++;
            lastAdaptiveEvaluationSeconds = double.NegativeInfinity;
            lastAdaptiveResult = null;
        }

        observations.Add(new Observation(elapsedSeconds, voltage208.ToArray()));
        var cutoff = elapsedSeconds - options.EvaluationWindowSeconds;
        observations.RemoveAll(observation => observation.ElapsedSeconds < cutoff);
        RefreshStepDetectionProfileIfNeeded();

        var durationSeconds = observations.Count < 2
            ? 0.0
            : observations[^1].ElapsedSeconds - observations[0].ElapsedSeconds;
        if (observations.Count < options.MinimumObservationCount ||
            durationSeconds < options.MinimumDurationSeconds)
        {
            consecutiveStableUpdates = 0;
            lastAdaptiveResult = null;
            return new EcdCwrReferenceStationarityResult(
                EcdCwrReferenceStationarityState.Warming,
                CanLock: false,
                observations.Count,
                durationSeconds,
                CommonScale: 1.0,
                CommonScaleDriftPerMinute: 0.0,
                ShapeResidual: 0.0,
                ShapeResidualPerMinute: 0.0,
                options.MinimumObservationCount,
                options.MinimumDurationSeconds,
                ConsecutiveStableUpdates: 0,
                options.RequiredStableUpdates,
                MeetsDriftThresholds: false,
                options.MaximumAbsoluteCommonScaleDriftPerMinute,
                options.MaximumShapeResidualPerMinute,
                CommonScaleNoiseUncertaintyPerMinute: 0.0,
                ShapeNoiseUncertaintyPerMinute: 0.0,
                AdaptiveNoiseReady: false,
                AdaptiveThresholdLimitedBySafetyCeiling: false,
                quietWindowRestartCount,
                quietWindowRestarted,
                options.AllowCommonScaleDrift);
        }

        if (!quietWindowRestarted &&
            lastAdaptiveResult is not null &&
            elapsedSeconds - lastAdaptiveEvaluationSeconds < options.AdaptiveEvaluationIntervalSeconds)
        {
            return lastAdaptiveResult with
            {
                ObservationCount = observations.Count,
                DurationSeconds = durationSeconds,
                QuietWindowRestartCount = quietWindowRestartCount,
                QuietWindowRestarted = false
            };
        }

        var edgeCount = Math.Clamp(
            (int)Math.Ceiling(observations.Count * options.EdgeFraction),
            1,
            observations.Count / 2);
        var early = observations.Take(edgeCount).ToArray();
        var late = observations.Skip(observations.Count - edgeCount).ToArray();
        var earlyCenter = ChannelMedian(early);
        var lateCenter = ChannelMedian(late);
        var commonScale = EcdCwrCommonScaleNormalizer.EstimateRobustPositiveScale(
            earlyCenter,
            lateCenter);
        var shapeResidual = RelativeResidual(earlyCenter, lateCenter, commonScale);
        var earlyTime = early.Average(observation => observation.ElapsedSeconds);
        var lateTime = late.Average(observation => observation.ElapsedSeconds);
        var separationMinutes = Math.Max((lateTime - earlyTime) / 60.0, double.Epsilon);
        var commonScaleDrift = (commonScale - 1.0) / separationMinutes;
        var shapeResidualPerMinute = shapeResidual / separationMinutes;
        var adaptive = CalculateAdaptiveThresholds(
            earlyCenter,
            early.Length,
            late.Length,
            separationMinutes);
        var safetyCeilingBlocked = adaptive.ShapeLimitedBySafetyCeiling ||
            (!options.AllowCommonScaleDrift && adaptive.CommonScaleLimitedBySafetyCeiling);
        var stable =
            !safetyCeilingBlocked &&
            (options.AllowCommonScaleDrift ||
             Math.Abs(commonScaleDrift) <= adaptive.CommonScaleLimitPerMinute) &&
            shapeResidualPerMinute <= adaptive.ShapeLimitPerMinute;
        consecutiveStableUpdates = stable ? consecutiveStableUpdates + 1 : 0;
        var canLock = consecutiveStableUpdates >= options.RequiredStableUpdates;

        lastAdaptiveEvaluationSeconds = elapsedSeconds;
        lastAdaptiveResult = new EcdCwrReferenceStationarityResult(
            canLock
                ? EcdCwrReferenceStationarityState.Stable
                : EcdCwrReferenceStationarityState.Drifting,
            canLock,
            observations.Count,
            durationSeconds,
            commonScale,
            commonScaleDrift,
            shapeResidual,
            shapeResidualPerMinute,
            options.MinimumObservationCount,
            options.MinimumDurationSeconds,
            consecutiveStableUpdates,
            options.RequiredStableUpdates,
            MeetsDriftThresholds: stable,
            adaptive.CommonScaleLimitPerMinute,
            adaptive.ShapeLimitPerMinute,
            adaptive.CommonScaleNoiseUncertaintyPerMinute,
            adaptive.ShapeNoiseUncertaintyPerMinute,
            AdaptiveNoiseReady: true,
            safetyCeilingBlocked,
            quietWindowRestartCount,
            quietWindowRestarted,
            options.AllowCommonScaleDrift);
        return lastAdaptiveResult;
    }

    public EcdCwrReferenceStationarityResult? UpdateBlockFrames(
        long blockStartSampleIndex,
        int sampleRateHz,
        IReadOnlyList<DemodulatedFrame> frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockStartSampleIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentNullException.ThrowIfNull(frames);

        EcdCwrReferenceStationarityResult? latest = null;
        foreach (var frame in frames.Where(EcdCwrRobustReferenceBuilder.IsStrictGreenFrame))
        {
            if (frame.EndSample <= frame.StartSample)
            {
                continue;
            }

            var absoluteEndSample = checked(blockStartSampleIndex + frame.EndSample);
            latest = Update(
                absoluteEndSample / (double)sampleRateHz,
                frame.FlattenAmplitudesRowMajor());
        }

        return latest;
    }

    public void Reset()
    {
        observations.Clear();
        consecutiveStableUpdates = 0;
        stepDetectionCenter = null;
        stepDetectionScale = null;
        observationsSinceStepProfileRefresh = 0;
        quietWindowRestartCount = 0;
        lastAdaptiveEvaluationSeconds = double.NegativeInfinity;
        lastAdaptiveResult = null;
    }

    private bool IsCoherentStep(IReadOnlyList<double> voltage208)
    {
        if (stepDetectionCenter is null || stepDetectionScale is null)
        {
            return false;
        }

        var normalizedVoltage = EcdCwrCommonScaleNormalizer.NormalizeVector(
            stepDetectionCenter,
            voltage208).Values;
        var squaredScore = 0.0;
        var excursionCount = 0;
        for (var channel = 0; channel < voltage208.Count; channel++)
        {
            var normalized = Math.Abs(normalizedVoltage[channel] - stepDetectionCenter[channel]) /
                stepDetectionScale[channel];
            squaredScore += normalized * normalized;
            if (normalized >= options.CoherentStepSigmaThreshold)
            {
                excursionCount++;
            }
        }

        var minimumExcursions = Math.Max(
            3,
            (int)Math.Ceiling(voltage208.Count * options.CoherentStepMinimumChannelFraction));
        var globalScore = Math.Sqrt(squaredScore / voltage208.Count);
        return excursionCount >= minimumExcursions &&
            globalScore >= options.CoherentStepSigmaThreshold;
    }

    private void RefreshStepDetectionProfileIfNeeded()
    {
        observationsSinceStepProfileRefresh++;
        if (observations.Count < options.MinimumStepDetectionObservations ||
            stepDetectionCenter is not null &&
            observationsSinceStepProfileRefresh < StepProfileRefreshObservationCount)
        {
            return;
        }

        var rawCenter = ChannelMedian(observations);
        var normalizedObservations = observations
            .Select(observation => new Observation(
                observation.ElapsedSeconds,
                EcdCwrCommonScaleNormalizer.NormalizeVector(
                    rawCenter,
                    observation.Voltage208).Values))
            .ToArray();
        stepDetectionCenter = ChannelMedian(normalizedObservations);
        stepDetectionScale = ChannelMadScale(normalizedObservations, stepDetectionCenter);
        observationsSinceStepProfileRefresh = 0;
    }

    private AdaptiveThresholds CalculateAdaptiveThresholds(
        IReadOnlyList<double> earlyCenter,
        int earlyCount,
        int lateCount,
        double separationMinutes)
    {
        var center = ChannelMedian(observations);
        var times = observations.Select(observation => observation.ElapsedSeconds).ToArray();
        var scaleSeries = observations
            .Select(observation => EcdCwrCommonScaleNormalizer.EstimateRobustPositiveScale(
                center,
                observation.Voltage208))
            .ToArray();
        var commonScaleSigma = DetrendedMadScale(times, scaleSeries);
        // Difference between two independently estimated medians. The robust
        // sigma below is learned after linear detrending, so coherent drift
        // cannot widen its own acceptance threshold.
        var medianDifferenceFactor = MedianNormalStandardErrorFactor *
            Math.Sqrt((1.0 / earlyCount) + (1.0 / lateCount));
        var commonNoiseUncertainty = options.AdaptiveConfidenceZ *
            commonScaleSigma *
            medianDifferenceFactor /
            separationMinutes;

        var shapeNoiseEnergy = 0.0;
        var referenceEnergy = 0.0;
        var normalizedChannel = new double[observations.Count];
        for (var channel = 0; channel < center.Length; channel++)
        {
            for (var observation = 0; observation < observations.Count; observation++)
            {
                var scale = Math.Abs(scaleSeries[observation]) > double.Epsilon
                    ? scaleSeries[observation]
                    : 1.0;
                normalizedChannel[observation] = observations[observation].Voltage208[channel] / scale;
            }

            var channelSigma = DetrendedMadScale(times, normalizedChannel);
            var channelUncertainty = options.AdaptiveConfidenceZ * medianDifferenceFactor * channelSigma;
            shapeNoiseEnergy += channelUncertainty * channelUncertainty;
            referenceEnergy += earlyCenter[channel] * earlyCenter[channel];
        }

        var shapeNoiseUncertainty = referenceEnergy > double.Epsilon
            ? Math.Sqrt(shapeNoiseEnergy / referenceEnergy) / separationMinutes
            : 0.0;
        var unconstrainedCommonLimit = Math.Max(
            options.MaximumAbsoluteCommonScaleDriftPerMinute,
            commonNoiseUncertainty);
        var unconstrainedShapeLimit = Math.Max(
            options.MaximumShapeResidualPerMinute,
            shapeNoiseUncertainty);
        return new AdaptiveThresholds(
            Math.Min(unconstrainedCommonLimit, options.MaximumAdaptiveCommonScaleDriftPerMinute),
            Math.Min(unconstrainedShapeLimit, options.MaximumAdaptiveShapeResidualPerMinute),
            commonNoiseUncertainty,
            shapeNoiseUncertainty,
            unconstrainedCommonLimit > options.MaximumAdaptiveCommonScaleDriftPerMinute,
            unconstrainedShapeLimit > options.MaximumAdaptiveShapeResidualPerMinute);
    }

    private static double[] ChannelMedian(IReadOnlyList<Observation> source)
    {
        var center = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var column = new double[source.Count];
        for (var channel = 0; channel < center.Length; channel++)
        {
            for (var observation = 0; observation < source.Count; observation++)
            {
                column[observation] = source[observation].Voltage208[channel];
            }

            Array.Sort(column);
            var midpoint = column.Length / 2;
            center[channel] = column.Length % 2 == 0
                ? (column[midpoint - 1] + column[midpoint]) * 0.5
                : column[midpoint];
        }

        return center;
    }

    private static double[] ChannelMadScale(
        IReadOnlyList<Observation> source,
        IReadOnlyList<double> center)
    {
        var scale = new double[center.Count];
        var deviations = new double[source.Count];
        for (var channel = 0; channel < center.Count; channel++)
        {
            for (var observation = 0; observation < source.Count; observation++)
            {
                deviations[observation] = Math.Abs(
                    source[observation].Voltage208[channel] - center[channel]);
            }

            scale[channel] = Math.Max(
                Math.Max(Math.Abs(center[channel]) * 1.0e-6, 1.0e-12),
                MadNormalConsistencyFactor * Median(deviations));
        }

        return scale;
    }

    private static double DetrendedMadScale(
        IReadOnlyList<double> times,
        IReadOnlyList<double> values)
    {
        if (times.Count != values.Count || times.Count < 2)
        {
            return 0.0;
        }

        var meanTime = times.Average();
        var meanValue = values.Average();
        var covariance = 0.0;
        var timeEnergy = 0.0;
        for (var index = 0; index < times.Count; index++)
        {
            var centeredTime = times[index] - meanTime;
            covariance += centeredTime * (values[index] - meanValue);
            timeEnergy += centeredTime * centeredTime;
        }

        var slope = timeEnergy > double.Epsilon ? covariance / timeEnergy : 0.0;
        var residuals = new double[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            residuals[index] = values[index] - (meanValue + (slope * (times[index] - meanTime)));
        }

        var residualCenter = Median(residuals);
        for (var index = 0; index < residuals.Length; index++)
        {
            residuals[index] = Math.Abs(residuals[index] - residualCenter);
        }

        return MadNormalConsistencyFactor * Median(residuals);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var midpoint = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[midpoint - 1] + sorted[midpoint]) * 0.5
            : sorted[midpoint];
    }

    private static double RelativeResidual(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        double scale)
    {
        var residualEnergy = 0.0;
        var referenceEnergy = 0.0;
        for (var index = 0; index < reference.Count; index++)
        {
            var residual = target[index] - (scale * reference[index]);
            residualEnergy += residual * residual;
            referenceEnergy += reference[index] * reference[index];
        }

        return referenceEnergy > double.Epsilon
            ? Math.Sqrt(residualEnergy / referenceEnergy)
            : 0.0;
    }

    private static void ValidateOptions(EcdCwrReferenceStationarityOptions options)
    {
        if (!double.IsFinite(options.EvaluationWindowSeconds) ||
            options.EvaluationWindowSeconds <= 0.0 ||
            !double.IsFinite(options.MinimumDurationSeconds) ||
            options.MinimumDurationSeconds <= 0.0 ||
            options.MinimumDurationSeconds > options.EvaluationWindowSeconds ||
            options.MinimumObservationCount < 2 ||
            !double.IsFinite(options.EdgeFraction) ||
            options.EdgeFraction is <= 0.0 or > 0.5 ||
            !double.IsFinite(options.MaximumAbsoluteCommonScaleDriftPerMinute) ||
            options.MaximumAbsoluteCommonScaleDriftPerMinute <= 0.0 ||
            !double.IsFinite(options.MaximumShapeResidualPerMinute) ||
            options.MaximumShapeResidualPerMinute <= 0.0 ||
            options.RequiredStableUpdates <= 0 ||
            !double.IsFinite(options.AdaptiveConfidenceZ) ||
            options.AdaptiveConfidenceZ <= 0.0 ||
            !double.IsFinite(options.MaximumAdaptiveCommonScaleDriftPerMinute) ||
            options.MaximumAdaptiveCommonScaleDriftPerMinute < options.MaximumAbsoluteCommonScaleDriftPerMinute ||
            !double.IsFinite(options.MaximumAdaptiveShapeResidualPerMinute) ||
            options.MaximumAdaptiveShapeResidualPerMinute < options.MaximumShapeResidualPerMinute ||
            options.MinimumStepDetectionObservations < 2 ||
            !double.IsFinite(options.CoherentStepSigmaThreshold) ||
            options.CoherentStepSigmaThreshold <= 0.0 ||
            !double.IsFinite(options.CoherentStepMinimumChannelFraction) ||
            options.CoherentStepMinimumChannelFraction is <= 0.0 or > 1.0 ||
            !double.IsFinite(options.AdaptiveEvaluationIntervalSeconds) ||
            options.AdaptiveEvaluationIntervalSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private readonly record struct AdaptiveThresholds(
        double CommonScaleLimitPerMinute,
        double ShapeLimitPerMinute,
        double CommonScaleNoiseUncertaintyPerMinute,
        double ShapeNoiseUncertaintyPerMinute,
        bool CommonScaleLimitedBySafetyCeiling,
        bool ShapeLimitedBySafetyCeiling);

    private sealed record Observation(double ElapsedSeconds, double[] Voltage208);
}
