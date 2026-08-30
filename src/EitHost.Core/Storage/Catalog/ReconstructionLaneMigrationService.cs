using System.Text.Json;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

public sealed class ReconstructionLaneMigrationService
{
    private const string ClassificationVersion = "reconstruction-lane-legacy-classification-v1";
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;

    public ReconstructionLaneMigrationService(DataRootLayout layout, ExperimentCatalog catalog)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public ReconstructionLaneMigrationReport Classify(Guid experimentRunId)
    {
        var run = catalog.GetRun(experimentRunId) ?? throw new KeyNotFoundException(
            $"Experiment run {experimentRunId:D} does not exist.");
        if (run.Status is not (
                ExperimentCatalog.CompletedStatus or
                ExperimentCatalog.InterruptedStatus or
                ExperimentCatalog.FailedStatus))
        {
            throw new InvalidOperationException("Legacy reconstruction classification requires a terminal run.");
        }

        var blocks = catalog.ListProcessingBlocks(experimentRunId)
            .ToDictionary(block => block.BlockNumber);
        var classified = new List<LegacyFrameClassification>();
        var failures = new List<string>();
        foreach (var artifact in catalog.ListDerivedArtifacts(experimentRunId)
                     .Where(item => string.Equals(item.Kind, "reconstruction", StringComparison.Ordinal))
                     .OrderBy(item => blocks.TryGetValue(item.BlockNumber, out var block)
                         ? block.SourceStartSampleIndex
                         : long.MaxValue)
                     .ThenBy(item => item.BlockNumber))
        {
            if (!blocks.TryGetValue(artifact.BlockNumber, out var block))
            {
                failures.Add($"block {artifact.BlockNumber}: processing ledger missing");
                continue;
            }

            try
            {
                classified.Add(ReadClassification(artifact, block));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"block {artifact.BlockNumber}: {ex.Message}");
            }
        }

        var migrated = 0;
        foreach (var group in classified.GroupBy(item => item.Lane, StringComparer.Ordinal))
        {
            var revisionId = group.Key;
            if (catalog.GetPublishedReconstructionRevision(experimentRunId, group.Key) is not null)
            {
                continue;
            }

            var ordered = group
                .OrderBy(item => item.Block.SourceStartSampleIndex)
                .ThenBy(item => item.Block.BlockNumber)
                .ToArray();
            var fingerprint = $"{ClassificationVersion}:{group.Key}";
            var createdAt = ordered.Min(item => item.Artifact.CreatedAt);
            catalog.UpsertReconstructionRevision(new ReconstructionRevisionCatalogRecord(
                experimentRunId,
                group.Key,
                revisionId,
                ReconstructionRevisionStatus.Staged,
                fingerprint,
                RawDenominator: 0,
                DemodDenominator: ordered.Length,
                TerminalOutcomeCount: 0,
                ReconstructedCount: 0,
                NeutralCount: 0,
                ExcludedCount: 0,
                EstimatedIncrementalBytes: 0,
                CreatedAt: createdAt,
                UpdatedAt: DateTimeOffset.UtcNow));
            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                catalog.RecordReconstructionLaneFrame(new ReconstructionLaneFrameCatalogRecord(
                    experimentRunId,
                    group.Key,
                    revisionId,
                    item.Block.BlockNumber,
                    index + 1,
                    ReconstructionFrameOutcome.Reconstructed,
                    item.Block.AcquiredAt,
                    item.Artifact.CreatedAt,
                    fingerprint,
                    item.Artifact.ArtifactPath,
                    item.Artifact.DatasetPath,
                    KalmanDisposition: item.DynamicKalmanAction,
                    PresentationJson: JsonSerializer.Serialize(new
                    {
                        provenance = "legacy-not-display-audited",
                        processingMode = item.ProcessingMode
                    })));
                migrated++;
            }

            catalog.PublishReconstructionRevision(
                experimentRunId,
                group.Key,
                revisionId,
                rawDenominator: 0,
                demodDenominator: ordered.Length,
                publishedAt: DateTimeOffset.UtcNow);
        }

        return new ReconstructionLaneMigrationReport(
            experimentRunId,
            classified.Count,
            migrated,
            classified.Count(item => item.Lane == ReconstructionLane.LegacyLiveUnverified),
            classified.Count(item => item.Lane == ReconstructionLane.LegacyOfflineIncomplete),
            failures);
    }

    private LegacyFrameClassification ReadClassification(
        DerivedArtifactCatalogRecord artifact,
        ProcessingBlockCatalogRecord block)
    {
        var path = layout.ResolveArtifactPath(artifact.ArtifactPath);
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var candidateRoot = DataRootLayout.GetDerivedBlockRoot(block.BlockNumber);
        var root = file.LinkExists(candidateRoot) ? candidateRoot : string.Empty;
        var processingMode = ReadOptionalString(
                                 file,
                                 $"{root}/metadata/stages/reconstruction/processing_mode") ??
                             ReadOptionalString(file, $"{root}/metadata/run/processing_mode") ??
                             "legacy-unclassified";
        var dynamicAction = ReadDynamicKalmanAction(file, root);
        var lane = processingMode.StartsWith("offline-catch-up", StringComparison.OrdinalIgnoreCase) ||
                   processingMode.StartsWith("recovered-existing", StringComparison.OrdinalIgnoreCase)
            ? ReconstructionLane.LegacyOfflineIncomplete
            : ReconstructionLane.LegacyLiveUnverified;
        return new LegacyFrameClassification(
            artifact,
            block,
            lane,
            processingMode,
            dynamicAction);
    }

    private static string? ReadDynamicKalmanAction(IH5Group file, string root)
    {
        var path = $"{root}/metadata/reconstruction_json";
        if (!file.LinkExists(path))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<DerivedReconstructionMetadata>(
            file.Dataset(path).Read<string>());
        return metadata?.DynamicKalmanAction;
    }

    private static string? ReadOptionalString(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<string>() : null;

    private sealed record LegacyFrameClassification(
        DerivedArtifactCatalogRecord Artifact,
        ProcessingBlockCatalogRecord Block,
        string Lane,
        string ProcessingMode,
        string? DynamicKalmanAction);
}

public sealed record ReconstructionLaneMigrationReport(
    Guid ExperimentRunId,
    int CandidateCount,
    int MigratedCount,
    int LegacyLiveCount,
    int LegacyOfflineIncompleteCount,
    IReadOnlyList<string> Failures);
