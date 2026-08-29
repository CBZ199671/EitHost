using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

public sealed class ExperimentReconstructionCatchUpService
{
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly IRealtimeReconstructionBackend backend;
    private readonly DerivedArtifactHdf5Writer writer;
    private readonly CanonicalExperimentReplaySource replaySource;

    public ExperimentReconstructionCatchUpService(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        IRealtimeReconstructionBackend backend,
        DerivedArtifactHdf5Writer? writer = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.writer = writer ?? new DerivedArtifactHdf5Writer();
        replaySource = new CanonicalExperimentReplaySource(this.layout, this.catalog);
    }

    public async Task<ExperimentReconstructionCatchUpReport> RunAsync(
        Guid experimentRunId,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(experimentRunId, progress: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconstructs every demodulated block the live pipeline could not. Each block is committed
    /// independently, so cancelling leaves the remainder pending for a later idempotent retry.
    /// </summary>
    public async Task<ExperimentReconstructionCatchUpReport> RunAsync(
        Guid experimentRunId,
        IProgress<ExperimentCatchUpProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        // Observed before any catalog work so an already-cancelled request is honoured even when
        // the run has nothing pending.
        cancellationToken.ThrowIfCancellationRequested();
        var run = catalog.GetRun(experimentRunId) ?? throw new InvalidOperationException(
            $"Experiment run {experimentRunId:D} does not exist.");
        EnsureTerminalRun(run);
        var indexedEpochs = catalog.ListReferenceEpochs(experimentRunId);
        if (indexedEpochs.Any(epoch => epoch.LockedStartSampleIndex < 0))
        {
            return SetUnavailableStatus(
                run,
                "one or more reference epochs have no safe source sample anchor");
        }

        var detail = replaySource.GetImagingRunDetail(experimentRunId);
        var runConfig = catalog.GetRunConfig(experimentRunId);
        if (detail is null || runConfig is null)
        {
            return SetUnavailableStatus(run, "canonical experiment run config is unavailable");
        }

        var persistedEpochs = replaySource.ListReferenceEpochs(experimentRunId);
        if (persistedEpochs.Count != indexedEpochs.Count)
        {
            return SetUnavailableStatus(
                run,
                "one or more indexed reference artifacts are unavailable");
        }

        var epochs = persistedEpochs
            .Where(epoch => epoch.LockedStartSampleIndex >= 0)
            .OrderBy(epoch => epoch.LockedStartSampleIndex)
            .ThenBy(epoch => epoch.ReferenceEpoch)
            .ToArray();
        var demodArtifacts = catalog.ListDerivedArtifacts(experimentRunId)
            .Where(artifact => string.Equals(artifact.Kind, "demod", StringComparison.Ordinal))
            .GroupBy(artifact => artifact.BlockNumber)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First());
        var pending = catalog.ListProcessingBlocks(experimentRunId)
            .Where(block => string.Equals(block.DemodStatus, "ready", StringComparison.Ordinal))
            .Where(block => block.ReconstructionStatus is not ("ready" or "not_applicable"))
            .OrderBy(block => block.SourceStartSampleIndex)
            .ThenBy(block => block.BlockNumber)
            .ToArray();
        var recovered = 0;
        var imported = 0;
        var failed = 0;
        var notApplicable = 0;
        var processedBlocks = 0;
        progress?.Report(new ExperimentCatchUpProgress(
            experimentRunId,
            ExperimentCatchUpPhase.Reconstructing,
            0,
            pending.Length));

        foreach (var block in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reported on entry so every exit path below - skipped, not-applicable or failed -
            // still advances the indicator.
            processedBlocks++;
            progress?.Report(new ExperimentCatchUpProgress(
                experimentRunId,
                ExperimentCatchUpPhase.Reconstructing,
                processedBlocks,
                pending.Length));
            var epoch = ReferenceReconstructionCoordinator.SelectCatchUpEpoch(
                epochs,
                block.SourceStartSampleIndex,
                candidate => candidate.LockedStartSampleIndex,
                candidate => candidate.ReferenceEpoch);
            if (epoch is null)
            {
                MarkNotApplicable(block);
                notApplicable++;
                continue;
            }

            try
            {
                var frame = replaySource.GetFrame(experimentRunId, block.BlockNumber);
                if (frame is { ReferenceInvalidated: true })
                {
                    MarkNotApplicable(block);
                    notApplicable++;
                    continue;
                }

                var demodPayload = ReadDemodPayload(
                    experimentRunId,
                    block.BlockNumber,
                    demodArtifacts);
                if (demodPayload is { IsHighQuality: false })
                {
                    MarkNotApplicable(block);
                    notApplicable++;
                    continue;
                }

                var target = frame?.MeanAmplitude208 ?? demodPayload?.TargetVoltage208
                    ?? throw new FileNotFoundException(
                        $"Neither replay frame nor demod artifact exists for block {block.BlockNumber}.");
                if (epoch.CommonScaleNormalized)
                {
                    target = EcdCwrCommonScaleNormalizer
                        .NormalizeVector(epoch.ReferenceAmplitude208, target)
                        .Values;
                }

                var processedAt = DateTimeOffset.UtcNow;
                var storedWeights = frame?.MeasurementWeight208;
                var weights = storedWeights ??
                    Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray();
                var weightPolicy = storedWeights is null
                    ? "offline-catch-up-all-one-v1"
                    : frame!.WeightPolicyVersion;
                var processingMode = "offline-catch-up-v1";
                RealtimeReconstructionResult result;
                if (frame?.Conductivity is { Length: > 0 } existingConductivity)
                {
                    result = new RealtimeReconstructionResult(
                        block.BlockNumber,
                        string.Empty,
                        existingConductivity,
                        detail.NodeCoords ?? new double[0, 2],
                        detail.CellConnectivity ?? new int[0, 3],
                        processedAt,
                        TimeSpan.Zero,
                        RawConductivity: frame.RawConductivity,
                        WeightedSystemConditionNumber: frame.ReconstructionConditionNumber,
                        ReconstructionScaleStatus: detail.ReconstructionScaleStatus,
                        ReconstructionScaleProvenance: detail.ReconstructionScaleProvenance);
                    processingMode = "recovered-existing-frame-v1";
                    imported++;
                }
                else
                {
                    var request = new RealtimeReconstructionRequest(
                        run.SetLabel,
                        block.BlockNumber,
                        block.AcquiredAt,
                        epoch.ReferenceAmplitude208,
                        target,
                        detail.ActualFrequencyHz ?? detail.FrequencyHz,
                        detail.ChannelCycles,
                        detail.MeshSize,
                        detail.DifferenceLambda,
                        persistResultFiles: false,
                        detail.ReconstructionRoute,
                        detail.CustomLambdaEnabled,
                        detail.DifferenceOrientation,
                        weights,
                        weightPolicy,
                        dynamicKalman: null,
                        detail.ReconstructionScaleStatus,
                        detail.ReconstructionScaleProvenance);
                    result = await backend.ReconstructAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        throw new InvalidDataException(result.ErrorMessage ?? "offline reconstruction returned no conductivity");
                    }

                    recovered++;
                }

                PersistResult(
                    run,
                    block,
                    result,
                    processedAt,
                    processingMode,
                    epoch.ReferenceEpoch,
                    weightPolicy,
                    epoch.ReferenceAmplitude208,
                    target,
                    weights);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                catalog.RecordReconstructionOutcome(
                    block,
                    "failed",
                    DateTimeOffset.UtcNow,
                    failureMessage: ex.Message);
            }
        }

        var status = UpdateRunStatus(experimentRunId);
        var coverage = catalog.GetCoverage(experimentRunId);
        return new ExperimentReconstructionCatchUpReport(
            experimentRunId,
            pending.Length,
            recovered,
            imported,
            failed,
            notApplicable,
            coverage.ReconstructionPendingCount,
            status,
            null);
    }

    private static void EnsureTerminalRun(ExperimentRunRecord run)
    {
        if (run.Status is not (
                ExperimentCatalog.CompletedStatus or
                ExperimentCatalog.InterruptedStatus or
                ExperimentCatalog.FailedStatus))
        {
            throw new InvalidOperationException(
                $"Reconstruction catch-up requires a terminal experiment run; current status is '{run.Status}'.");
        }
    }

    private DemodCatchUpPayload? ReadDemodPayload(
        Guid experimentRunId,
        int blockNumber,
        IReadOnlyDictionary<int, DerivedArtifactCatalogRecord> artifacts)
    {
        if (!artifacts.TryGetValue(blockNumber, out var artifact))
        {
            return null;
        }

        var path = layout.ResolveArtifactPath(artifact.ArtifactPath);
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var candidateRoot = DataRootLayout.GetDerivedBlockRoot(blockNumber);
        var blockRoot = file.LinkExists(candidateRoot) ? candidateRoot : string.Empty;
        var embeddedRunId = Guid.Parse(
            file.Dataset($"{blockRoot}/metadata/run/experiment_run_id").Read<string>());
        var embeddedBlock = file.Dataset($"{blockRoot}/metadata/run/block_number").Read<int>();
        if (embeddedRunId != experimentRunId || embeddedBlock != blockNumber)
        {
            throw new InvalidDataException("Demod artifact identity does not match the processing ledger.");
        }

        return new DemodCatchUpPayload(
            file.Dataset($"{blockRoot}/demod/mean_amplitude_208").Read<double[]>(),
            !file.LinkExists($"{blockRoot}/quality/is_high_quality") ||
            file.Dataset($"{blockRoot}/quality/is_high_quality").Read<bool>());
    }

    private void PersistResult(
        ExperimentRunRecord run,
        ProcessingBlockCatalogRecord block,
        RealtimeReconstructionResult result,
        DateTimeOffset processedAt,
        string processingMode,
        int referenceEpoch,
        string weightPolicyVersion,
        double[] referenceVoltage208,
        double[] targetVoltage208,
        double[] measurementWeight208)
    {
        var derivedDirectory = Path.Combine(
            layout.ResolveArtifactPath(run.RunDirectory),
            "derived");
        Directory.CreateDirectory(derivedDirectory);
        var outputPath = layout.GetDerivedBlockPath(run.RunDirectory, block.BlockNumber);
        writer.WriteReconstruction(
            outputPath,
            new DerivedReconstructionData(
                run.ExperimentRunId,
                block.BlockNumber,
                block.SourceStartSampleIndex,
                block.SourceEndSampleIndex,
                block.AcquiredAt,
                processedAt,
                result.Conductivity,
                result.RawConductivity,
                null,
                result.WeightedSystemConditionNumber,
                processingMode,
                referenceEpoch,
                weightPolicyVersion,
                referenceVoltage208,
                targetVoltage208,
                measurementWeight208,
                ReconstructionBackendElapsedMilliseconds: result.BackendElapsed.TotalMilliseconds));
        if (result.NodeCoords.Length > 0 && result.CellConnectivity.Length > 0)
        {
            writer.WriteMesh(
                Path.Combine(derivedDirectory, "mesh.h5"),
                new DerivedMeshData(
                    run.ExperimentRunId,
                    processedAt,
                    result.NodeCoords,
                    result.CellConnectivity));
        }

        catalog.RecordReconstructionOutcome(
            block,
            "ready",
            processedAt,
            new DerivedArtifactCatalogRecord(
                run.ExperimentRunId,
                block.BlockNumber,
                "reconstruction",
                layout.ToRelativeArtifactPath(outputPath),
                DataRootLayout.GetDerivedDatasetPath(
                    block.BlockNumber,
                    "/reconstruction/conductivity"),
                processedAt));
    }

    private void MarkNotApplicable(ProcessingBlockCatalogRecord block)
    {
        catalog.RecordReconstructionOutcome(
            block,
            "not_applicable",
            DateTimeOffset.UtcNow);
    }

    private string UpdateRunStatus(Guid experimentRunId)
    {
        var current = catalog.GetRun(experimentRunId)!;
        var coverage = catalog.GetCoverage(experimentRunId);
        var status = coverage.ReconstructionFailedCount > 0
            ? "incomplete"
            : coverage.ReconstructionPendingCount > 0
                ? coverage.ReconstructionReadyCount > 0 ? "partial" : "pending"
                : coverage.ReconstructionReadyCount > 0
                    ? "complete"
                    : coverage.ReconstructionNotApplicableCount > 0
                        ? "not_applicable"
                        : "pending";
        catalog.SetRunStageStatuses(
            experimentRunId,
            current.RawStatus,
            current.DemodStatus,
            status);
        return status;
    }

    private ExperimentReconstructionCatchUpReport SetUnavailableStatus(
        ExperimentRunRecord run,
        string message)
    {
        var coverage = catalog.GetCoverage(run.ExperimentRunId);
        catalog.SetRunStageStatuses(
            run.ExperimentRunId,
            run.RawStatus,
            run.DemodStatus,
            coverage.ReconstructionPendingCount > 0 ? "incomplete" : run.ReconstructionStatus);
        return new ExperimentReconstructionCatchUpReport(
            run.ExperimentRunId,
            coverage.ReconstructionPendingCount,
            0,
            0,
            0,
            0,
            coverage.ReconstructionPendingCount,
            coverage.ReconstructionPendingCount > 0 ? "incomplete" : run.ReconstructionStatus,
            message);
    }

    private sealed record DemodCatchUpPayload(double[] TargetVoltage208, bool IsHighQuality);
}

public sealed record ExperimentReconstructionCatchUpReport(
    Guid ExperimentRunId,
    int CandidateBlockCount,
    int RecoveredBlockCount,
    int ImportedExistingCount,
    int FailedBlockCount,
    int NotApplicableCount,
    int PendingBlockCount,
    string ReconstructionStatus,
    string? UnavailableReason);
