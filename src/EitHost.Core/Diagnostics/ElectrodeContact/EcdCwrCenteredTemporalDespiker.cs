namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrCenteredTemporalDespiker
{
    public const int WindowSize = 5;
    public const int CenterIndex = 2;
    public const int MeasurementCount = 208;

    public EcdCwrTemporalDespikingResult Analyze(
        IReadOnlyList<IReadOnlyList<double>> frames,
        IReadOnlyList<double>? contactWeights = null,
        EcdCwrTemporalDespikingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new EcdCwrTemporalDespikingOptions();
        ValidateOptions(options);
        if (frames.Count != WindowSize)
        {
            throw new ArgumentException("Centered temporal despiking requires exactly 5 frames.", nameof(frames));
        }

        foreach (var frame in frames)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Count != MeasurementCount)
            {
                throw new ArgumentException("Each temporal despiking frame must contain 208 measurements.", nameof(frames));
            }

            if (frame.Any(value => !double.IsFinite(value)))
            {
                throw new ArgumentException("Temporal despiking frames must contain finite values only.", nameof(frames));
            }
        }

        contactWeights ??= Enumerable.Repeat(1.0, MeasurementCount).ToArray();
        if (contactWeights.Count != MeasurementCount ||
            contactWeights.Any(weight => !double.IsFinite(weight) || weight < 0.0 || weight > 1.0))
        {
            throw new ArgumentException("Temporal despiking contact weights must contain 208 finite values in [0, 1].", nameof(contactWeights));
        }

        var isolated = new bool[MeasurementCount];
        var excursionScores = new double[MeasurementCount];
        var returnScores = new double[MeasurementCount];
        var temporalWeights = Enumerable.Repeat(1.0, MeasurementCount).ToArray();
        var combinedWeights = new double[MeasurementCount];
        var rawCenter = frames[CenterIndex].ToArray();
        var repairCandidate = rawCenter.ToArray();
        var isolatedCount = 0;
        for (var channel = 0; channel < MeasurementCount; channel++)
        {
            var first = frames[0][channel];
            var left = frames[1][channel];
            var center = frames[2][channel];
            var right = frames[3][channel];
            var last = frames[4][channel];
            var pre = 0.5 * (first + left);
            var post = 0.5 * (right + last);
            var expected = 0.5 * (pre + post);
            var neighbors = new[] { first, left, right, last };
            var median = Median4(neighbors);
            var medianAbsolute = Median4(neighbors.Select(Math.Abs).ToArray());
            var mad = Median4(neighbors.Select(value => Math.Abs(value - median)).ToArray());
            var scale = Math.Max(
                options.AbsoluteScaleFloor,
                Math.Max(options.RelativeScaleFloor * medianAbsolute, 1.4826 * mad));
            var excursion = Math.Abs(center - expected) / scale;
            var returnScore = Math.Abs(pre - post) / scale;
            excursionScores[channel] = excursion;
            returnScores[channel] = returnScore;

            var reversesDirection = (center - pre) * (post - center) < 0.0;
            var outerBaseline = 0.5 * (first + last);
            var centerDelta = center - outerBaseline;
            var leftDelta = left - outerBaseline;
            var rightDelta = right - outerBaseline;
            var persistentShoulders = HasPersistentShoulders(
                centerDelta,
                leftDelta,
                rightDelta,
                options.PersistenceShoulderFraction);
            var isIsolated = reversesDirection &&
                !persistentShoulders &&
                excursion >= options.ExcursionThreshold &&
                returnScore <= options.ReturnThreshold;
            isolated[channel] = isIsolated;
            if (isIsolated)
            {
                isolatedCount++;
                repairCandidate[channel] = expected;
                var normalized = excursion / options.ExcursionThreshold;
                var mapped = 1.0 / (1.0 + Math.Pow(normalized, options.WeightPower));
                temporalWeights[channel] = Math.Clamp(mapped, options.MinimumWeight, 1.0);
            }

            combinedWeights[channel] = Math.Min(contactWeights[channel], temporalWeights[channel]);
        }

        var globalThreshold = Math.Max(1, (int)Math.Ceiling(MeasurementCount * options.GlobalIsolatedFraction));
        var isGlobal = isolatedCount >= globalThreshold;
        var repairedCenter = isGlobal ? rawCenter : repairCandidate;
        var repairedChannelIndices = isGlobal
            ? []
            : isolated
                .Select((repair, index) => (repair, index))
                .Where(item => item.repair)
                .Select(item => item.index)
                .ToArray();
        return new EcdCwrTemporalDespikingResult(
            isolated,
            excursionScores,
            returnScores,
            temporalWeights,
            combinedWeights,
            isolatedCount,
            isGlobal,
            excursionScores.Max(),
            CreatePolicyVersion(options),
            repairedCenter,
            repairedChannelIndices);
    }

    public static string CreatePolicyVersion(EcdCwrTemporalDespikingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FormattableString.Invariant(
            $"ecd-cwr-centered5-v2:repair=shoulder4:rel={options.RelativeScaleFloor:G4}:exc={options.ExcursionThreshold:G4}:ret={options.ReturnThreshold:G4}:shoulder={options.PersistenceShoulderFraction:G4}:min={options.MinimumWeight:G4}");
    }

    private static bool HasPersistentShoulders(
        double centerDelta,
        double leftDelta,
        double rightDelta,
        double minimumFraction)
    {
        var centerMagnitude = Math.Abs(centerDelta);
        if (centerMagnitude <= double.Epsilon)
        {
            return false;
        }

        var sameDirection = Math.Sign(leftDelta) == Math.Sign(centerDelta) &&
            Math.Sign(rightDelta) == Math.Sign(centerDelta);
        return sameDirection &&
            Math.Min(Math.Abs(leftDelta), Math.Abs(rightDelta)) >= minimumFraction * centerMagnitude;
    }

    private static double Median4(double[] values)
    {
        Array.Sort(values);
        return 0.5 * (values[1] + values[2]);
    }

    private static void ValidateOptions(EcdCwrTemporalDespikingOptions options)
    {
        if (!double.IsFinite(options.AbsoluteScaleFloor) || options.AbsoluteScaleFloor <= 0.0 ||
            !double.IsFinite(options.RelativeScaleFloor) || options.RelativeScaleFloor <= 0.0 ||
            !double.IsFinite(options.ExcursionThreshold) || options.ExcursionThreshold <= 0.0 ||
            !double.IsFinite(options.ReturnThreshold) || options.ReturnThreshold < 0.0 ||
            !double.IsFinite(options.PersistenceShoulderFraction) || options.PersistenceShoulderFraction < 0.0 ||
            options.PersistenceShoulderFraction > 1.0 ||
            !double.IsFinite(options.MinimumWeight) || options.MinimumWeight < 0.0 || options.MinimumWeight > 1.0 ||
            !double.IsFinite(options.WeightPower) || options.WeightPower <= 0.0 ||
            !double.IsFinite(options.GlobalIsolatedFraction) || options.GlobalIsolatedFraction <= 0.0 ||
            options.GlobalIsolatedFraction > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Temporal despiking options are outside their valid ranges.");
        }
    }
}

public sealed record EcdCwrTemporalDespikingOptions(
    double AbsoluteScaleFloor = 1.0e-12,
    double RelativeScaleFloor = 0.03,
    double ExcursionThreshold = 4.0,
    double ReturnThreshold = 1.5,
    double PersistenceShoulderFraction = 0.08,
    double MinimumWeight = 0.02,
    double WeightPower = 2.0,
    double GlobalIsolatedFraction = 0.25);

public sealed record EcdCwrTemporalDespikingResult(
    bool[] IsolatedChannels,
    double[] ExcursionScores,
    double[] ReturnScores,
    double[] TemporalMeasurementWeight208,
    double[] CombinedMeasurementWeight208,
    int IsolatedChannelCount,
    bool IsGlobalIsolatedSpike,
    double MaximumExcursionScore,
    string WeightPolicyVersion,
    double[] RepairedCenter208,
    int[] RepairedChannelIndices);
