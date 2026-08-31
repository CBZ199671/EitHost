namespace EitHost.Core.Reconstruction;

public sealed record RealtimeReconstructionResult(
    int BlockNumber,
    string OutputHdf5Path,
    double[] Conductivity,
    double[,] NodeCoords,
    int[,] CellConnectivity,
    DateTimeOffset CompletedAt,
    TimeSpan BackendElapsed,
    string? ErrorMessage = null,
    bool OutputPersisted = true,
    double[]? MeasuredVoltageFit208 = null,
    double[]? SimulatedVoltageFit208 = null,
    double? WeightedSystemConditionNumber = null,
    double? VoltageFitResidualNorm = null,
    double? VoltageFitRelativeResidual = null,
    double? VoltageFitCosineSimilarity = null,
    double? VoltageFitResidualL1Norm = null,
    double? VoltageFitRelativeL1Residual = null,
    double? VoltageFitResidualLinfNorm = null,
    double? VoltageFitMeasuredNorm = null,
    double? VoltageFitSimulatedNorm = null,
    double? VoltageFitR2 = null,
    double? ReconstructionConductivityRange = null,
    double[]? RawConductivity = null,
    bool DynamicKalmanApplied = false,
    string? DynamicKalmanAction = null,
    double? DynamicKalmanNisPerDof = null,
    double? DynamicKalmanGainMean = null,
    double? DynamicKalmanVarianceInflation = null,
    int? DynamicKalmanUpdateCount = null,
    int? DynamicKalmanTotalLatencyFrames = null,
    string? DynamicKalmanMode = null,
    bool? DynamicKalmanFallback = null,
    double? DynamicKalmanSolveMilliseconds = null,
    double[,]? ContactJacobian = null,
    string? ContactJacobianMeasurementSpace = null,
    string? ContactJacobianStatus = null,
    string? ContactJacobianSource = null,
    string ReconstructionScaleStatus = ReconstructionScale.ModelRelative,
    string ReconstructionScaleProvenance = ReconstructionScale.NormalizedModelProvenance,
    ReconstructionMeshIndexMetadata? MeshIndexMetadata = null)
{
    private ReconstructionMeshIndexMetadata EffectiveMeshIndexMetadata =>
        MeshIndexMetadata ?? ReconstructionMeshIndexMetadata.LegacyCell;

    public bool Succeeded => string.IsNullOrWhiteSpace(ErrorMessage) && Conductivity.Length > 0;

    public string MeshIndexSchema => EffectiveMeshIndexMetadata.MeshIndexSchema;

    public string ParameterEntity => EffectiveMeshIndexMetadata.ParameterEntity;

    public string? LogicalMeshFingerprint => EffectiveMeshIndexMetadata.LogicalMeshFingerprint;

    public string? OrderedIndexFingerprint => EffectiveMeshIndexMetadata.OrderedIndexFingerprint;

    public int? CoordinateDecimals => EffectiveMeshIndexMetadata.CoordinateDecimals;

    public double? CoordinateQuantizationStep => EffectiveMeshIndexMetadata.CoordinateQuantizationStep;

    public bool UsesLegacyMeshContract => EffectiveMeshIndexMetadata.UsesLegacyContract;

    public ReconstructionMeshIndexMetadata GetMeshIndexMetadata() => EffectiveMeshIndexMetadata;

    public double MinConductivity => Conductivity.Length == 0 ? double.NaN : Conductivity.Min();

    public double MaxConductivity => Conductivity.Length == 0 ? double.NaN : Conductivity.Max();
}
