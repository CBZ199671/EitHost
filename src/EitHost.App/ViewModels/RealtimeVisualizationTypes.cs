using System.Windows.Media;
using EitHost.Core.Analysis;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels;

internal sealed record RealtimeRawPreviewSnapshot(
    Geometry? Channel1Geometry,
    Geometry? Channel2Geometry,
    string Stats);

internal sealed record RealtimeSignalPreviewSource(
    int BlockNumber,
    int AcceptedFrameCount,
    int FramesPerBlock,
    double QualityWeight,
    double[] AmplitudeVoltage208,
    double[] RealVoltage208,
    double[] ImaginaryVoltage208,
    double[]? ReferenceVoltage208,
    string DifferenceOrientation,
    bool DiagnosticMode = false,
    int TrustedMeasurementCount = 0,
    int DiagnosticMeasurementCount = 0,
    string? RejectSummary = null,
    bool ReferenceIsProvisional = false,
    RealtimeDemodulationStepStability? StepStability = null);

internal sealed record RealtimeDemodPreviewSnapshot(
    Geometry? PrimaryGeometry,
    Geometry? SecondaryGeometry,
    Geometry? GridGeometry,
    Geometry? ZeroLineGeometry,
    string Stats,
    IReadOnlyList<RealtimeDemodulationAxisTick> AxisTicks);

internal sealed record RealtimeBoundaryFitPreviewSnapshot(
    Geometry? MeasuredDeltaGeometry,
    Geometry? SimulatedDeltaGeometry,
    Geometry? TemplateExpectedGeometry,
    string Stats,
    string YAxisTop,
    string YAxisMiddle,
    string YAxisBottom);

internal sealed record RealtimeImagePreviewSnapshot(
    ImageSource? Image,
    string Stats,
    bool LowConfidence = false,
    RealtimeLiveFrameCommit? LiveFrameCommit = null);

internal sealed record RealtimePersistedLiveFrameEvidence(
    string SetLabel,
    Guid ExperimentRunId,
    string RevisionId,
    int SourceBlockNumber,
    long SourceStartSampleIndex,
    long SourceEndSampleIndex,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ProcessedAt,
    string AlgorithmFingerprint,
    string ArtifactPath,
    string DatasetPath,
    string FinalWeightHash,
    string ResultHash,
    string KalmanSessionId,
    string KalmanDisposition,
    int ReferenceEpoch,
    Task<bool> PersistenceReady);

internal sealed record RealtimeImagePresentationEvidence(
    string RendererVersion,
    string Colormap,
    string Polarity,
    double Gain,
    double? ScaleCenter,
    double? ScaleRange,
    string OverlayDisposition,
    bool LowConfidence,
    string Stats);

internal sealed record RealtimeLiveFrameCommit(
    RealtimePersistedLiveFrameEvidence Frame,
    RealtimeImagePresentationEvidence Presentation);

internal sealed record RealtimeVisualizationWorkItem(
    RealtimeReconstructionResult? Result,
    double[] Reference,
    double[] Target,
    ElectrodeContactDiagnosticResult? ContactResult,
    EcdCwrWaveformTemplateDisplayPackage? TemplateDisplayPackage,
    double? ImageQualityScore,
    int CompletedFrames,
    bool RenderBoundaryFit,
    bool RenderImage,
    int ReferenceEpoch,
    string? DegradedStatus = null,
    EcdCwrBoundaryChangeDecision? BoundaryChangeDecision = null,
    RealtimeNeutralImagePresentation? NeutralPresentation = null,
    bool NonReplaceable = false,
    RealtimePersistedLiveFrameEvidence? PersistedLiveEvidence = null);

internal sealed record RealtimeNeutralImagePresentation(string Stats, string Activity);

internal sealed class RealtimeDevicePreviewCache
{
    internal RealtimeRawPreviewSnapshot? Raw { get; set; }

    internal RealtimeSignalPreviewSource? SignalSource { get; set; }

    internal RealtimeBoundaryFitPreviewSnapshot? BoundaryFit { get; set; }

    internal RealtimeImagePreviewSnapshot? Image { get; set; }

    internal RealtimeRoiPreviewSnapshot? Roi { get; set; }

    internal string? Summary { get; set; }

    internal string? ImageStats { get; set; }

    internal string? ReconstructionActivity { get; set; }

    internal string? ReferenceSummary { get; set; }

    internal string? BaselineIntegritySummary { get; set; }

    internal string? ContactSummary { get; set; }

    internal string? MultiFrequencySummary { get; set; }

    internal string? DataQualityStatus { get; set; }

    internal string? ReferenceModeStatus { get; set; }

    internal string? ReconstructionQualityStatus { get; set; }

    internal string? RoiReadinessStatus { get; set; }

    internal bool ReferenceInvalidated { get; set; }

    internal RealtimeDevicePreviewCache Clone()
    {
        return new RealtimeDevicePreviewCache
        {
            Raw = Raw,
            SignalSource = SignalSource,
            BoundaryFit = BoundaryFit,
            Image = Image,
            Roi = Roi,
            Summary = Summary,
            ImageStats = ImageStats,
            ReconstructionActivity = ReconstructionActivity,
            ReferenceSummary = ReferenceSummary,
            BaselineIntegritySummary = BaselineIntegritySummary,
            ContactSummary = ContactSummary,
            MultiFrequencySummary = MultiFrequencySummary,
            DataQualityStatus = DataQualityStatus,
            ReferenceModeStatus = ReferenceModeStatus,
            ReconstructionQualityStatus = ReconstructionQualityStatus,
            RoiReadinessStatus = RoiReadinessStatus,
            ReferenceInvalidated = ReferenceInvalidated
        };
    }
}
