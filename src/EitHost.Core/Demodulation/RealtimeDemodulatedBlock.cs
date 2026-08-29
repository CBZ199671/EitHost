namespace EitHost.Core.Demodulation;

public sealed record RealtimeDemodulatedBlock(
    int BlockNumber,
    long StartSampleIndex,
    long EndSampleIndex,
    int ConsumedSampleCount,
    double EstimatedWindowSamples,
    int UniformOffsetSamples,
    int RotationStartChannelOneBased,
    int RotationDirection,
    int AcceptedFrameCount,
    int RejectedFrameCount,
    double QualityWeight,
    bool IsHighQuality,
    DemodulatedFrameAverage Average,
    IReadOnlyList<DemodulatedFrame> Frames,
    IReadOnlyList<int> PeakLocations,
    DemodulatedObservationAggregate? TrustedPartialAverage = null,
    DemodulatedObservationAggregate? DiagnosticAverage = null,
    bool UniformIntegrationStable = true,
    double UniformIntegrationInstability = 0.0)
{
    public double[] MeanAmplitude208 => Average.FlattenAmplitudesRowMajor();

    public double[] MeanReal208 => Average.FlattenRealRowMajor();

    public double[] MeanImaginary208 => Average.FlattenImaginaryRowMajor();

    public double[] MeanFullAmplitude256 => Average.FlattenFullAmplitudesRowMajor();

    public double[] MeanFullReal256 => Average.FlattenFullRealRowMajor();

    public double[] MeanFullImaginary256 => Average.FlattenFullImaginaryRowMajor();

    public int TrustedMeasurementCount => TrustedPartialAverage?.FiniteMeasurementCount
        ?? MeanAmplitude208.Count(double.IsFinite);

    public int DiagnosticMeasurementCount => DiagnosticAverage?.FiniteMeasurementCount
        ?? MeanAmplitude208.Count(double.IsFinite);
}
