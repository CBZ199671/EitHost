namespace EitHost.Core.Reconstruction;

public sealed record RealtimeReconstructionRequest
{
    public const int BoundaryVoltageCount = 208;
    public const string DefaultReconstructionRoute = "noser_rm";
    public const string DefaultDifferenceOrientation = "target_minus_reference";

    public RealtimeReconstructionRequest(
        string setLabel,
        int blockNumber,
        DateTimeOffset timestamp,
        IReadOnlyList<double> referenceVoltage208,
        IReadOnlyList<double> targetVoltage208,
        double excitationFrequencyHz,
        double excitationChannelCycles,
        double meshSize,
        double differenceLambda,
        bool persistResultFiles = false,
        string reconstructionRoute = DefaultReconstructionRoute,
        bool customLambdaEnabled = true,
        string differenceOrientation = DefaultDifferenceOrientation,
        IReadOnlyList<double>? measurementWeight208 = null,
        string weightPolicyVersion = "all-one-v1",
        RealtimeDynamicKalmanOptions? dynamicKalman = null,
        string reconstructionScaleStatus = ReconstructionScale.ModelRelative,
        string? reconstructionScaleProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentNullException.ThrowIfNull(referenceVoltage208);
        ArgumentNullException.ThrowIfNull(targetVoltage208);
        if (!double.IsFinite(excitationFrequencyHz) || excitationFrequencyHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(excitationFrequencyHz));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(excitationChannelCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(meshSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(differenceLambda);

        if (referenceVoltage208.Count != BoundaryVoltageCount)
        {
            throw new ArgumentException("Realtime reconstruction reference vector must contain 208 boundary-voltage values.", nameof(referenceVoltage208));
        }

        if (targetVoltage208.Count != BoundaryVoltageCount)
        {
            throw new ArgumentException("Realtime reconstruction target vector must contain 208 boundary-voltage values.", nameof(targetVoltage208));
        }

        measurementWeight208 ??= Enumerable.Repeat(1.0, BoundaryVoltageCount).ToArray();
        if (measurementWeight208.Count != BoundaryVoltageCount)
        {
            throw new ArgumentException("Realtime reconstruction measurement weights must contain 208 values.", nameof(measurementWeight208));
        }

        SetLabel = setLabel;
        BlockNumber = blockNumber;
        Timestamp = timestamp;
        ReferenceVoltage208 = referenceVoltage208.Select(ValidateFinite).ToArray();
        TargetVoltage208 = targetVoltage208.Select(ValidateFinite).ToArray();
        MeasurementWeight208 = measurementWeight208.Select(ValidateWeight).ToArray();
        ExcitationFrequencyHz = excitationFrequencyHz;
        ExcitationChannelCycles = excitationChannelCycles;
        MeshSize = meshSize;
        DifferenceLambda = differenceLambda;
        PersistResultFiles = persistResultFiles;
        ReconstructionRoute = NormalizeReconstructionRoute(reconstructionRoute);
        CustomLambdaEnabled = customLambdaEnabled;
        DifferenceOrientation = NormalizeDifferenceOrientation(differenceOrientation);
        WeightPolicyVersion = string.IsNullOrWhiteSpace(weightPolicyVersion)
            ? "all-one-v1"
            : weightPolicyVersion.Trim();
        DynamicKalman = dynamicKalman;
        ReconstructionScaleStatus = ReconstructionScale.NormalizeStatus(reconstructionScaleStatus);
        ReconstructionScaleProvenance = ReconstructionScale.NormalizeProvenance(
            ReconstructionScaleStatus,
            reconstructionScaleProvenance);
    }

    public string SetLabel { get; }

    public int BlockNumber { get; }

    public DateTimeOffset Timestamp { get; }

    public IReadOnlyList<double> ReferenceVoltage208 { get; }

    public IReadOnlyList<double> TargetVoltage208 { get; }

    public IReadOnlyList<double> MeasurementWeight208 { get; }

    public double ExcitationFrequencyHz { get; }

    public double ExcitationChannelCycles { get; }

    public double MeshSize { get; }

    public double DifferenceLambda { get; }

    public bool PersistResultFiles { get; }

    public string ReconstructionRoute { get; }

    public bool CustomLambdaEnabled { get; }

    public string DifferenceOrientation { get; }

    public string WeightPolicyVersion { get; }

    public RealtimeDynamicKalmanOptions? DynamicKalman { get; }

    public string ReconstructionScaleStatus { get; }

    public string ReconstructionScaleProvenance { get; }

    public static string NormalizeReconstructionRoute(string? route)
    {
        return route?.Trim() switch
        {
            "laplace_rm" => "laplace_rm",
            "curvature_rm" => "curvature_rm",
            "noser_rm" or "" or null => DefaultReconstructionRoute,
            _ => throw new ArgumentException("Unsupported realtime reconstruction route.", nameof(route))
        };
    }

    public static string NormalizeDifferenceOrientation(string? orientation)
    {
        return orientation?.Trim() switch
        {
            "reference_minus_target" => "reference_minus_target",
            "target_minus_reference" or "" or null => DefaultDifferenceOrientation,
            _ => throw new ArgumentException("Unsupported realtime difference orientation.", nameof(orientation))
        };
    }

    private static double ValidateFinite(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("Realtime reconstruction vectors must contain finite values only.");
        }

        return value;
    }

    private static double ValidateWeight(double value)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentException("Realtime reconstruction measurement weights must be finite values in [0, 1].");
        }

        return value;
    }
}
