namespace EitHost.Core.Reconstruction;

public sealed record RealtimeDynamicKalmanOptions
{
    public const double SafeImageProcessNoiseRelativeStd = 0.05;
    public const double AdvancedMeasurementProcessNoiseRelativeStd = 0.15;
    public const double DefaultMeasurementNoiseRelativeStd = 0.10;

    public RealtimeDynamicKalmanOptions(
        string sessionId,
        string fingerprint,
        bool resetSession = false,
        bool innovationCandidate = false,
        int upstreamLatencyFrames = 2,
        double processNoiseRelativeStd = SafeImageProcessNoiseRelativeStd,
        double measurementNoiseRelativeStd = DefaultMeasurementNoiseRelativeStd,
        double initialRelativeStd = 0.50,
        double transitionDecayPerBlock = 1.0,
        string innovationGate = "inflate",
        double nisThresholdPerDof = 9.0,
        double maxVarianceInflation = 100.0,
        string mode = "auto",
        int maxMeasurementStateProduct = 2_000_000,
        double staticNoserAnchorRelativeStd = 0.10,
        double staticNoserAnchorMinimumGain = 0.75,
        double staticGuardRmsRatio = RealtimeDynamicKalmanStabilityGuard.SpatialRmsRatioLimit,
        double staticGuardRobustRatio = RealtimeDynamicKalmanStabilityGuard.RobustSpreadRatioLimit,
        double staticGuardMinimumDeviationRelative = RealtimeDynamicKalmanStabilityGuard.MinimumDeviationRelative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (upstreamLatencyFrames != 2)
        {
            throw new ArgumentOutOfRangeException(nameof(upstreamLatencyFrames), "Realtime Kalman requires exactly 2 upstream centered-latency blocks.");
        }

        ValidatePositive(processNoiseRelativeStd, nameof(processNoiseRelativeStd));
        ValidatePositive(measurementNoiseRelativeStd, nameof(measurementNoiseRelativeStd));
        ValidatePositive(initialRelativeStd, nameof(initialRelativeStd));
        ValidatePositive(nisThresholdPerDof, nameof(nisThresholdPerDof));
        ValidatePositive(staticNoserAnchorRelativeStd, nameof(staticNoserAnchorRelativeStd));
        ValidatePositive(staticGuardRmsRatio, nameof(staticGuardRmsRatio));
        ValidatePositive(staticGuardRobustRatio, nameof(staticGuardRobustRatio));
        ValidatePositive(staticGuardMinimumDeviationRelative, nameof(staticGuardMinimumDeviationRelative));
        if (!double.IsFinite(transitionDecayPerBlock) || transitionDecayPerBlock <= 0.0 || transitionDecayPerBlock > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionDecayPerBlock));
        }

        if (!double.IsFinite(maxVarianceInflation) || maxVarianceInflation < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVarianceInflation));
        }

        if (maxMeasurementStateProduct <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMeasurementStateProduct));
        }

        if (!double.IsFinite(staticNoserAnchorMinimumGain) || staticNoserAnchorMinimumGain is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(staticNoserAnchorMinimumGain));
        }

        var gate = innovationGate.Trim().ToLowerInvariant().Replace('-', '_');
        if (gate is not ("none" or "reject" or "inflate"))
        {
            throw new ArgumentException("Realtime Kalman innovation gate must be none, reject, or inflate.", nameof(innovationGate));
        }

        var normalizedMode = NormalizeMode(mode);

        SessionId = sessionId.Trim();
        Fingerprint = fingerprint.Trim();
        ResetSession = resetSession;
        InnovationCandidate = innovationCandidate;
        UpstreamLatencyFrames = upstreamLatencyFrames;
        ProcessNoiseRelativeStd = processNoiseRelativeStd;
        MeasurementNoiseRelativeStd = measurementNoiseRelativeStd;
        InitialRelativeStd = initialRelativeStd;
        TransitionDecayPerBlock = transitionDecayPerBlock;
        InnovationGate = gate;
        NisThresholdPerDof = nisThresholdPerDof;
        MaxVarianceInflation = maxVarianceInflation;
        Mode = normalizedMode;
        MaxMeasurementStateProduct = maxMeasurementStateProduct;
        StaticNoserAnchorRelativeStd = staticNoserAnchorRelativeStd;
        StaticNoserAnchorMinimumGain = staticNoserAnchorMinimumGain;
        StaticGuardRmsRatio = staticGuardRmsRatio;
        StaticGuardRobustRatio = staticGuardRobustRatio;
        StaticGuardMinimumDeviationRelative = staticGuardMinimumDeviationRelative;
    }

    public string SessionId { get; }

    public string Fingerprint { get; }

    public bool ResetSession { get; }

    public bool InnovationCandidate { get; }

    public int UpstreamLatencyFrames { get; }

    public double ProcessNoiseRelativeStd { get; }

    public double MeasurementNoiseRelativeStd { get; }

    public double InitialRelativeStd { get; }

    public double TransitionDecayPerBlock { get; }

    public string InnovationGate { get; }

    public double NisThresholdPerDof { get; }

    public double MaxVarianceInflation { get; }

    public string Mode { get; }

    public int MaxMeasurementStateProduct { get; }

    public double StaticNoserAnchorRelativeStd { get; }

    public double StaticNoserAnchorMinimumGain { get; }

    public double StaticGuardRmsRatio { get; }

    public double StaticGuardRobustRatio { get; }

    public double StaticGuardMinimumDeviationRelative { get; }

    public static string NormalizeMode(string? value)
    {
        return value?.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "auto" => "auto",
            "measurement" or "measurement_space" or "advanced" => "measurement",
            "fast" or "image" or "fast_image" or null or "" => "fast_image",
            _ => throw new ArgumentException("Realtime Kalman mode must be auto, measurement, or fast_image.", nameof(value))
        };
    }

    private static void ValidatePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
