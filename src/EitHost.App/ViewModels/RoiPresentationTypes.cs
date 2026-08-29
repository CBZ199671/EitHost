using System.Windows.Media;
using EitHost.Core.Analysis;
using EitHost.Core.Diagnostics;

namespace EitHost.App.ViewModels;

internal sealed record RealtimeRoiPreviewSnapshot(
    Geometry? CurveGeometry,
    Geometry? RawCurveGeometry,
    Geometry? NoiseBandGeometry,
    IReadOnlyList<RoiCurveMarker> Markers,
    string AxisStart,
    string AxisMiddle,
    string AxisEnd,
    string Summary,
    FixedRoiTemporalVisualSnapshot FixedTemporal);

internal sealed record RealtimeRoiGeometry(
    double[,] NodeCoords,
    int[,] CellConnectivity);

internal static class RoiValueSource
{
    public const string InverseReconstruction = "inverse_reconstruction";
    public const string TrustedNeutral = "trusted_neutral";
}

internal sealed record RoiCurveChart(
    Geometry? Geometry,
    Geometry? RawGeometry,
    Geometry? NoiseBandGeometry,
    IReadOnlyList<RoiCurveMarker> Markers,
    string AxisStart,
    string AxisMiddle,
    string AxisEnd,
    int EpochSegmentCount);

internal sealed record ReplayRoiCalculationResult(
    List<RoiCurvePoint> Series,
    IReadOnlyList<FixedRoiTemporalSample> FixedSamples);

internal sealed record FixedRoiTemporalExportSource(
    string SetLabel,
    string Source,
    IReadOnlyList<FixedRoiTemporalAnalysis> Analyses);

internal sealed record RoiSelectionSnapshot(
    int Revision,
    string Mode,
    RoiDefinition? CustomDefinition,
    FixedRoiCell? FixedCell,
    double? NominalResolutionDiameterFraction,
    double CanvasSize);

internal sealed record RoiCurvePoint(
    string SetLabel,
    int FrameIndex,
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double QualityWeight,
    int? ReferenceEpoch,
    string ReferenceLockKind,
    string ValueSource,
    double MeanConductivity,
    double DespikedMeanConductivity,
    double RawMeanConductivity,
    EcdCwrRoiFilterState FilterState,
    double FilterScore,
    double FilterReturnScore,
    double NoiseCenter,
    double NoiseSigma,
    double NoiseSigmaMultiplier,
    int NoiseSampleCount,
    bool NoiseBandReady,
    bool IsOutsideNoiseBand,
    bool IsSustainedEvent,
    int SelectedCellCount,
    double AreaWeight,
    double MinConductivity,
    double MaxConductivity,
    RoiSelectionSnapshot RoiSelection);
