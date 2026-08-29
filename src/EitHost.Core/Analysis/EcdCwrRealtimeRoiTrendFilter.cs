namespace EitHost.Core.Analysis;

public sealed record EcdCwrRoiNoiseModel(
    double Center,
    double Sigma,
    double SigmaMultiplier,
    int SampleCount,
    string PolicyVersion);

public sealed record EcdCwrRoiTrendPoint(
    double DespikedMeanConductivity,
    double TrendMeanConductivity,
    bool IsOutsideNoiseBand,
    bool IsSustainedEvent);

public sealed record EcdCwrRealtimeRoiTrendResult(
    IReadOnlyList<EcdCwrRoiTrendPoint> Points,
    EcdCwrRoiNoiseModel? NoiseModel,
    string PolicyVersion);

public sealed record EcdCwrRealtimeRoiTrendOptions(
    double TrustedQualityThreshold = 0.65,
    int NoiseWarmupSampleCount = 30,
    double NoiseSigmaMultiplier = 3.0,
    int EventConsecutiveSamples = 3,
    double AbsoluteNoiseFloor = 1.0e-9,
    double RelativeNoiseFloor = 1.0e-6);

public sealed class EcdCwrRealtimeRoiTrendFilter
{
    private static readonly int[] BinomialWeights = [1, 4, 6, 4, 1];
    public const string PolicyVersion = "ecd-cwr-roi-trend-v1:median3+binomial5";

    public EcdCwrRealtimeRoiTrendResult Analyze(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        IReadOnlyList<EcdCwrRoiCurveFilterPoint> despiked,
        EcdCwrRoiNoiseModel? fixedNoiseModel = null,
        EcdCwrRealtimeRoiTrendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(despiked);
        options ??= new EcdCwrRealtimeRoiTrendOptions();
        ValidateOptions(options);
        if (samples.Count != despiked.Count)
        {
            throw new ArgumentException("ROI trend samples and despiked points must have the same length.");
        }

        if (samples.Any(sample =>
                sample is null ||
                !double.IsFinite(sample.RawMeanConductivity) ||
                !double.IsFinite(sample.QualityWeight) ||
                sample.QualityWeight < 0.0 ||
                sample.QualityWeight > 1.0) ||
            despiked.Any(point =>
                point is null ||
                !double.IsFinite(point.DisplayMeanConductivity)))
        {
            throw new ArgumentException("ROI trend input contains invalid values.");
        }

        var medianFiltered = CreateMedianThree(despiked);
        var trend = CreateBinomialFive(medianFiltered);
        var noiseModel = fixedNoiseModel ?? TryCreateNoiseModel(
            samples,
            despiked.Select(point => point.DisplayMeanConductivity).ToArray(),
            options);
        ValidateNoiseModel(noiseModel);

        var points = new EcdCwrRoiTrendPoint[samples.Count];
        var outsideStreak = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var outside = noiseModel is not null &&
                Math.Abs(trend[index] - noiseModel.Center) >
                noiseModel.SigmaMultiplier * noiseModel.Sigma;
            outsideStreak = outside ? outsideStreak + 1 : 0;
            points[index] = new EcdCwrRoiTrendPoint(
                despiked[index].DisplayMeanConductivity,
                trend[index],
                outside,
                outsideStreak >= options.EventConsecutiveSamples);
        }

        return new EcdCwrRealtimeRoiTrendResult(points, noiseModel, PolicyVersion);
    }

    public EcdCwrRoiNoiseModel? TryCreateNoiseModel(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        IReadOnlyList<double> despikedValues,
        EcdCwrRealtimeRoiTrendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(despikedValues);
        options ??= new EcdCwrRealtimeRoiTrendOptions();
        ValidateOptions(options);
        if (samples.Count != despikedValues.Count ||
            samples.Any(sample =>
                sample is null ||
                !double.IsFinite(sample.RawMeanConductivity) ||
                !double.IsFinite(sample.QualityWeight) ||
                sample.QualityWeight < 0.0 ||
                sample.QualityWeight > 1.0) ||
            despikedValues.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("ROI noise-model samples and despiked values must be finite and aligned.");
        }

        return CreateNoiseModel(samples, despikedValues, options);
    }

    private static double[] CreateMedianThree(IReadOnlyList<EcdCwrRoiCurveFilterPoint> points)
    {
        var filtered = new double[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var first = Math.Max(0, index - 1);
            var last = Math.Min(points.Count - 1, index + 1);
            filtered[index] = Median(Enumerable.Range(first, last - first + 1)
                .Select(sample => points[sample].DisplayMeanConductivity)
                .ToArray());
        }

        return filtered;
    }

    private static double[] CreateBinomialFive(IReadOnlyList<double> values)
    {
        var trend = new double[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var weightedSum = 0.0;
            var totalWeight = 0;
            for (var offset = -2; offset <= 2; offset++)
            {
                var sample = index + offset;
                if (sample < 0 || sample >= values.Count)
                {
                    continue;
                }

                var weight = BinomialWeights[offset + 2];
                weightedSum += values[sample] * weight;
                totalWeight += weight;
            }

            trend[index] = weightedSum / totalWeight;
        }

        return trend;
    }

    private static EcdCwrRoiNoiseModel? CreateNoiseModel(
        IReadOnlyList<EcdCwrRoiCurveSample> samples,
        IReadOnlyList<double> despikedValues,
        EcdCwrRealtimeRoiTrendOptions options)
    {
        var trusted = samples
            .Select((sample, index) => (sample, index))
            .Where(item =>
                item.sample.IsNoiseBaselineEligible &&
                item.sample.QualityWeight >= options.TrustedQualityThreshold)
            .Take(options.NoiseWarmupSampleCount)
            .Select(item => despikedValues[item.index])
            .ToArray();
        if (trusted.Length < options.NoiseWarmupSampleCount)
        {
            return null;
        }

        var center = Median(trusted);
        var mad = Median(trusted.Select(value => Math.Abs(value - center)).ToArray());
        var sigma = Math.Max(
            options.AbsoluteNoiseFloor,
            Math.Max(options.RelativeNoiseFloor * Math.Abs(center), 1.4826 * mad));
        return new EcdCwrRoiNoiseModel(
            center,
            sigma,
            options.NoiseSigmaMultiplier,
            trusted.Length,
            PolicyVersion);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? 0.5 * (ordered[middle - 1] + ordered[middle])
            : ordered[middle];
    }

    private static void ValidateNoiseModel(EcdCwrRoiNoiseModel? model)
    {
        if (model is not null &&
            (!double.IsFinite(model.Center) ||
             !double.IsFinite(model.Sigma) ||
             model.Sigma <= 0.0 ||
             !double.IsFinite(model.SigmaMultiplier) ||
             model.SigmaMultiplier <= 0.0 ||
             model.SampleCount <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(model), "ROI noise model is invalid.");
        }
    }

    private static void ValidateOptions(EcdCwrRealtimeRoiTrendOptions options)
    {
        if (!double.IsFinite(options.TrustedQualityThreshold) ||
            options.TrustedQualityThreshold <= 0.0 ||
            options.TrustedQualityThreshold > 1.0 ||
            options.NoiseWarmupSampleCount <= 0 ||
            !double.IsFinite(options.NoiseSigmaMultiplier) ||
            options.NoiseSigmaMultiplier <= 0.0 ||
            options.EventConsecutiveSamples <= 0 ||
            !double.IsFinite(options.AbsoluteNoiseFloor) ||
            options.AbsoluteNoiseFloor <= 0.0 ||
            !double.IsFinite(options.RelativeNoiseFloor) ||
            options.RelativeNoiseFloor <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Realtime ROI trend options are invalid.");
        }
    }
}
