using System.Text.Json;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

public sealed record ReconstructionLaneRoiFrame(
    int BlockNumber,
    double[] Conductivity,
    int? ReferenceEpoch);

public sealed record ReconstructionLaneRoiReadBatch(
    IReadOnlyDictionary<int, ReconstructionLaneRoiFrame> FramesByBlock,
    int ArtifactOpenCount);

public sealed record ReconstructionLaneRoiReadProgress(
    int CompletedFrameCount,
    int TotalFrameCount);

/// <summary>Read-only replay projection for one immutable, published reconstruction revision.</summary>
public sealed class ReconstructionLaneReplaySource : IImagingReplaySource
{
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly CanonicalExperimentReplaySource canonical;
    private readonly Guid experimentRunId;
    private readonly ReconstructionRevisionCatalogRecord revision;
    private readonly IReadOnlyList<ReconstructionLaneFrameCatalogRecord> laneFrames;
    private readonly IReadOnlyDictionary<int, ReconstructionLaneFrameCatalogRecord> framesByBlock;
    private readonly IReadOnlyList<RealtimeRoiEvidenceCatalogRecord> trustedNeutralRoiEvidence;
    private readonly IReadOnlyDictionary<int, RealtimeRoiEvidenceCatalogRecord> trustedNeutralEvidenceByBlock;
    private readonly GlobalReconstructionMeshStore meshStore;
    private ImagingRunDetail? laneDetail;

    public ReconstructionLaneReplaySource(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        CanonicalExperimentReplaySource canonical,
        Guid experimentRunId,
        string lane,
        string revisionId)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.canonical = canonical ?? throw new ArgumentNullException(nameof(canonical));
        meshStore = new GlobalReconstructionMeshStore(this.layout, new DerivedArtifactHdf5Writer());
        this.experimentRunId = experimentRunId;
        revision = catalog.GetReconstructionRevision(experimentRunId, lane, revisionId)
            ?? throw new KeyNotFoundException($"Reconstruction revision {lane}/{revisionId} does not exist.");
        if (!revision.IsComplete)
        {
            throw new InvalidOperationException($"Reconstruction revision {lane}/{revisionId} is not published and complete.");
        }

        laneFrames = catalog.ListReconstructionLaneFrames(experimentRunId, lane, revisionId)
            .OrderBy(frame => frame.SequenceNumber)
            .ThenBy(frame => frame.SourceBlockNumber)
            .ToArray();
        framesByBlock = laneFrames.ToDictionary(frame => frame.SourceBlockNumber);
        trustedNeutralRoiEvidence = string.Equals(lane, ReconstructionLane.Live, StringComparison.Ordinal)
            ? catalog.ListRealtimeRoiEvidence(experimentRunId, revisionId)
                .OrderBy(evidence => evidence.AcquiredAt)
                .ThenBy(evidence => evidence.SourceBlockNumber)
                .ToArray()
            : [];
        trustedNeutralEvidenceByBlock = trustedNeutralRoiEvidence.ToDictionary(
            evidence => evidence.SourceBlockNumber);
        if (laneFrames.Count != revision.DemodDenominator)
        {
            throw new InvalidDataException(
                $"Published reconstruction revision coverage is invalid: {laneFrames.Count}/{revision.DemodDenominator}.");
        }
    }

    public string Lane => revision.Lane;

    public string RevisionId => revision.RevisionId;

    public ReconstructionRevisionCatalogRecord Revision => revision;

    public IReadOnlyList<RealtimeRoiEvidenceCatalogRecord> ListRealtimeRoiEvidence() =>
        trustedNeutralRoiEvidence;

    public bool HasPersistedLaneFrame(int blockNumber) => framesByBlock.ContainsKey(blockNumber);

    public ImagingRunDetail? GetImagingRunDetail(Guid imagingRunId)
    {
        if (imagingRunId != experimentRunId)
        {
            return null;
        }

        if (laneDetail is not null)
        {
            return laneDetail;
        }

        var detail = canonical.GetImagingRunDetail(imagingRunId);
        if (detail is null)
        {
            return null;
        }

        foreach (var laneFrame in laneFrames.Where(frame =>
                     frame.Outcome == ReconstructionFrameOutcome.Reconstructed &&
                     frame.ArtifactPath is not null))
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(
                layout.ResolveArtifactPath(laneFrame.ArtifactPath!));
            var metadataPath = DataRootLayout.GetDerivedDatasetPath(
                laneFrame.SourceBlockNumber,
                "/metadata/reconstruction_json");
            if (!file.LinkExists(metadataPath))
            {
                continue;
            }

            var metadata = JsonSerializer.Deserialize<DerivedReconstructionMetadata>(
                file.Dataset(metadataPath).Read<string>());
            if (string.IsNullOrWhiteSpace(metadata?.MeshFingerprint) ||
                string.IsNullOrWhiteSpace(metadata.MeshArtifactPath))
            {
                continue;
            }

            var mesh = meshStore.Load(metadata.MeshArtifactPath, metadata.MeshFingerprint);
            ValidateFrameMeshMetadata(metadata, mesh.MeshIndexMetadata);
            laneDetail = detail with
            {
                NodeCoords = mesh.NodeCoords,
                CellConnectivity = mesh.CellConnectivity,
                MeshFingerprint = mesh.Fingerprint,
                MeshIndexSchema = mesh.MeshIndexMetadata.MeshIndexSchema,
                ReconstructionParameterEntity = mesh.MeshIndexMetadata.ParameterEntity,
                LogicalMeshFingerprint = mesh.MeshIndexMetadata.LogicalMeshFingerprint,
                OrderedIndexFingerprint = mesh.MeshIndexMetadata.OrderedIndexFingerprint,
                MeshCoordinateDecimals = mesh.MeshIndexMetadata.CoordinateDecimals,
                MeshCoordinateQuantizationStep = mesh.MeshIndexMetadata.CoordinateQuantizationStep
            };
            return laneDetail;
        }

        laneDetail = detail;
        return laneDetail;
    }

    public IReadOnlyList<ImagingFrameIndexEntry> ListFrameIndex(Guid imagingRunId)
    {
        if (imagingRunId != experimentRunId)
        {
            return [];
        }

        var laneEntries = laneFrames.Select(frame =>
        {
            var block = catalog.GetProcessingBlock(imagingRunId, frame.SourceBlockNumber);
            return new ImagingFrameIndexEntry(
                frame.SourceBlockNumber,
                frame.AcquiredAt,
                block?.QualityWeight ?? 0.0,
                block?.AcceptedFrameCount ?? 0,
                block?.RejectedFrameCount ?? 0,
                frame.Outcome == ReconstructionFrameOutcome.Reconstructed);
        });
        var evidenceEntries = trustedNeutralRoiEvidence
            .Where(evidence => !framesByBlock.ContainsKey(evidence.SourceBlockNumber))
            .Select(evidence =>
            {
                var block = GetValidatedTrustedNeutralBlock(evidence);
                return new ImagingFrameIndexEntry(
                    evidence.SourceBlockNumber,
                    evidence.AcquiredAt,
                    evidence.QualityWeight,
                    block.AcceptedFrameCount,
                    block.RejectedFrameCount,
                    HasConductivity: false);
            });
        return laneEntries
            .Concat(evidenceEntries)
            .OrderBy(entry => entry.CapturedAt)
            .ThenBy(entry => entry.BlockNumber)
            .ToArray();
    }

    public IReadOnlyList<ImagingReferenceEpochRecord> ListReferenceEpochs(Guid imagingRunId) =>
        imagingRunId == experimentRunId ? canonical.ListReferenceEpochs(imagingRunId) : [];

    public IReadOnlyList<ImagingReferenceCandidateRecord> ListReferenceCandidates(Guid imagingRunId) =>
        imagingRunId == experimentRunId ? canonical.ListReferenceCandidates(imagingRunId) : [];

    public ReconstructionLaneRoiReadBatch ReadRoiFrames(
        Guid imagingRunId,
        ImagingRunDetail detail,
        IReadOnlyCollection<int> blockNumbers,
        IProgress<ReconstructionLaneRoiReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(blockNumbers);
        if (imagingRunId != experimentRunId || detail.ImagingRunId != experimentRunId)
        {
            return new ReconstructionLaneRoiReadBatch(
                new Dictionary<int, ReconstructionLaneRoiFrame>(),
                ArtifactOpenCount: 0);
        }

        var requestedBlocks = blockNumbers.Distinct().ToArray();
        var requestedFrames = requestedBlocks
            .Select(block => framesByBlock.TryGetValue(block, out var frame) ? frame : null)
            .Where(static frame => frame is not null)
            .Cast<ReconstructionLaneFrameCatalogRecord>()
            .ToArray();
        var total = requestedBlocks.Length;
        var completed = total - requestedFrames.Length;
        var frames = new Dictionary<int, ReconstructionLaneRoiFrame>();
        ReportRoiReadProgress(progress, completed, total, force: true);

        var reconstructed = requestedFrames
            .Where(frame => frame.Outcome == ReconstructionFrameOutcome.Reconstructed)
            .ToArray();
        foreach (var missing in reconstructed.Where(frame =>
                     string.IsNullOrWhiteSpace(frame.ArtifactPath) ||
                     string.IsNullOrWhiteSpace(frame.DatasetPath)))
        {
            throw new InvalidDataException(
                $"Published reconstruction frame {missing.SourceBlockNumber} has no lane artifact dataset.");
        }

        completed += requestedFrames.Length - reconstructed.Length;
        ReportRoiReadProgress(progress, completed, total, force: true);
        var laneMetadata = ReconstructionMeshIndexMetadata.FromPersisted(
            detail.MeshIndexSchema,
            detail.ReconstructionParameterEntity,
            detail.LogicalMeshFingerprint,
            detail.OrderedIndexFingerprint,
            detail.MeshCoordinateDecimals,
            detail.MeshCoordinateQuantizationStep);
        var artifactOpenCount = 0;
        foreach (var artifactGroup in reconstructed.GroupBy(
                     frame => frame.ArtifactPath!,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = Hdf5FileAccess.OpenReadWithRetry(
                layout.ResolveArtifactPath(artifactGroup.Key));
            artifactOpenCount++;
            foreach (var laneFrame in artifactGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conductivity = ReadRequiredConductivity(file, laneFrame);
                var metadata = ReadReconstructionMetadata(file, laneFrame.SourceBlockNumber);
                if (!string.IsNullOrWhiteSpace(metadata?.MeshFingerprint))
                {
                    if (string.IsNullOrWhiteSpace(detail.MeshFingerprint) ||
                        !string.Equals(detail.MeshFingerprint, metadata.MeshFingerprint, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Reconstruction frame mesh does not match lane mesh for block {laneFrame.SourceBlockNumber}.");
                    }

                    ValidateFrameMeshMetadata(metadata, laneMetadata);
                    laneMetadata.ValidateForResult(
                        detail.NodeCoords!,
                        detail.CellConnectivity!,
                        conductivity.Length,
                        requireCanonical: !laneMetadata.UsesLegacyContract);
                }

                frames.Add(
                    laneFrame.SourceBlockNumber,
                    new ReconstructionLaneRoiFrame(
                        laneFrame.SourceBlockNumber,
                        conductivity,
                        metadata?.ReferenceEpoch));
                completed++;
                ReportRoiReadProgress(progress, completed, total, force: completed == total);
            }
        }

        return new ReconstructionLaneRoiReadBatch(frames, artifactOpenCount);
    }

    public ImagingFrameDetail? GetFrame(Guid imagingRunId, int blockNumber)
    {
        if (imagingRunId != experimentRunId)
        {
            return null;
        }

        framesByBlock.TryGetValue(blockNumber, out var laneFrame);
        trustedNeutralEvidenceByBlock.TryGetValue(blockNumber, out var trustedNeutralEvidence);
        if (laneFrame is null && trustedNeutralEvidence is null)
        {
            return null;
        }

        var frame = canonical.GetFrame(imagingRunId, blockNumber)
            ?? throw new InvalidDataException($"Canonical demodulated frame {blockNumber} is unavailable.");
        if (laneFrame is null)
        {
            GetValidatedTrustedNeutralBlock(trustedNeutralEvidence!);
            return frame with
            {
                Conductivity = null,
                RawConductivity = null,
                MeasurementWeight208 = null,
                ReconstructionConditionNumber = null,
                ReferenceEpoch = trustedNeutralEvidence!.ReferenceEpoch,
                DynamicKalmanSessionId = null,
                DynamicKalmanAction = null,
                DynamicKalmanNisPerDof = null,
                DynamicKalmanGainMean = null,
                DynamicKalmanVarianceInflation = null,
                DynamicKalmanUpdateCount = null,
                DynamicKalmanTotalLatencyFrames = null,
                DynamicKalmanMode = null,
                DynamicKalmanFallback = null,
                DynamicKalmanSolveMilliseconds = null,
                ReconstructionBackendElapsedMilliseconds = null,
                ReconstructionLane = revision.Lane,
                ReconstructionRevisionId = revision.RevisionId,
                ReconstructionFrameOutcome = ReconstructionFrameOutcome.Neutral,
                ReconstructionPresentationJson = JsonSerializer.Serialize(new ReconstructionFramePresentation(
                    "realtime-raster-v2",
                    "blue-white-red-v1",
                    "normal",
                    1.0,
                    null,
                    null,
                    "neutral",
                    false,
                    "trusted-neutral replay evidence")),
                ReconstructionExclusionReason = null,
                ReconstructionAlgorithmFingerprint = revision.AlgorithmFingerprint,
                ReconstructionMeshFingerprint = null,
                ReconstructionMeshArtifactPath = null
            };
        }

        var isOfflineComplete = string.Equals(
            laneFrame.Lane,
            ReconstructionLane.OfflineComplete,
            StringComparison.Ordinal);
        var offlinePresentation = isOfflineComplete
            ? ReadPersistedPresentation(laneFrame.PresentationJson)
            : null;
        var conductivity = default(double[]);
        var rawConductivity = default(double[]);
        var weights = isOfflineComplete ? null : frame.MeasurementWeight208;
        var conditionNumber = isOfflineComplete ? null : frame.ReconstructionConditionNumber;
        var referenceEpoch = frame.ReferenceEpoch;
        DerivedReconstructionMetadata? metadata = null;
        if (laneFrame.ArtifactPath is { } artifactPath && laneFrame.DatasetPath is not null)
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(layout.ResolveArtifactPath(artifactPath));
            conductivity = ReadRequiredConductivity(file, laneFrame);

            var blockRoot = DataRootLayout.GetDerivedBlockRoot(blockNumber);
            rawConductivity = ReadOptional<double[]>(file, $"{blockRoot}/reconstruction/raw_conductivity");
            weights = ReadOptional<double[]>(file, $"{blockRoot}/input/measurement_weight_208") ?? weights;
            conditionNumber = ReadFiniteOptional(
                file,
                $"{blockRoot}/reconstruction/weighted_system_condition_number") ?? conditionNumber;
            metadata = ReadReconstructionMetadata(file, blockNumber);
            referenceEpoch = metadata?.ReferenceEpoch ?? referenceEpoch;
        }

        if (conductivity is { Length: > 0 } && !string.IsNullOrWhiteSpace(metadata?.MeshFingerprint))
        {
            var detail = GetImagingRunDetail(imagingRunId)
                ?? throw new InvalidDataException("Reconstruction lane mesh is unavailable.");
            if (string.IsNullOrWhiteSpace(detail.MeshFingerprint) ||
                !string.Equals(detail.MeshFingerprint, metadata.MeshFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Reconstruction frame mesh does not match lane mesh for block {blockNumber}.");
            }

            var laneMetadata = ReconstructionMeshIndexMetadata.FromPersisted(
                detail.MeshIndexSchema,
                detail.ReconstructionParameterEntity,
                detail.LogicalMeshFingerprint,
                detail.OrderedIndexFingerprint,
                detail.MeshCoordinateDecimals,
                detail.MeshCoordinateQuantizationStep);
            ValidateFrameMeshMetadata(metadata, laneMetadata);
            laneMetadata.ValidateForResult(
                detail.NodeCoords!,
                detail.CellConnectivity!,
                conductivity.Length,
                requireCanonical: !laneMetadata.UsesLegacyContract);
        }

        return frame with
        {
            Conductivity = conductivity,
            RawConductivity = rawConductivity,
            MeasurementWeight208 = weights,
            WeightPolicyVersion = isOfflineComplete
                ? offlinePresentation?.ContactEvidencePolicy ??
                  "offline-contact-unavailable:missing-or-invalid-presentation"
                : frame.WeightPolicyVersion,
            ImageQualityScore = isOfflineComplete ? null : frame.ImageQualityScore,
            ReconstructionConditionNumber = conditionNumber,
            ElectrodeScores = isOfflineComplete ? null : frame.ElectrodeScores,
            FaultConfidence = isOfflineComplete ? null : frame.FaultConfidence,
            ElectrodeStates = isOfflineComplete
                ? offlinePresentation?.ElectrodeStates ?? []
                : frame.ElectrodeStates,
            FaultTypes = isOfflineComplete ? null : frame.FaultTypes,
            UpgradeGateReasons = isOfflineComplete ? null : frame.UpgradeGateReasons,
            ContactSummary = isOfflineComplete
                ? CreateOfflineContactSummary(offlinePresentation)
                : frame.ContactSummary,
            CandidateDiagnosticJson = isOfflineComplete ? null : frame.CandidateDiagnosticJson,
            DisplayCompensationPolicy = isOfflineComplete ? null : frame.DisplayCompensationPolicy,
            DisplayCompensationOnly = isOfflineComplete ? false : frame.DisplayCompensationOnly,
            DisplayCompensationPayloadJson = isOfflineComplete
                ? null
                : frame.DisplayCompensationPayloadJson,
            ReferenceInvalidated = isOfflineComplete ? false : frame.ReferenceInvalidated,
            ReferenceStatus = isOfflineComplete
                ? $"offline-independent-boundary:{offlinePresentation?.BoundaryChangeAction ?? "unavailable"}"
                : frame.ReferenceStatus,
            ReferenceEpoch = referenceEpoch,
            BaselineCommonScale = isOfflineComplete ? null : frame.BaselineCommonScale,
            BaselineShapeResidualRelative = isOfflineComplete
                ? null
                : frame.BaselineShapeResidualRelative,
            BaselineComplexScaleMagnitude = isOfflineComplete
                ? null
                : frame.BaselineComplexScaleMagnitude,
            BaselineComplexPhaseDegrees = isOfflineComplete
                ? null
                : frame.BaselineComplexPhaseDegrees,
            BaselineComplexShapeResidualRelative = isOfflineComplete
                ? null
                : frame.BaselineComplexShapeResidualRelative,
            BaselineCommonModeEnergyFraction = isOfflineComplete
                ? null
                : frame.BaselineCommonModeEnergyFraction,
            BaselineNearDriveScale = isOfflineComplete ? null : frame.BaselineNearDriveScale,
            BaselineRemoteScale = isOfflineComplete ? null : frame.BaselineRemoteScale,
            BaselineClassification = isOfflineComplete ? null : frame.BaselineClassification,
            BaselineGlobalNoiseScore = isOfflineComplete ? null : frame.BaselineGlobalNoiseScore,
            BaselineGlobalNoiseThreshold = isOfflineComplete
                ? null
                : frame.BaselineGlobalNoiseThreshold,
            BaselineDemodStateChanged = isOfflineComplete ? null : frame.BaselineDemodStateChanged,
            DynamicKalmanSessionId = metadata?.DynamicKalmanSessionId,
            DynamicKalmanAction = metadata?.DynamicKalmanAction,
            DynamicKalmanNisPerDof = metadata?.DynamicKalmanNisPerDof,
            DynamicKalmanGainMean = metadata?.DynamicKalmanGainMean,
            DynamicKalmanVarianceInflation = metadata?.DynamicKalmanVarianceInflation,
            DynamicKalmanUpdateCount = metadata?.DynamicKalmanUpdateCount,
            DynamicKalmanTotalLatencyFrames = metadata?.DynamicKalmanTotalLatencyFrames,
            DynamicKalmanMode = metadata?.DynamicKalmanMode,
            DynamicKalmanFallback = metadata?.DynamicKalmanFallback,
            DynamicKalmanSolveMilliseconds = metadata?.DynamicKalmanSolveMilliseconds,
            ReconstructionBackendElapsedMilliseconds = metadata?.ReconstructionBackendElapsedMilliseconds,
            ReconstructionLane = laneFrame.Lane,
            ReconstructionRevisionId = laneFrame.RevisionId,
            ReconstructionFrameOutcome = laneFrame.Outcome,
            ReconstructionPresentationJson = laneFrame.PresentationJson,
            ReconstructionExclusionReason = laneFrame.ExclusionReason,
            ReconstructionAlgorithmFingerprint = laneFrame.AlgorithmFingerprint,
            ReconstructionMeshFingerprint = metadata?.MeshFingerprint,
            ReconstructionMeshArtifactPath = metadata?.MeshArtifactPath
        };
    }

    private static ReconstructionFramePresentation? ReadPersistedPresentation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReconstructionFramePresentation>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CreateOfflineContactSummary(ReconstructionFramePresentation? presentation)
    {
        if (presentation is null)
        {
            return "离线独立接触诊断证据不可用 · offline-contact-unavailable:missing-or-invalid-presentation";
        }

        var summary = string.IsNullOrWhiteSpace(presentation.ContactSummary)
            ? "离线独立接触诊断无摘要"
            : presentation.ContactSummary.Trim();
        return string.IsNullOrWhiteSpace(presentation.ContactEvidencePolicy)
            ? $"{summary} · offline-contact-unavailable:missing-policy"
            : $"{summary} · {presentation.ContactEvidencePolicy}";
    }

    private ProcessingBlockCatalogRecord GetValidatedTrustedNeutralBlock(
        RealtimeRoiEvidenceCatalogRecord evidence)
    {
        var block = catalog.GetProcessingBlock(experimentRunId, evidence.SourceBlockNumber)
            ?? throw new InvalidDataException(
                $"Trusted-neutral evidence block {evidence.SourceBlockNumber} has no canonical demodulated block.");
        if (block.SourceStartSampleIndex != evidence.SourceStartSampleIndex ||
            block.SourceEndSampleIndex != evidence.SourceEndSampleIndex)
        {
            throw new InvalidDataException(
                $"Trusted-neutral evidence block {evidence.SourceBlockNumber} sample range does not match the canonical block.");
        }

        return block;
    }

    private static string ResolveConductivityDatasetPath(string recordedPath, int blockNumber)
    {
        var normalized = recordedPath.TrimEnd('/');
        var historicalGroupPath = DataRootLayout.GetDerivedDatasetPath(blockNumber, "/reconstruction");
        return string.Equals(normalized, historicalGroupPath, StringComparison.Ordinal)
            ? DataRootLayout.GetDerivedDatasetPath(blockNumber, "/reconstruction/conductivity")
            : recordedPath;
    }

    private static double[] ReadRequiredConductivity(
        IH5Group file,
        ReconstructionLaneFrameCatalogRecord laneFrame)
    {
        var blockNumber = laneFrame.SourceBlockNumber;
        var conductivityPath = ResolveConductivityDatasetPath(laneFrame.DatasetPath!, blockNumber);
        if (!file.LinkExists(conductivityPath))
        {
            throw new InvalidDataException(
                $"Lane artifact conductivity dataset is missing for block {blockNumber}: {conductivityPath}.");
        }

        double[] conductivity;
        try
        {
            conductivity = file.Dataset(conductivityPath).Read<double[]>();
        }
        catch (Exception ex) when (
            ex is InvalidCastException ||
            ex.Message.Contains("cannot be casted to IH5Dataset", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Lane artifact conductivity path is not a dataset for block {blockNumber}: {conductivityPath}.",
                ex);
        }

        if (conductivity.Length == 0 || conductivity.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"Lane artifact conductivity dataset is empty or non-finite for block {blockNumber}: {conductivityPath}.");
        }

        return conductivity;
    }

    private static DerivedReconstructionMetadata? ReadReconstructionMetadata(
        IH5Group file,
        int blockNumber)
    {
        var path = DataRootLayout.GetDerivedDatasetPath(blockNumber, "/metadata/reconstruction_json");
        return file.LinkExists(path)
            ? JsonSerializer.Deserialize<DerivedReconstructionMetadata>(file.Dataset(path).Read<string>())
            : null;
    }

    private static void ReportRoiReadProgress(
        IProgress<ReconstructionLaneRoiReadProgress>? progress,
        int completed,
        int total,
        bool force)
    {
        if (progress is not null && (force || completed % 16 == 0))
        {
            progress.Report(new ReconstructionLaneRoiReadProgress(completed, total));
        }
    }

    private static void ValidateFrameMeshMetadata(
        DerivedReconstructionMetadata metadata,
        ReconstructionMeshIndexMetadata meshMetadata)
    {
        var hasFrameContract = !string.IsNullOrWhiteSpace(metadata.MeshIndexSchema) ||
            !string.IsNullOrWhiteSpace(metadata.ParameterEntity) ||
            !string.IsNullOrWhiteSpace(metadata.LogicalMeshFingerprint) ||
            !string.IsNullOrWhiteSpace(metadata.OrderedIndexFingerprint);
        if (!hasFrameContract)
        {
            return;
        }

        if (!string.Equals(metadata.MeshIndexSchema, meshMetadata.MeshIndexSchema, StringComparison.Ordinal) ||
            !string.Equals(metadata.ParameterEntity, meshMetadata.ParameterEntity, StringComparison.Ordinal) ||
            !string.Equals(metadata.LogicalMeshFingerprint, meshMetadata.LogicalMeshFingerprint, StringComparison.Ordinal) ||
            !string.Equals(metadata.OrderedIndexFingerprint, meshMetadata.OrderedIndexFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Reconstruction frame mesh-index contract does not match the fixed canonical inverse mesh.");
        }
    }

    private static T? ReadOptional<T>(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<T>() : default;

    private static double? ReadFiniteOptional(IH5Group file, string path)
    {
        if (!file.LinkExists(path))
        {
            return null;
        }

        var value = file.Dataset(path).Read<double>();
        return double.IsFinite(value) ? value : null;
    }
}
