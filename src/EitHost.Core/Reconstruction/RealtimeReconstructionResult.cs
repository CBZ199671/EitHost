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
    string ReconstructionScaleProvenance = ReconstructionScale.NormalizedModelProvenance)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(ErrorMessage) && Conductivity.Length > 0;

    public double MinConductivity => Conductivity.Length == 0 ? double.NaN : Conductivity.Min();

    public double MaxConductivity => Conductivity.Length == 0 ? double.NaN : Conductivity.Max();
}
