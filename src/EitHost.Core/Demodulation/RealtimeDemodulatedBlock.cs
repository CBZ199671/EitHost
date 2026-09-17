using System.Runtime.CompilerServices;
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
    public Acquisition.Pseudo3dAcquisitionStamp? TimeDivision { get; init; }

    // Average and the exposed vectors are publish-once/read-only. A with-expression
    // replacing Average gets a different cache, rather than inheriting stale vectors.
    private static readonly ConditionalWeakTable<DemodulatedFrameAverage, Vectors> Cache = new();
    private Vectors Flat => Cache.GetValue(Average, static average => new Vectors(average));
    public double[] MeanAmplitude208 => Flat.Amplitude.Value;

    public double[] MeanReal208 => Flat.Real.Value;

    public double[] MeanImaginary208 => Flat.Imaginary.Value;

    public double[] MeanFullAmplitude256 => Flat.FullAmplitude.Value;

    public double[] MeanFullReal256 => Flat.FullReal.Value;

    public double[] MeanFullImaginary256 => Flat.FullImaginary.Value;

    public int TrustedMeasurementCount => TrustedPartialAverage?.FiniteMeasurementCount
        ?? MeanAmplitude208.Count(double.IsFinite);

    public int DiagnosticMeasurementCount => DiagnosticAverage?.FiniteMeasurementCount
        ?? MeanAmplitude208.Count(double.IsFinite);
    private sealed class Vectors(DemodulatedFrameAverage average)
    {
        internal readonly Lazy<double[]> Amplitude = new(average.FlattenAmplitudesRowMajor);
        internal readonly Lazy<double[]> Real = new(average.FlattenRealRowMajor);
        internal readonly Lazy<double[]> Imaginary = new(average.FlattenImaginaryRowMajor);
        internal readonly Lazy<double[]> FullAmplitude = new(average.FlattenFullAmplitudesRowMajor);
        internal readonly Lazy<double[]> FullReal = new(average.FlattenFullRealRowMajor);
        internal readonly Lazy<double[]> FullImaginary = new(average.FlattenFullImaginaryRowMajor);
    }}
