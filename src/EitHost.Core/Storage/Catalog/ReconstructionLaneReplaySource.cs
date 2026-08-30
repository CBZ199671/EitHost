using System.Text.Json;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

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

    public ImagingRunDetail? GetImagingRunDetail(Guid imagingRunId) =>
        imagingRunId == experimentRunId ? canonical.GetImagingRunDetail(imagingRunId) : null;

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
        if (laneFrame.ArtifactPath is { } artifactPath && laneFrame.DatasetPath is { } datasetPath)
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(layout.ResolveArtifactPath(artifactPath));
            if (!file.LinkExists(datasetPath))
            {
                throw new InvalidDataException($"Lane artifact dataset is missing for block {blockNumber}.");
            }

            conductivity = file.Dataset(datasetPath).Read<double[]>();
            var blockRoot = DataRootLayout.GetDerivedBlockRoot(blockNumber);
            rawConductivity = ReadOptional<double[]>(file, $"{blockRoot}/reconstruction/raw_conductivity");
            weights = ReadOptional<double[]>(file, $"{blockRoot}/input/measurement_weight_208") ?? weights;
            conditionNumber = ReadFiniteOptional(
                file,
                $"{blockRoot}/reconstruction/weighted_system_condition_number") ?? conditionNumber;
            if (file.LinkExists($"{blockRoot}/metadata/reconstruction_json"))
            {
                metadata = JsonSerializer.Deserialize<DerivedReconstructionMetadata>(
                    file.Dataset($"{blockRoot}/metadata/reconstruction_json").Read<string>());
                referenceEpoch = metadata?.ReferenceEpoch ?? referenceEpoch;
            }
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
            ReconstructionAlgorithmFingerprint = laneFrame.AlgorithmFingerprint
        };
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
