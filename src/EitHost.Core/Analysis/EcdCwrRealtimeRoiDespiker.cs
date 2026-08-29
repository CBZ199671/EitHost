namespace EitHost.Core.Analysis;

public enum EcdCwrRoiFilterState
{
    Raw,
    ProvisionalHold,
    RepairedIsolated,
    RepairedShortBurst,
    RestoredNonIsolated,
}

public sealed record EcdCwrRoiCurveSample(
    double RawMeanConductivity,
    double QualityWeight,
    bool IsNoiseBaselineEligible = true);

public sealed record EcdCwrRoiCurveFilterPoint(
    double RawMeanConductivity,
    double DisplayMeanConductivity,
    EcdCwrRoiFilterState State,
    double ExcursionScore,
    double ReturnScore);

public sealed record EcdCwrRealtimeRoiDespikingOptions(
    double LowConfidenceThreshold = 0.65,
    double AbsoluteScaleFloor = 1.0e-8,
    double RelativeScaleFloor = 5.0e-4,
    double ExcursionThreshold = 4.0,
    double HighConfidenceExcursionThreshold = 6.0,
    double ReturnThreshold = 2.5,
    double PersistenceShoulderFraction = 0.08,
    int BaselineSampleCount = 5,
    int FutureConfirmationCount = 2,
    int QualityBaselineSampleCount = 5,
    double RelativeQualityDropThreshold = 0.08,
    double BurstSeedExcursionThreshold = 2.5,
    double BurstRecoveryThreshold = 2.0,
    int BurstMaximumLength = 7,
    int BurstRecoverySampleCount = 2)
{
    public int MaximumDecisionLag => Math.Max(
        FutureConfirmationCount,
        BurstMaximumLength + BurstRecoverySampleCount - 1);
}

public sealed class EcdCwrRealtimeRoiDespiker
{
    public const string PolicyVersion = "ecd-cwr-roi-display-v3:burst7-relativeq+highq-isolated";

    public IReadOnlyList<EcdCwrRoiCurveFilterPoint> Analyze(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        EcdCwrRealtimeRoiDespikingOptions? options = null,
        EcdCwrRoiNoiseModel? noiseModel = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        options ??= new EcdCwrRealtimeRoiDespikingOptions();
        ValidateOptions(options);
        ValidateNoiseModel(noiseModel);

        if (samples.Any(sample =>
                sample is null ||
                !double.IsFinite(sample.RawMeanConductivity) ||
                !double.IsFinite(sample.QualityWeight) ||
                sample.QualityWeight < 0.0 ||
                sample.QualityWeight > 1.0))
        {
            throw new ArgumentException(
                "ROI curve samples require finite conductivity and quality in [0, 1].",
                nameof(samples));
        }

        var filtered = new EcdCwrRoiCurveFilterPoint[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            filtered[index] = AnalyzeSample(samples, index, options, noiseModel);
        }

        ApplyShortBurstRepairs(samples, filtered, options, noiseModel);
        return filtered;
    }

    private static void ApplyShortBurstRepairs(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        EcdCwrRoiCurveFilterPoint[] filtered,
        EcdCwrRealtimeRoiDespikingOptions options,
        EcdCwrRoiNoiseModel? noiseModel)
    {
        for (var seed = 0; seed < samples.Count; seed++)
        {
            if (!IsLowConfidenceSeed(samples, seed, options))
            {
                continue;
            }

            var baseline = CollectTrustedBaseline(samples, seed, options);
            if (baseline.Count == 0)
            {
                continue;
            }

            var baselineCenter = Median(baseline);
            var scale = RobustScale(baseline, options, noiseModel?.Sigma ?? 0.0);
            var seedExcursion = Math.Abs(samples[seed].RawMeanConductivity - baselineCenter) / scale;
            if (seedExcursion < options.BurstSeedExcursionThreshold)
            {
                continue;
            }

            var latestRecoveryStart = samples.Count - options.BurstRecoverySampleCount;
            var lastCandidate = Math.Min(seed + options.BurstMaximumLength, latestRecoveryStart);
            var recoveryStart = -1;
            for (var candidate = seed + 1; candidate <= lastCandidate; candidate++)
            {
                var recovered = Enumerable.Range(candidate, options.BurstRecoverySampleCount)
                    .All(index =>
                        Math.Abs(samples[index].RawMeanConductivity - baselineCenter) <=
                        options.BurstRecoveryThreshold * scale);
                if (recovered)
                {
                    recoveryStart = candidate;
                    break;
                }
            }

            if (recoveryStart < 0)
            {
                continue;
            }

            var burstLength = recoveryStart - seed;
            var postAnchor = Enumerable.Range(recoveryStart, options.BurstRecoverySampleCount)
                .Average(index => samples[index].RawMeanConductivity);
            var returnScore = Math.Abs(postAnchor - baselineCenter) / scale;
            var reportedExcursion = filtered[seed].ExcursionScore > 0.0
                ? filtered[seed].ExcursionScore
                : seedExcursion;
            for (var index = seed; index < recoveryStart; index++)
            {
                var interpolation = (double)(index - seed + 1) / (burstLength + 1);
                var repaired = baselineCenter + ((postAnchor - baselineCenter) * interpolation);
                filtered[index] = new EcdCwrRoiCurveFilterPoint(
                    samples[index].RawMeanConductivity,
                    repaired,
                    EcdCwrRoiFilterState.RepairedShortBurst,
                    reportedExcursion,
                    returnScore);
            }

            seed = recoveryStart - 1;
        }
    }

    private static EcdCwrRoiCurveFilterPoint AnalyzeSample(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        int index,
        EcdCwrRealtimeRoiDespikingOptions options,
        EcdCwrRoiNoiseModel? noiseModel)
    {
        var sample = samples[index];
        var raw = sample.RawMeanConductivity;
        var lowConfidenceSeed = IsLowConfidenceSeed(samples, index, options);
        if (!lowConfidenceSeed && noiseModel is null)
        {
            return Raw(raw);
        }

        var baseline = CollectTrustedBaseline(samples, index, options);
        if (baseline.Count == 0)
        {
            return Raw(raw);
        }

        var baselineCenter = Median(baseline);
        var baselineScale = RobustScale(baseline, options, noiseModel?.Sigma ?? 0.0);
        var initialExcursion = Math.Abs(raw - baselineCenter) / baselineScale;
        var excursionThreshold = lowConfidenceSeed
            ? options.ExcursionThreshold
            : options.HighConfidenceExcursionThreshold;
        if (initialExcursion < excursionThreshold)
        {
            return Raw(raw);
        }

        if (index + options.FutureConfirmationCount >= samples.Count)
        {
            return new EcdCwrRoiCurveFilterPoint(
                raw,
                baselineCenter,
                EcdCwrRoiFilterState.ProvisionalHold,
                initialExcursion,
                0.0);
        }

        var left = Enumerable.Range(Math.Max(0, index - 2), Math.Min(2, index))
            .Select(leftIndex => samples[leftIndex].RawMeanConductivity)
            .ToArray();
        var right = Enumerable.Range(index + 1, options.FutureConfirmationCount)
            .Select(rightIndex => samples[rightIndex].RawMeanConductivity)
            .ToArray();
        var shoulders = left.Concat(right).ToArray();
        var pre = left.Average();
        var post = right.Average();
        var expected = shoulders.Average();
        var scale = RobustScale(shoulders, options, noiseModel?.Sigma ?? 0.0);
        var excursion = Math.Abs(raw - expected) / scale;
        var returnScore = Math.Abs(pre - post) / scale;
        var reversesDirection = (raw - pre) * (post - raw) < 0.0;
        var futureIsTrusted = Enumerable.Range(index + 1, options.FutureConfirmationCount)
            .All(futureIndex => samples[futureIndex].QualityWeight >= options.LowConfidenceThreshold);
        var persistentShoulders = HasPersistentShoulders(left, raw, right, options.PersistenceShoulderFraction);
        var isIsolated = futureIsTrusted &&
            reversesDirection &&
            !persistentShoulders &&
            excursion >= excursionThreshold &&
            returnScore <= options.ReturnThreshold;

        return new EcdCwrRoiCurveFilterPoint(
            raw,
            isIsolated ? expected : raw,
            isIsolated
                ? EcdCwrRoiFilterState.RepairedIsolated
                : EcdCwrRoiFilterState.RestoredNonIsolated,
            excursion,
            returnScore);
    }

    private static List<double> CollectTrustedBaseline(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        int index,
        EcdCwrRealtimeRoiDespikingOptions options)
    {
        var baseline = new List<double>(options.BaselineSampleCount);
        for (var prior = index - 1; prior >= 0 && baseline.Count < options.BaselineSampleCount; prior--)
        {
            if (samples[prior].QualityWeight >= options.LowConfidenceThreshold)
            {
                baseline.Add(samples[prior].RawMeanConductivity);
            }
        }

        return baseline;
    }

    private static bool IsLowConfidenceSeed(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        int index,
        EcdCwrRealtimeRoiDespikingOptions options)
    {
        var quality = samples[index].QualityWeight;
        if (quality < options.LowConfidenceThreshold)
        {
            return true;
        }

        var start = Math.Max(0, index - options.QualityBaselineSampleCount);
        var priorQuality = Enumerable.Range(start, index - start)
            .Select(prior => samples[prior].QualityWeight)
            .Where(prior => prior >= options.LowConfidenceThreshold)
            .ToArray();
        return priorQuality.Length >= 3 &&
            Median(priorQuality) - quality >= options.RelativeQualityDropThreshold;
    }

    private static bool HasPersistentShoulders(
        IReadOnlyList<double> left,
        double center,
        IReadOnlyList<double> right,
        double minimumFraction)
    {
        var outerBaseline = 0.5 * (left[0] + right[^1]);
        var centerDelta = center - outerBaseline;
        var centerMagnitude = Math.Abs(centerDelta);
        if (centerMagnitude <= double.Epsilon)
        {
            return false;
        }

        var leftDelta = left[^1] - outerBaseline;
        var rightDelta = right[0] - outerBaseline;
        return Math.Sign(leftDelta) == Math.Sign(centerDelta) &&
            Math.Sign(rightDelta) == Math.Sign(centerDelta) &&
            Math.Min(Math.Abs(leftDelta), Math.Abs(rightDelta)) >= minimumFraction * centerMagnitude;
    }

    private static double RobustScale(
        IReadOnlyList<double> values,
        EcdCwrRealtimeRoiDespikingOptions options,
        double externalScaleFloor)
    {
        var median = Median(values);
        var medianAbsolute = Median(values.Select(Math.Abs).ToArray());
        var mad = Median(values.Select(value => Math.Abs(value - median)).ToArray());
        var relativeScaleFloor = externalScaleFloor > 0.0
            ? externalScaleFloor
            : options.RelativeScaleFloor * medianAbsolute;
        return Math.Max(
            Math.Max(options.AbsoluteScaleFloor, externalScaleFloor),
            Math.Max(relativeScaleFloor, 1.4826 * mad));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? 0.5 * (ordered[middle - 1] + ordered[middle])
            : ordered[middle];
    }

    private static EcdCwrRoiCurveFilterPoint Raw(double value)
    {
        return new EcdCwrRoiCurveFilterPoint(
            value,
            value,
            EcdCwrRoiFilterState.Raw,
            0.0,
            0.0);
    }

    private static void ValidateOptions(EcdCwrRealtimeRoiDespikingOptions options)
    {
        if (!double.IsFinite(options.LowConfidenceThreshold) ||
            options.LowConfidenceThreshold <= 0.0 ||
            options.LowConfidenceThreshold > 1.0 ||
            !double.IsFinite(options.AbsoluteScaleFloor) ||
            options.AbsoluteScaleFloor <= 0.0 ||
            !double.IsFinite(options.RelativeScaleFloor) ||
            options.RelativeScaleFloor <= 0.0 ||
            !double.IsFinite(options.ExcursionThreshold) ||
            options.ExcursionThreshold <= 0.0 ||
            !double.IsFinite(options.HighConfidenceExcursionThreshold) ||
            options.HighConfidenceExcursionThreshold <= options.ExcursionThreshold ||
            !double.IsFinite(options.ReturnThreshold) ||
            options.ReturnThreshold < 0.0 ||
            !double.IsFinite(options.PersistenceShoulderFraction) ||
            options.PersistenceShoulderFraction < 0.0 ||
            options.PersistenceShoulderFraction > 1.0 ||
            options.BaselineSampleCount <= 0 ||
            options.FutureConfirmationCount != 2 ||
            options.QualityBaselineSampleCount < 3 ||
            !double.IsFinite(options.RelativeQualityDropThreshold) ||
            options.RelativeQualityDropThreshold <= 0.0 ||
            options.RelativeQualityDropThreshold >= 1.0 ||
            !double.IsFinite(options.BurstSeedExcursionThreshold) ||
            options.BurstSeedExcursionThreshold <= 0.0 ||
            !double.IsFinite(options.BurstRecoveryThreshold) ||
            options.BurstRecoveryThreshold <= 0.0 ||
            options.BurstMaximumLength <= 0 ||
            options.BurstRecoverySampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Realtime ROI despiking options are outside their valid ranges.");
        }
    }

    private static void ValidateNoiseModel(EcdCwrRoiNoiseModel? noiseModel)
    {
        if (noiseModel is not null &&
            (!double.IsFinite(noiseModel.Center) ||
             !double.IsFinite(noiseModel.Sigma) ||
             noiseModel.Sigma <= 0.0 ||
             !double.IsFinite(noiseModel.SigmaMultiplier) ||
             noiseModel.SigmaMultiplier <= 0.0 ||
             noiseModel.SampleCount <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(noiseModel));
        }
    }
}
