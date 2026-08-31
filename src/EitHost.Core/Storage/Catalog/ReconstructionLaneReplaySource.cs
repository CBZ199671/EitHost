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
        if (laneFrames.Count != revision.DemodDenominator)
        {
            throw new InvalidDataException(
                $"Published reconstruction revision coverage is invalid: {laneFrames.Count}/{revision.DemodDenominator}.");
        }
    }

    public string Lane => revision.Lane;

    public string RevisionId => revision.RevisionId;

    public ReconstructionRevisionCatalogRecord Revision => revision;

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

        return laneFrames.Select(frame =>
        {
            var block = catalog.GetProcessingBlock(imagingRunId, frame.SourceBlockNumber);
            return new ImagingFrameIndexEntry(
                frame.SourceBlockNumber,
                frame.AcquiredAt,
                block?.QualityWeight ?? 0.0,
                block?.AcceptedFrameCount ?? 0,
                block?.RejectedFrameCount ?? 0,
                frame.Outcome == ReconstructionFrameOutcome.Reconstructed);
        }).ToArray();
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
        if (imagingRunId != experimentRunId || !framesByBlock.TryGetValue(blockNumber, out var laneFrame))
        {
            return null;
        }

        var frame = canonical.GetFrame(imagingRunId, blockNumber)
            ?? throw new InvalidDataException($"Canonical demodulated frame {blockNumber} is unavailable.");
        var conductivity = default(double[]);
        var rawConductivity = default(double[]);
        var weights = frame.MeasurementWeight208;
        var conditionNumber = frame.ReconstructionConditionNumber;
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
            ReconstructionConditionNumber = conditionNumber,
            ReferenceEpoch = referenceEpoch,
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
