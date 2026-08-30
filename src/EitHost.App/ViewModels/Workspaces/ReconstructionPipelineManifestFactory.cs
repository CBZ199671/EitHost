using System.IO;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal static class ReconstructionPipelineManifestFactory
{
    internal static ReconstructionPipelineManifestCatalogRecord CreateRecording(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        ExperimentRunConfigRecord runConfig,
        DateTimeOffset createdAt)
    {
        return ReconstructionPipelineManifestCodec.Create(
            CreatePayload(config, state, runConfig),
            ReconstructionPipelineManifestStatus.Recording,
            createdAt,
            createdAt);
    }

    internal static ReconstructionPipelineManifestCatalogRecord Finalize(
        ReconstructionPipelineManifestCatalogRecord recording,
        ExperimentCatalog catalog,
        DataRootLayout layout,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var readiness = ReconstructionPipelineManifestCodec.EvaluateForOffline(recording);
        var payload = readiness.Manifest ?? DeserializeRecording(recording);
        ReconstructionPipelineInputInventory inventory;
        try
        {
            inventory = CreateInputInventory(recording.ExperimentRunId, catalog, layout);
        }
        catch (Exception ex)
        {
            return ReconstructionPipelineManifestCodec.Create(
                payload,
                ReconstructionPipelineManifestStatus.Unavailable,
                recording.CreatedAt,
                updatedAt,
                $"构建终态输入清单失败：{ex.Message}");
        }

        payload = payload with { Inputs = inventory };
        var candidate = ReconstructionPipelineManifestCodec.Create(
            payload,
            ReconstructionPipelineManifestStatus.Ready,
            recording.CreatedAt,
            updatedAt);
        readiness = ReconstructionPipelineManifestCodec.EvaluateForOffline(candidate);
        return readiness.Available
            ? candidate
            : ReconstructionPipelineManifestCodec.Create(
                payload,
                ReconstructionPipelineManifestStatus.Unavailable,
                recording.CreatedAt,
                updatedAt,
                readiness.Reason);
    }

    private static ReconstructionPipelineManifestPayload CreatePayload(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        ExperimentRunConfigRecord runConfig)
    {
        var match = state.AdaptiveContactProfileMatch;
        var temporalOptions = new EcdCwrTemporalDespikingOptions();
        var dynamicMode = string.Equals(config.DynamicKalmanMode, "auto", StringComparison.Ordinal)
            ? "auto:fast_image-with-static-guard"
            : config.DynamicKalmanMode;
        var processNoise = config.DynamicKalmanMode is "auto" or "fast_image"
            ? RealtimeDynamicKalmanOptions.SafeImageProcessNoiseRelativeStd
            : RealtimeDynamicKalmanOptions.AdvancedMeasurementProcessNoiseRelativeStd;
        return new ReconstructionPipelineManifestPayload(
            config.ImagingRunId,
            new ReconstructionDemodulationPolicy(
                "realtime-lockin-demod-v2",
                config.AcquisitionSettings.SampleRateHz,
                config.DacSettings.ActualFrequencyHz,
                runConfig.ChannelCycles,
                config.DemodDiscardLeadingCycles,
                config.DemodDiscardTrailingCycles,
                config.FramesPerBlock,
                config.MinimumAcceptedFrames,
                config.ReadRows,
                (int)config.AcquisitionSettings.Range,
                runConfig.AdcFullSpanVolts ?? 0.0,
                runConfig.AdcLsbVolts ?? 0.0,
                config.UseFrequencyDivisionLockIn,
                config.InterferenceFrequencyHz.ToArray(),
                config.EnableOutlierDetection,
                config.PersistAllDemodulatedBlocks),
            new ReconstructionReferencePolicy(
                config.ReferenceScalePolicy,
                "stable-candidate-window-v2",
                EcdCwrDiagnosticPolicy.CurrentVersion,
                runConfig.ContactOperatingFingerprintJson ?? string.Empty,
                match?.Profile?.ProfileId,
                RealtimeContactDiagnosticController.CreateAdaptiveContactThresholdMode(match),
                "raw_reference_dispersion-v1",
                "boundary-noise-precision-v1",
                PersistNoisePrecisionWeights: true),
            new ReconstructionWeightingPolicy(
                config.EnableOutlierCompensation,
                config.EnableTemporalDespiking,
                EcdCwrCenteredTemporalDespiker.WindowSize,
                EcdCwrCenteredTemporalDespiker.CenterIndex,
                EcdCwrCenteredTemporalDespiker.CreatePolicyVersion(temporalOptions),
                "diagnostic+centered5+boundary-noise-precision-v1",
                PersistPreTemporalWeights: true,
                PersistFinalWeights: true,
                AllowAllOneFallback: false),
            new ReconstructionInversePolicy(
                config.ReconstructionRoute,
                config.BackendProfile,
                "pyeidors-worker-json-v2",
                config.MeshSize,
                config.DifferenceLambda,
                config.CustomLambdaEnabled,
                config.DifferenceOrientation,
                runConfig.ReconstructionScaleStatus,
                runConfig.ReconstructionScaleProvenance,
                "circular-16-electrode-2d",
                "pyeidors-model-mesh-v1"),
            new ReconstructionDynamicKalmanPolicy(
                config.EnableDynamicKalman,
                dynamicMode,
                UpstreamLatencyFrames: 2,
                ProcessNoiseRelativeStd: processNoise,
                MeasurementNoiseRelativeStd: RealtimeDynamicKalmanOptions.DefaultMeasurementNoiseRelativeStd,
                InitialRelativeStd: 0.50,
                TransitionDecayPerBlock: 1.0,
                InnovationGate: "inflate",
                NisThresholdPerDof: 9.0,
                MaximumVarianceInflation: 100.0,
                SessionPolicyVersion: "offline-own-session-per-reset-boundary-v1",
                ImportLiveState: false),
            new ReconstructionPresentationPolicy(
                "realtime-raster-v3-node-interpolated",
                "blue-white-red-v1",
                "robust-quantile-adaptive-v1",
                "per-frame-persisted",
                "boundary-change-neutral-overlay-v1",
                PersistPerFramePresentation: true),
            new ReconstructionResetPolicy(
                "reset-at-reference-epoch",
                "reset-and-exclude-gap-boundary",
                "centered5-edge-terminal-outcome",
                "new-offline-session-per-reset-boundary",
                "source-start-sample-index-ascending"));
    }

    private static ReconstructionPipelineInputInventory CreateInputInventory(
        Guid experimentRunId,
        ExperimentCatalog catalog,
        DataRootLayout layout)
    {
        var rawSegments = catalog.ListRawSegments(experimentRunId)
            .OrderBy(item => item.StartSampleIndex)
            .Select(item => new ReconstructionRawInputIdentity(
                item.SegmentSequence,
                item.ArtifactPath,
                item.DatasetPath,
                item.StartSampleIndex,
                item.EndSampleIndex,
                item.SampleRows,
                GetArtifactBytes(layout, item.ArtifactPath),
                item.HasDiscontinuity))
            .ToArray();
        var artifacts = catalog.ListDerivedArtifacts(experimentRunId);
        var artifactLookup = artifacts
            .GroupBy(item => (item.BlockNumber, item.Kind))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First());
        var references = catalog.ListReferenceEpochs(experimentRunId)
            .OrderBy(item => item.LockedStartSampleIndex)
            .Select(item => new ReconstructionReferenceInputIdentity(
                item.ReferenceEpoch,
                item.LockedBlockNumber,
                item.LockedStartSampleIndex,
                item.ArtifactPath,
                item.DatasetPath,
                HasDataset(layout, item.ArtifactPath, "/reference/noise_precision_weight_208")))
            .ToArray();
        var firstReferenceBlock = references.Length == 0
            ? int.MaxValue
            : references.Min(item => item.LockedBlockNumber);
        var demodBlocks = catalog.ListProcessingBlocks(experimentRunId)
            .Where(item => string.Equals(item.DemodStatus, "ready", StringComparison.Ordinal))
            .OrderBy(item => item.SourceStartSampleIndex)
            .Select(item =>
            {
                artifactLookup.TryGetValue((item.BlockNumber, "demod"), out var demod);
                artifactLookup.TryGetValue((item.BlockNumber, "diagnostics"), out var diagnostics);
                var requiresWeights = item.BlockNumber > firstReferenceBlock;
                var hasWeights = !requiresWeights ||
                                 (diagnostics is not null && HasDataset(
                                     layout,
                                     diagnostics.ArtifactPath,
                                     DataRootLayout.GetDerivedDatasetPath(
                                         item.BlockNumber,
                                         "/diagnostics/measurement_weight_208")));
                return new ReconstructionDemodInputIdentity(
                    item.BlockNumber,
                    item.SourceStartSampleIndex,
                    item.SourceEndSampleIndex,
                    item.DemodStatus,
                    demod?.ArtifactPath,
                    diagnostics?.ArtifactPath,
                    hasWeights);
            })
            .ToArray();
        var mesh = artifacts
            .Where(item => string.Equals(item.Kind, "mesh", StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        return new ReconstructionPipelineInputInventory(
            rawSegments.Sum(item => item.SampleRows),
            rawSegments.Length,
            demodBlocks.Length,
            references.Length,
            demodBlocks.All(item => item.HasPersistedPreTemporalWeights),
            references.All(item => item.HasNoisePrecisionWeights),
            mesh is not null && File.Exists(layout.ResolveArtifactPath(mesh.ArtifactPath)),
            rawSegments,
            demodBlocks,
            references,
            mesh?.ArtifactPath);
    }

    private static ReconstructionPipelineManifestPayload DeserializeRecording(
        ReconstructionPipelineManifestCatalogRecord recording)
    {
        return ReconstructionPipelineManifestCodec.ReadPayload(recording);
    }

    private static bool HasDataset(DataRootLayout layout, string artifactPath, string datasetPath)
    {
        try
        {
            var path = layout.ResolveArtifactPath(artifactPath);
            using var file = Hdf5FileAccess.OpenReadWithRetry(path);
            return file.LinkExists(datasetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static long GetArtifactBytes(DataRootLayout layout, string artifactPath)
    {
        try
        {
            return new FileInfo(layout.ResolveArtifactPath(artifactPath)).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
