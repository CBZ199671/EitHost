using System.Text.Json;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

public interface IImagingReplaySource
{
    ImagingRunDetail? GetImagingRunDetail(Guid imagingRunId);

    IReadOnlyList<ImagingFrameIndexEntry> ListFrameIndex(Guid imagingRunId);

    IReadOnlyList<ImagingReferenceEpochRecord> ListReferenceEpochs(Guid imagingRunId);

    IReadOnlyList<ImagingReferenceCandidateRecord> ListReferenceCandidates(Guid imagingRunId);

    ImagingFrameDetail? GetFrame(Guid imagingRunId, int blockNumber);
}

public sealed class LegacyEitFrameReplaySource(EitFrameStore store) : IImagingReplaySource
{
    private readonly EitFrameStore store = store ?? throw new ArgumentNullException(nameof(store));

    public ImagingRunDetail? GetImagingRunDetail(Guid imagingRunId) =>
        store.GetImagingRunDetail(imagingRunId);

    public IReadOnlyList<ImagingFrameIndexEntry> ListFrameIndex(Guid imagingRunId) =>
        store.ListFrameIndex(imagingRunId);

    public IReadOnlyList<ImagingReferenceEpochRecord> ListReferenceEpochs(Guid imagingRunId) =>
        store.ListReferenceEpochs(imagingRunId);

    public IReadOnlyList<ImagingReferenceCandidateRecord> ListReferenceCandidates(Guid imagingRunId) =>
        store.ListReferenceCandidates(imagingRunId);

    public ImagingFrameDetail? GetFrame(Guid imagingRunId, int blockNumber) =>
        store.GetFrame(imagingRunId, blockNumber);
}

public sealed class CanonicalExperimentReplaySource
    : IImagingReplaySource
{
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;

    public CanonicalExperimentReplaySource(DataRootLayout layout, ExperimentCatalog catalog)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public ReconstructionRevisionCatalogRecord? GetPublishedReconstructionRevision(
        Guid imagingRunId,
        string lane) => catalog.GetPublishedReconstructionRevision(imagingRunId, lane);

    public ReconstructionLaneReplaySource OpenPublishedReconstructionLane(
        Guid imagingRunId,
        string lane,
        string revisionId) => new(layout, catalog, this, imagingRunId, lane, revisionId);

    public ImagingRunDetail? GetImagingRunDetail(Guid imagingRunId)
    {
        var run = catalog.GetRun(imagingRunId);
        if (run is null)
        {
            return null;
        }

        var config = catalog.GetRunConfig(imagingRunId);
        var epochs = ListReferenceEpochs(imagingRunId);
        var latestEpoch = epochs.LastOrDefault();
        double[,]? nodes = null;
        int[,]? cells = null;
        if (ResolveArtifactPath(run, blockNumber: -1, "mesh", "mesh.h5") is { } meshPath)
        {
            using var mesh = Hdf5FileAccess.OpenReadWithRetry(meshPath);
            ValidateRunIdentity(mesh, imagingRunId);
            nodes = ReadOptional<double[,]>(mesh, "/mesh/node_coords");
            cells = ReadOptional<int[,]>(mesh, "/mesh/cell_connectivity");
        }

        return new ImagingRunDetail(
            imagingRunId,
            run.SessionId,
            run.SetLabel,
            run.StartedAt,
            run.EndedAt,
            config?.ReconstructionRoute ?? "legacy-transition-unrecorded",
            config?.DifferenceLambda ?? 0.01,
            config?.CustomLambdaEnabled ?? false,
            config?.MeshSize ?? 0.1,
            config?.FrequencyHz ?? 0.0,
            config?.ChannelCycles ?? 0.0,
            config?.SampleRateHz ?? 0.0,
            config?.DifferenceOrientation ?? "target_minus_reference",
            latestEpoch?.LockedBlockNumber,
            latestEpoch?.ReferenceAmplitude208,
            nodes,
            cells,
            run.StorageMode,
            config?.ReconstructionScaleStatus ?? ReconstructionScale.ModelRelative,
            config?.ReconstructionScaleProvenance ?? "legacy-transition-unrecorded",
            config?.ReferenceScalePolicy ?? "legacy_unspecified",
            config?.ContactOperatingFingerprintJson,
            config?.ContactThresholdProfileId,
            config?.ContactThresholdMode ?? "uncalibrated-legacy",
            config?.RequestedFrequencyHz,
            config?.ActualFrequencyHz,
            config?.DdsFrequencyTuningWord,
            config?.RequestedDwellUs,
            config?.EffectiveDwellUs,
            config?.AdRangeCode,
            config?.AdcFullSpanVolts,
            config?.AdcLsbVolts);
    }

    public IReadOnlyList<ImagingFrameIndexEntry> ListFrameIndex(Guid imagingRunId)
    {
        if (catalog.GetRun(imagingRunId) is null)
        {
            return [];
        }

        return catalog.ListProcessingBlocks(imagingRunId)
            .Where(block => string.Equals(block.DemodStatus, "ready", StringComparison.Ordinal))
            .Select(block => new ImagingFrameIndexEntry(
                block.BlockNumber,
                block.AcquiredAt,
                block.QualityWeight,
                block.AcceptedFrameCount,
                block.RejectedFrameCount,
                string.Equals(
                    block.ReconstructionStatus,
                    "ready",
                    StringComparison.Ordinal)))
            .ToArray();
    }

    public IReadOnlyList<ImagingReferenceEpochRecord> ListReferenceEpochs(Guid imagingRunId)
    {
        var epochs = new List<ImagingReferenceEpochRecord>();
        foreach (var index in catalog.ListReferenceEpochs(imagingRunId))
        {
            var path = layout.ResolveArtifactPath(index.ArtifactPath);
            if (!File.Exists(path))
            {
                continue;
            }

            using var file = Hdf5FileAccess.OpenReadWithRetry(path);
            ValidateRunIdentity(file, imagingRunId);
            var embeddedEpoch = file.Dataset("/metadata/run/reference_epoch").Read<int>();
            if (embeddedEpoch != index.ReferenceEpoch)
            {
                throw new InvalidDataException("Reference artifact epoch does not match catalog-v2.");
            }

            if (file.LinkExists("/metadata/run/locked_start_sample_index") &&
                file.Dataset("/metadata/run/locked_start_sample_index").Read<long>() !=
                index.LockedStartSampleIndex)
            {
                throw new InvalidDataException(
                    "Reference artifact sample anchor does not match catalog-v2.");
            }

            var metadata = JsonSerializer.Deserialize<DerivedReferenceEpochMetadata>(
                    file.Dataset("/metadata/reference_json").Read<string>())
                ?? throw new InvalidDataException("Reference artifact metadata is invalid.");
            epochs.Add(new ImagingReferenceEpochRecord(
                imagingRunId,
                index.ReferenceEpoch,
                index.LockedBlockNumber,
                index.LockedAt,
                metadata.RetainedFrameCount,
                metadata.RejectedFrameCount,
                file.Dataset("/reference/amplitude_208").Read<double[]>(),
                file.Dataset("/reference/full_real_256").Read<double[]>(),
                file.Dataset("/reference/full_imaginary_256").Read<double[]>(),
                metadata.NoiseGlobalThreshold,
                metadata.DemodEstimatedWindowSamples,
                metadata.DemodUniformOffsetSamples,
                metadata.DemodRotationStartChannel,
                metadata.DemodRotationDirection,
                metadata.FrequencyHz,
                metadata.DacGain,
                metadata.PgaGain,
                metadata.LockKind,
                metadata.CommonScaleNormalized,
                metadata.CommonScaleNormalizationPolicy,
                metadata.MedianInputCommonScale,
                metadata.ReferenceScalePolicy,
                metadata.SourceCandidateIds,
                metadata.SelectedWindowStartedAt,
                metadata.SelectedWindowEndedAt,
                metadata.EffectiveReferenceAt,
                metadata.SelectedWindowDriftPerMinute,
                metadata.SelectedWindowGapCount,
                metadata.SelectedWindowSaturationCount,
                metadata.SelectedWindowContactEvidence,
                metadata.NoiseEstimationPolicy,
                metadata.ActionGroupId,
                metadata.CommonActionAt,
                metadata.WindowSkewMilliseconds,
                metadata.SwitchSkewMilliseconds,
                metadata.SynchronizedSetCount,
                index.LockedStartSampleIndex,
                file.LinkExists("/reference/noise_precision_weight_208")
                    ? file.Dataset("/reference/noise_precision_weight_208").Read<double[]>()
                    : null));
        }

        return epochs;
    }

    public IReadOnlyList<ImagingReferenceCandidateRecord> ListReferenceCandidates(Guid imagingRunId)
    {
        var candidates = new List<ImagingReferenceCandidateRecord>();
        var artifacts = catalog.ListDerivedArtifacts(imagingRunId)
            .Where(artifact => string.Equals(
                artifact.Kind,
                "reference_candidates",
                StringComparison.Ordinal))
            .OrderBy(artifact => artifact.BlockNumber)
            .ToArray();
        foreach (var shard in artifacts.GroupBy(
                     artifact => artifact.ArtifactPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            var path = layout.ResolveArtifactPath(shard.Key);
            if (!File.Exists(path))
            {
                continue;
            }

            using var file = Hdf5FileAccess.OpenReadWithRetry(path);
            foreach (var artifact in shard.OrderBy(item => item.BlockNumber))
            {
                var blockRoot = ResolveBlockRoot(file, artifact.BlockNumber);
                ValidateBlockIdentity(file, imagingRunId, artifact.BlockNumber, blockRoot);
                var metadata = JsonSerializer.Deserialize<DerivedReferenceCandidateMetadata[]>(
                        file.Dataset(At(blockRoot, "/candidates/metadata_json")).Read<string>()) ?? [];
                var voltage = file.Dataset(At(blockRoot, "/candidates/voltage_208")).Read<double[,]>();
                var real = file.Dataset(At(blockRoot, "/candidates/full_real_256")).Read<double[,]>();
                var imaginary = file.Dataset(At(blockRoot, "/candidates/full_imaginary_256")).Read<double[,]>();
                if (metadata.Length != voltage.GetLength(0) ||
                    metadata.Length != real.GetLength(0) ||
                    metadata.Length != imaginary.GetLength(0))
                {
                    throw new InvalidDataException("Reference candidate metadata/vector row counts differ.");
                }

                for (var row = 0; row < metadata.Length; row++)
                {
                    var item = metadata[row];
                    candidates.Add(new ImagingReferenceCandidateRecord(
                        imagingRunId,
                        item.Sequence,
                        item.SourceId,
                        item.CapturedAt,
                        artifact.BlockNumber,
                        item.FrameNumber,
                        item.StartSampleIndex,
                        item.EndSampleIndex,
                        item.Fingerprint,
                        item.GapBeforeSamples,
                        item.SaturationCount,
                        item.ContactEvidence,
                        ReadRow(voltage, row),
                        ReadRow(real, row),
                        ReadRow(imaginary, row)));
                }
            }
        }

        return candidates.OrderBy(candidate => candidate.Sequence).ToArray();
    }

    public ImagingFrameDetail? GetFrame(Guid imagingRunId, int blockNumber)
    {
        var frameCatalog = catalog.GetReplayFrameCatalogData(imagingRunId, blockNumber);
        var run = frameCatalog.Run;
        var block = frameCatalog.Block;
        if (run is null || block is null)
        {
            return null;
        }

        var artifacts = frameCatalog.Artifacts;
        var demodPath = ResolveArtifactPath(
            run,
            blockNumber,
            "demod",
            $"demod_{blockNumber:D8}.h5",
            artifacts);
        if (demodPath is null)
        {
            return null;
        }

        var amplitude = Array.Empty<double>();
        var real = Array.Empty<double>();
        var imaginary = Array.Empty<double>();
        double[]? fullAmplitude = null;
        double[]? fullReal = null;
        double[]? fullImaginary = null;
        var qualityWeight = block.QualityWeight;
        var acceptedFrames = block.AcceptedFrameCount;
        var rejectedFrames = block.RejectedFrameCount;
        DerivedFrameDiagnosticsMetadata? diagnosticsMetadata = null;
        double[]? measurementWeights = null;
        double[]? electrodeScores = null;
        double[]? faultConfidence = null;
        double[]? conductivity = null;
        double[]? rawConductivity = null;
        double? imageQuality = null;
        double? conditionNumber = null;
        DerivedReconstructionMetadata? reconstructionMetadata = null;
        var diagnosticsPath = ResolveArtifactPath(
            run,
            blockNumber,
            "diagnostics",
            $"diagnostics_{blockNumber:D8}.h5",
            artifacts);
        var reconstructionPath = ResolveArtifactPath(
            run,
            blockNumber,
            "reconstruction",
            $"recon_{blockNumber:D8}.h5",
            artifacts);
        var sources = new Dictionary<string, BlockArtifactSections>(StringComparer.OrdinalIgnoreCase);
        AddArtifactSection(sources, demodPath, BlockArtifactSections.Demod);
        AddArtifactSection(sources, diagnosticsPath, BlockArtifactSections.Diagnostics);
        AddArtifactSection(sources, reconstructionPath, BlockArtifactSections.Reconstruction);
        foreach (var (path, sections) in sources)
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(path);
            var blockRoot = ResolveBlockRoot(file, blockNumber);
            ValidateBlockIdentity(file, imagingRunId, blockNumber, blockRoot);
            if ((sections & BlockArtifactSections.Demod) != 0)
            {
                amplitude = file.Dataset(At(blockRoot, "/demod/mean_amplitude_208")).Read<double[]>();
                real = file.Dataset(At(blockRoot, "/demod/mean_real_208")).Read<double[]>();
                imaginary = file.Dataset(At(blockRoot, "/demod/mean_imaginary_208")).Read<double[]>();
                fullAmplitude = ReadOptional<double[]>(file, At(blockRoot, "/demod/mean_full_amplitude_256"));
                fullReal = ReadOptional<double[]>(file, At(blockRoot, "/demod/mean_full_real_256"));
                fullImaginary = ReadOptional<double[]>(file, At(blockRoot, "/demod/mean_full_imaginary_256"));
                qualityWeight = ReadOptional(file, At(blockRoot, "/quality/weight"), qualityWeight);
                acceptedFrames = ReadOptional(file, At(blockRoot, "/quality/accepted_frames"), acceptedFrames);
                rejectedFrames = ReadOptional(file, At(blockRoot, "/quality/rejected_frames"), rejectedFrames);
            }

            if ((sections & BlockArtifactSections.Diagnostics) != 0)
            {
                diagnosticsMetadata = JsonSerializer.Deserialize<DerivedFrameDiagnosticsMetadata>(
                        file.Dataset(At(blockRoot, "/diagnostics/metadata_json")).Read<string>())
                    ?? throw new InvalidDataException("Frame diagnostics metadata is invalid.");
                measurementWeights = ReadOptional<double[]>(file, At(blockRoot, "/diagnostics/measurement_weight_208"));
                electrodeScores = ReadOptional<double[]>(file, At(blockRoot, "/diagnostics/electrode_scores"));
                faultConfidence = ReadOptional<double[]>(file, At(blockRoot, "/diagnostics/fault_confidence"));
                imageQuality = diagnosticsMetadata.ImageQualityScore;
                if (file.LinkExists(At(blockRoot, "/replay_demod_override/mean_amplitude_208")))
                {
                    amplitude = file.Dataset(At(blockRoot, "/replay_demod_override/mean_amplitude_208")).Read<double[]>();
                    real = file.Dataset(At(blockRoot, "/replay_demod_override/mean_real_208")).Read<double[]>();
                    imaginary = file.Dataset(At(blockRoot, "/replay_demod_override/mean_imaginary_208")).Read<double[]>();
                    fullAmplitude = EmptyToNull(ReadOptional<double[]>(
                        file,
                        At(blockRoot, "/replay_demod_override/mean_full_amplitude_256")));
                    fullReal = EmptyToNull(ReadOptional<double[]>(
                        file,
                        At(blockRoot, "/replay_demod_override/mean_full_real_256")));
                    fullImaginary = EmptyToNull(ReadOptional<double[]>(
                        file,
                        At(blockRoot, "/replay_demod_override/mean_full_imaginary_256")));
                }
            }

            if ((sections & BlockArtifactSections.Reconstruction) != 0)
            {
                conductivity = ReadOptional<double[]>(file, At(blockRoot, "/reconstruction/conductivity"));
                rawConductivity = ReadOptional<double[]>(file, At(blockRoot, "/reconstruction/raw_conductivity"));
                imageQuality = ReadFiniteOptional(file, At(blockRoot, "/reconstruction/image_quality_score")) ?? imageQuality;
                conditionNumber = ReadFiniteOptional(
                    file,
                    At(blockRoot, "/reconstruction/weighted_system_condition_number"));
                measurementWeights ??= ReadOptional<double[]>(file, At(blockRoot, "/input/measurement_weight_208"));
                if (!file.LinkExists(At(blockRoot, "/metadata/reconstruction_json")))
                {
                    continue;
                }

                reconstructionMetadata = JsonSerializer.Deserialize<DerivedReconstructionMetadata>(
                    file.Dataset(At(blockRoot, "/metadata/reconstruction_json")).Read<string>());
            }
        }

        var referenceEpoch = diagnosticsMetadata?.ReferenceEpoch ?? reconstructionMetadata?.ReferenceEpoch;
        return new ImagingFrameDetail(
            ImagingRunId: imagingRunId,
            BlockNumber: blockNumber,
            CapturedAt: block.AcquiredAt,
            QualityWeight: qualityWeight,
            AcceptedFrames: acceptedFrames,
            RejectedFrames: rejectedFrames,
            MeanAmplitude208: amplitude,
            MeanReal208: real,
            MeanImaginary208: imaginary,
            Conductivity: conductivity,
            MeanFullAmplitude256: fullAmplitude,
            MeanFullReal256: fullReal,
            MeanFullImaginary256: fullImaginary,
            MeasurementWeight208: measurementWeights,
            WeightPolicyVersion: reconstructionMetadata?.WeightPolicyVersion ??
                                 diagnosticsMetadata?.WeightPolicyVersion ??
                                 "all-one-v1",
            ImageQualityScore: imageQuality,
            ReconstructionConditionNumber: conditionNumber,
            ElectrodeScores: electrodeScores,
            FaultConfidence: faultConfidence,
            ElectrodeStates: diagnosticsMetadata?.ElectrodeStates,
            FaultTypes: diagnosticsMetadata?.FaultTypes,
            UpgradeGateReasons: diagnosticsMetadata?.UpgradeGateReasons,
            ContactSummary: diagnosticsMetadata?.ContactSummary,
            CandidateDiagnosticJson: diagnosticsMetadata?.CandidateDiagnosticJson,
            DisplayCompensationPolicy: diagnosticsMetadata?.DisplayCompensationPolicy,
            DisplayCompensationOnly: diagnosticsMetadata?.DisplayCompensationOnly ?? false,
            DisplayCompensationPayloadJson: diagnosticsMetadata?.DisplayCompensationPayloadJson,
            ReferenceInvalidated: diagnosticsMetadata?.ReferenceInvalidated ?? false,
            ReferenceStatus: diagnosticsMetadata?.ReferenceStatus,
            RawConductivity: rawConductivity,
            DynamicKalmanSessionId: reconstructionMetadata?.DynamicKalmanSessionId,
            DynamicKalmanAction: reconstructionMetadata?.DynamicKalmanAction,
            DynamicKalmanNisPerDof: reconstructionMetadata?.DynamicKalmanNisPerDof,
            DynamicKalmanGainMean: reconstructionMetadata?.DynamicKalmanGainMean,
            DynamicKalmanVarianceInflation: reconstructionMetadata?.DynamicKalmanVarianceInflation,
            DynamicKalmanUpdateCount: reconstructionMetadata?.DynamicKalmanUpdateCount,
            DynamicKalmanTotalLatencyFrames: reconstructionMetadata?.DynamicKalmanTotalLatencyFrames,
            DynamicKalmanMode: reconstructionMetadata?.DynamicKalmanMode,
            DynamicKalmanFallback: reconstructionMetadata?.DynamicKalmanFallback,
            DynamicKalmanSolveMilliseconds: reconstructionMetadata?.DynamicKalmanSolveMilliseconds,
            ReconstructionBackendElapsedMilliseconds: reconstructionMetadata?.ReconstructionBackendElapsedMilliseconds,
            ReferenceEpoch: referenceEpoch,
            BaselineCommonScale: diagnosticsMetadata?.BaselineCommonScale,
            BaselineShapeResidualRelative: diagnosticsMetadata?.BaselineShapeResidualRelative,
            BaselineComplexScaleMagnitude: diagnosticsMetadata?.BaselineComplexScaleMagnitude,
            BaselineComplexPhaseDegrees: diagnosticsMetadata?.BaselineComplexPhaseDegrees,
            BaselineComplexShapeResidualRelative: diagnosticsMetadata?.BaselineComplexShapeResidualRelative,
            BaselineCommonModeEnergyFraction: diagnosticsMetadata?.BaselineCommonModeEnergyFraction,
            BaselineNearDriveScale: diagnosticsMetadata?.BaselineNearDriveScale,
            BaselineRemoteScale: diagnosticsMetadata?.BaselineRemoteScale,
            BaselineClassification: diagnosticsMetadata?.BaselineClassification,
            BaselineGlobalNoiseScore: diagnosticsMetadata?.BaselineGlobalNoiseScore,
            BaselineGlobalNoiseThreshold: diagnosticsMetadata?.BaselineGlobalNoiseThreshold,
            BaselineDemodStateChanged: diagnosticsMetadata?.BaselineDemodStateChanged,
            DemodEstimatedWindowSamples: diagnosticsMetadata?.DemodEstimatedWindowSamples,
            DemodUniformOffsetSamples: diagnosticsMetadata?.DemodUniformOffsetSamples,
            DemodRotationStartChannel: diagnosticsMetadata?.DemodRotationStartChannel,
            DemodRotationDirection: diagnosticsMetadata?.DemodRotationDirection,
            CommonScaleNormalized: diagnosticsMetadata?.CommonScaleNormalized ?? false,
            CommonScaleNormalizationPolicy: diagnosticsMetadata?.CommonScaleNormalizationPolicy ?? "none",
            CommonScaleNormalizationFactor: diagnosticsMetadata?.CommonScaleNormalizationFactor);
    }

    private string? ResolveArtifactPath(
        ExperimentRunRecord run,
        int blockNumber,
        string kind,
        string deterministicFileName,
        IReadOnlyList<DerivedArtifactCatalogRecord>? knownArtifacts = null)
    {
        var catalogPath = (knownArtifacts ?? catalog.ListDerivedArtifacts(run.ExperimentRunId))
            .Where(artifact => artifact.BlockNumber == blockNumber &&
                               string.Equals(artifact.Kind, kind, StringComparison.Ordinal))
            .OrderByDescending(artifact => artifact.CreatedAt)
            .Select(artifact => layout.ResolveArtifactPath(artifact.ArtifactPath))
            .FirstOrDefault(File.Exists);
        if (catalogPath is not null)
        {
            return catalogPath;
        }

        var deterministicPath = Path.Combine(
            layout.ResolveArtifactPath(run.RunDirectory),
            "derived",
            deterministicFileName);
        return File.Exists(deterministicPath) ? deterministicPath : null;
    }

    private static void AddArtifactSection(
        IDictionary<string, BlockArtifactSections> sources,
        string? path,
        BlockArtifactSections section)
    {
        if (path is null)
        {
            return;
        }

        sources[path] = sources.TryGetValue(path, out var existing)
            ? existing | section
            : section;
    }

    private static void ValidateRunIdentity(IH5Group file, Guid experimentRunId)
    {
        var embedded = Guid.Parse(file.Dataset("/metadata/run/experiment_run_id").Read<string>());
        if (embedded != experimentRunId)
        {
            throw new InvalidDataException("Derived artifact run id does not match catalog-v2.");
        }
    }

    private static void ValidateBlockIdentity(
        IH5Group file,
        Guid experimentRunId,
        int blockNumber,
        string blockRoot = "")
    {
        var runRoot = At(blockRoot, "/metadata/run");
        var embedded = Guid.Parse(file.Dataset($"{runRoot}/experiment_run_id").Read<string>());
        if (embedded != experimentRunId)
        {
            throw new InvalidDataException("Derived artifact run id does not match catalog-v2.");
        }

        if (file.Dataset($"{runRoot}/block_number").Read<int>() != blockNumber)
        {
            throw new InvalidDataException("Derived artifact block number does not match catalog-v2.");
        }
    }

    private static string ResolveBlockRoot(IH5Group file, int blockNumber)
    {
        var blockRoot = DataRootLayout.GetDerivedBlockRoot(blockNumber);
        return file.LinkExists(blockRoot) ? blockRoot : string.Empty;
    }

    private static string At(string root, string path)
    {
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return string.IsNullOrEmpty(root) ? normalizedPath : $"{root}{normalizedPath}";
    }

    private static T? ReadOptional<T>(IH5Group file, string path)
        where T : class
    {
        return file.LinkExists(path) ? file.Dataset(path).Read<T>() : null;
    }

    private static T ReadOptional<T>(IH5Group file, string path, T fallback)
        where T : struct
    {
        return file.LinkExists(path) ? file.Dataset(path).Read<T>() : fallback;
    }

    private static double? ReadFiniteOptional(IH5Group file, string path)
    {
        if (!file.LinkExists(path))
        {
            return null;
        }

        var value = file.Dataset(path).Read<double>();
        return double.IsFinite(value) ? value : null;
    }

    private static double[]? EmptyToNull(double[]? values) =>
        values is { Length: > 0 } ? values : null;

    private static double[] ReadRow(double[,] matrix, int row)
    {
        var result = new double[matrix.GetLength(1)];
        for (var column = 0; column < result.Length; column++)
        {
            result[column] = matrix[row, column];
        }

        return result;
    }

    [Flags]
    private enum BlockArtifactSections
    {
        None = 0,
        Demod = 1,
        Diagnostics = 2,
        Reconstruction = 4
    }
}
