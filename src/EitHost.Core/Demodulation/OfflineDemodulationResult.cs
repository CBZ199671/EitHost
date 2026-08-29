namespace EitHost.Core.Demodulation;

public sealed record OfflineDemodulationResult(
    IReadOnlyList<int> PeakLocations,
    IReadOnlyList<DemodulatedFrame> Frames,
    DemodulatedFrameAverage Average,
    bool UsedUniformCadence,
    int UniformOffsetSamples,
    double EstimatedWindowSamples,
    DemodulatedObservationAggregate? TrustedPartialAverage = null,
    DemodulatedObservationAggregate? DiagnosticAverage = null,
    bool UniformIntegrationStable = true,
    double UniformIntegrationInstability = 0.0,
    string? BoundaryProvenance = null);
