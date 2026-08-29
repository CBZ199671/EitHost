using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.Baseline;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Storage.Frames;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeFrameDiagnosticPackage(
    ImagingFrameRecord Record,
    bool PersistReplayDemodOverride);

internal static class RealtimeFrameDiagnosticFactory
{
    internal static RealtimeFrameDiagnosticPackage Create(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        EcdCwrDegradedDemodulationSelection? degradedSelection,
        ElectrodeContactDiagnosticResult? contactResult,
        double[]? activeContactWeights,
        string? activeWeightPolicy,
        EcdCwrWaveformTemplateDisplayPackage? templateDisplayPackage,
        string? templateDisplayPayloadJson,
        EitBaselineIntegrityResult? baselineIntegrity,
        string? candidateDiagnosticJson,
        string referenceStatus,
        Func<IReadOnlyList<double>, double?> commonScaleEstimator)
    {
        ArgumentNullException.ThrowIfNull(commonScaleEstimator);
        var diagnosticAverage = !block.IsHighQuality ? block.DiagnosticAverage : null;
        var persistedMeanAmplitude208 =
            diagnosticAverage?.FlattenAmplitudesRowMajor() ?? block.MeanAmplitude208;
        var persistedImageQuality = degradedSelection is null
            ? contactResult?.ImageQualityScore
            : Math.Min(
                contactResult?.ImageQualityScore ?? degradedSelection.ImageQualityCap,
                degradedSelection.ImageQualityCap);
        var persistFullComplex = config.StoragePolicy.PersistFullComplex256;
        var record = new ImagingFrameRecord(
            ImagingRunId: config.ImagingRunId,
            BlockNumber: block.BlockNumber,
            CapturedAt: RealtimeDerivedPersistenceController.CalculateBlockAcquiredAt(config, state, block),
            QualityWeight: block.QualityWeight,
            AcceptedFrames: block.AcceptedFrameCount,
            RejectedFrames: block.RejectedFrameCount,
            MeanAmplitude208: persistedMeanAmplitude208,
            MeanReal208: diagnosticAverage?.FlattenRealRowMajor() ?? block.MeanReal208,
            MeanImaginary208: diagnosticAverage?.FlattenImaginaryRowMajor() ?? block.MeanImaginary208,
            MeanFullAmplitude256: persistFullComplex
                ? diagnosticAverage?.FlattenFullAmplitudesRowMajor() ?? block.MeanFullAmplitude256
                : null,
            MeanFullReal256: persistFullComplex
                ? diagnosticAverage?.FlattenFullRealRowMajor() ?? block.MeanFullReal256
                : null,
            MeanFullImaginary256: persistFullComplex
                ? diagnosticAverage?.FlattenFullImaginaryRowMajor() ?? block.MeanFullImaginary256
                : null,
            MeasurementWeight208: degradedSelection?.MeasurementWeight208 ?? activeContactWeights,
            WeightPolicyVersion: degradedSelection?.WeightPolicyVersion ?? activeWeightPolicy ?? "all-one-v1",
            ImageQualityScore: persistedImageQuality,
            ElectrodeScores: contactResult?.Scores,
            FaultConfidence: contactResult?.FaultConfidence,
            ElectrodeStates: contactResult?.States.Select(state => state.ToString()).ToArray(),
            FaultTypes: contactResult?.FaultTypes.Select(type => type.ToString()).ToArray(),
            UpgradeGateReasons: contactResult?.UpgradeGateReasons,
            ContactSummary: contactResult?.Summary,
            CandidateDiagnosticJson: candidateDiagnosticJson,
            DisplayCompensationPolicy: templateDisplayPackage?.PolicyVersion,
            DisplayCompensationOnly: templateDisplayPackage?.DisplayOnly ?? false,
            DisplayCompensationPayloadJson: templateDisplayPayloadJson,
            ReferenceInvalidated: state.ReferenceInvalidated,
            ReferenceStatus: referenceStatus,
            CommonScaleNormalized: state.ReferenceUsesCommonScaleNormalization,
            CommonScaleNormalizationPolicy: state.ReferenceUsesCommonScaleNormalization
                ? EcdCwrCommonScaleNormalizer.PolicyVersion
                : EcdCwrReferenceScalePolicy.PreservePhysicalScale,
            CommonScaleNormalizationFactor: commonScaleEstimator(persistedMeanAmplitude208),
            ReferenceEpoch: state.ReferenceEpoch > 0 ? state.ReferenceEpoch : null,
            BaselineCommonScale: baselineIntegrity?.CommonScale,
            BaselineShapeResidualRelative: baselineIntegrity?.ShapeResidualRelative,
            BaselineComplexScaleMagnitude: baselineIntegrity?.ComplexScaleMagnitude,
            BaselineComplexPhaseDegrees: baselineIntegrity?.ComplexPhaseDegrees,
            BaselineComplexShapeResidualRelative: baselineIntegrity?.ComplexShapeResidualRelative,
            BaselineCommonModeEnergyFraction: baselineIntegrity?.CommonModeEnergyFraction,
            BaselineNearDriveScale: baselineIntegrity?.NearDriveScale,
            BaselineRemoteScale: baselineIntegrity?.RemoteScale,
            BaselineClassification: baselineIntegrity?.StorageClassification,
            BaselineGlobalNoiseScore: baselineIntegrity?.GlobalNoiseScore,
            BaselineGlobalNoiseThreshold: baselineIntegrity?.GlobalNoiseThreshold,
            BaselineDemodStateChanged: baselineIntegrity?.DemodStateChanged,
            DemodEstimatedWindowSamples: block.EstimatedWindowSamples,
            DemodUniformOffsetSamples: block.UniformOffsetSamples,
            DemodRotationStartChannel: block.RotationStartChannelOneBased,
            DemodRotationDirection: block.RotationDirection);
        return new RealtimeFrameDiagnosticPackage(record, diagnosticAverage is not null);
    }
}
