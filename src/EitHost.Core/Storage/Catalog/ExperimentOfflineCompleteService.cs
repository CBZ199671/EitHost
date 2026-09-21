using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using EitHost.Core.Acquisition;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

public sealed record OfflineCompletePreflight(
    Guid ExperimentRunId,
    bool CanStart,
    string Reason,
    long RawSampleRows,
    long RawArtifactBytes,
    int DemodBlockCount,
    int ExistingTerminalOutcomeCount,
    long EstimatedIncrementalBytes,
    long AvailableBytes,
    string? ResumableRevisionId,
    string? AlgorithmFingerprint,
    string? PublishedRevisionId = null);

public sealed record OfflineCompleteReport(
    Guid ExperimentRunId,
    string? RevisionId,
    bool Published,
    int DemodBlockCount,
    int ReconstructedCount,
    int NeutralCount,
    int ExcludedCount,
    string Status,
    string? UnavailableReason = null);

public sealed record OfflineRevisionDeletionReport(
    Guid ExperimentRunId,
    string RevisionId,
    bool CleanupComplete,
    string? RecoveryDirectory = null,
    string? CleanupError = null);

/// <summary>
/// Builds the manual, immutable offline-complete reconstruction lane. It never reads or mutates
/// live Kalman state and never falls back to all-one measurement weights.
/// </summary>
public sealed class ExperimentOfflineCompleteService
{
    private const long EstimatedBytesPerReconstruction = 512L * 1024L;
    private const long StorageSafetyBytes = 64L * 1024L * 1024L;
    private const int OfflineReferenceMinimumFrameCount = 100;
    private const string OfflineContactEvidencePolicyVersion = "offline-contact-full-complex-256-v1";
    private const string OfflineBoundaryEvidencePolicyVersion = "offline-boundary-reference-candidates-v1";
    private const string OfflineContactUnavailablePrefix = "offline-contact-unavailable";
    private const string OfflineBoundaryUnavailablePrefix = "offline-boundary-unavailable";
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly IRealtimeReconstructionBackend backend;
    private readonly DerivedArtifactHdf5Writer writer;
    private readonly GlobalReconstructionMeshStore meshStore;
    private readonly CanonicalExperimentReplaySource replaySource;
    private readonly EcdCwrCenteredTemporalDespiker temporalDespiker = new();
    private readonly ConcurrentDictionary<(Guid RunId, string RevisionId), string> revisionMeshFingerprints = new();

    public ExperimentOfflineCompleteService(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        IRealtimeReconstructionBackend backend,
        DerivedArtifactHdf5Writer? writer = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.writer = writer ?? new DerivedArtifactHdf5Writer();
        meshStore = new GlobalReconstructionMeshStore(this.layout, this.writer);
        replaySource = new CanonicalExperimentReplaySource(this.layout, this.catalog);
    }

    public OfflineRevisionDeletionReport DeleteRevision(Guid experimentRunId, string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        var run = catalog.GetRun(experimentRunId) ??
            throw new KeyNotFoundException($"Experiment run {experimentRunId:D} does not exist.");
        var revision = catalog.GetReconstructionRevision(
                experimentRunId,
                ReconstructionLane.OfflineComplete,
                revisionId) ??
            throw new KeyNotFoundException(
                $"Reconstruction revision {ReconstructionLane.OfflineComplete}/{revisionId} does not exist.");
        if (!IsTerminal(run))
        {
            throw new InvalidOperationException("Only a terminal experiment may delete an offline revision.");
        }

        var runDirectory = layout.ResolveArtifactPath(run.RunDirectory);
        var quarantineRoot = Path.Combine(runDirectory, "offline", ".deleted");
        var deleteToken = Guid.NewGuid().ToString("N");
        var moved = new List<(string Source, string Quarantine)>();
        try
        {
            foreach (var (source, suffix) in new[]
                     {
                         (layout.GetOfflineRevisionDirectory(run.RunDirectory, revision.RevisionId), "published"),
                         (layout.GetOfflineRevisionDirectory(run.RunDirectory, revision.RevisionId, staging: true), "staging")
                     })
            {
                EnsurePathIsWithinRun(runDirectory, source);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                Directory.CreateDirectory(quarantineRoot);
                var quarantine = Path.Combine(
                    quarantineRoot,
                    $"{revision.RevisionId}-{deleteToken}-{suffix}");
                Directory.Move(source, quarantine);
                moved.Add((source, quarantine));
            }

            catalog.DeleteReconstructionRevision(
                experimentRunId,
                ReconstructionLane.OfflineComplete,
                revision.RevisionId);
        }
        catch
        {
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                if (!Directory.Exists(item.Quarantine))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.Source)!);
                Directory.Move(item.Quarantine, item.Source);
            }

            throw;
        }

        try
        {
            foreach (var item in moved)
            {
                Directory.Delete(item.Quarantine, recursive: true);
            }

            if (Directory.Exists(quarantineRoot) &&
                !Directory.EnumerateFileSystemEntries(quarantineRoot).Any())
            {
                Directory.Delete(quarantineRoot);
            }

            return new OfflineRevisionDeletionReport(experimentRunId, revision.RevisionId, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new OfflineRevisionDeletionReport(
                experimentRunId,
                revision.RevisionId,
                false,
                quarantineRoot,
                ex.Message);
        }
    }

    public OfflineCompletePreflight Preflight(Guid experimentRunId)
    {
        var run = catalog.GetRun(experimentRunId);
        if (run is null)
        {
            return UnavailablePreflight(experimentRunId, "实验不存在。");
        }

        if (!IsTerminal(run))
        {
            return UnavailablePreflight(experimentRunId, "仅终态实验可以执行离线完整重算。");
        }

        var readiness = catalog.GetOfflinePipelineReadiness(experimentRunId);
        if (!readiness.Available || readiness.Manifest?.Inputs is not { } inputs ||
            readiness.AlgorithmFingerprint is not { } fingerprint)
        {
            return UnavailablePreflight(experimentRunId, readiness.Reason);
        }

        var blocks = catalog.ListProcessingBlocks(experimentRunId)
            .Where(block => string.Equals(block.DemodStatus, "ready", StringComparison.Ordinal))
            .OrderBy(block => block.SourceStartSampleIndex)
            .ThenBy(block => block.BlockNumber)
            .ToArray();
        if (!MatchesFinalizedInventory(blocks, inputs.DemodBlocks))
        {
            return UnavailablePreflight(
                experimentRunId,
                "当前解调块与终态算法清单不一致；请先刷新终态输入清单。",
                inputs,
                fingerprint);
        }

        var published = catalog.GetPublishedReconstructionRevision(
            experimentRunId,
            ReconstructionLane.OfflineComplete);
        if (published is { IsComplete: true } &&
            string.Equals(published.AlgorithmFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new OfflineCompletePreflight(
                experimentRunId,
                false,
                $"离线完整版本 {published.RevisionId} 已发布，无需重复计算。",
                inputs.RawSampleRows,
                inputs.RawSegments.Sum(item => item.ArtifactBytes),
                blocks.Length,
                published.TerminalOutcomeCount,
                0,
                GetAvailableBytes(),
                null,
                fingerprint,
                published.RevisionId);
        }

        var resumable = catalog.ListReconstructionRevisions(
                experimentRunId,
                ReconstructionLane.OfflineComplete)
            .FirstOrDefault(revision =>
                !revision.IsPublished &&
                string.Equals(revision.AlgorithmFingerprint, fingerprint, StringComparison.Ordinal));
        var existingCount = resumable?.TerminalOutcomeCount ?? 0;
        var remaining = Math.Max(0, blocks.Length - existingCount);
        var estimate = checked((long)remaining * EstimatedBytesPerReconstruction);
        var available = GetAvailableBytes();
        var enoughSpace = available < 0 || available >= estimate + StorageSafetyBytes;
        return new OfflineCompletePreflight(
            experimentRunId,
            enoughSpace,
            enoughSpace ? "ready" : "可用磁盘空间不足，无法安全创建离线完整版本。",
            inputs.RawSampleRows,
            inputs.RawSegments.Sum(item => item.ArtifactBytes),
            blocks.Length,
            existingCount,
            estimate,
            available,
            resumable?.RevisionId,
            fingerprint);
    }

    public async Task<OfflineCompleteReport> RunAsync(
        Guid experimentRunId,
        IProgress<ExperimentCatchUpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preflight = Preflight(experimentRunId);
        if (preflight.PublishedRevisionId is { } publishedRevisionId)
        {
            return CreatePublishedReport(experimentRunId, publishedRevisionId);
        }
        if (!preflight.CanStart)
        {
            return new OfflineCompleteReport(
                experimentRunId,
                preflight.ResumableRevisionId,
                false,
                preflight.DemodBlockCount,
                0,
                0,
                0,
                "unavailable",
                preflight.Reason);
        }

        var run = catalog.GetRun(experimentRunId)!;
        var readiness = catalog.GetOfflinePipelineReadiness(experimentRunId);
        var manifest = readiness.Manifest!;
        var fingerprint = readiness.AlgorithmFingerprint!;
        var blocks = catalog.ListProcessingBlocks(experimentRunId)
            .Where(block => string.Equals(block.DemodStatus, "ready", StringComparison.Ordinal))
            .OrderBy(block => block.SourceStartSampleIndex)
            .ThenBy(block => block.BlockNumber)
            .ToArray();
        var revisionId = preflight.ResumableRevisionId ?? CreateRevisionId();
        var now = DateTimeOffset.UtcNow;
        var existingRevision = catalog.GetReconstructionRevision(
            experimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId);
        catalog.UpsertReconstructionRevision(new ReconstructionRevisionCatalogRecord(
            experimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            ReconstructionRevisionStatus.Staged,
            fingerprint,
            preflight.RawSampleRows,
            blocks.Length,
            existingRevision?.TerminalOutcomeCount ?? 0,
            existingRevision?.ReconstructedCount ?? 0,
            existingRevision?.NeutralCount ?? 0,
            existingRevision?.ExcludedCount ?? 0,
            preflight.EstimatedIncrementalBytes,
            existingRevision?.CreatedAt ?? now,
            now));

        try
        {
            if (TryFinishInterruptedPublish(run, revisionId, preflight.RawSampleRows, blocks.Length))
            {
                return CreatePublishedReport(experimentRunId, revisionId);
            }

            Directory.CreateDirectory(layout.GetOfflineRevisionDirectory(run.RunDirectory, revisionId, staging: true));
            var epochs = replaySource.ListReferenceEpochs(experimentRunId)
                .OrderBy(epoch => epoch.LockedStartSampleIndex)
                .ThenBy(epoch => epoch.ReferenceEpoch)
                .ToArray();
            ValidateReferenceEpochs(epochs);
            var referenceCandidates = replaySource.ListReferenceCandidates(experimentRunId);
            var diagnosticStates = CreateOfflineDiagnosticStates(
                manifest,
                epochs,
                referenceCandidates);
            var artifactLookup = catalog.ListDerivedArtifacts(experimentRunId)
                .GroupBy(item => (item.BlockNumber, item.Kind))
                .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First());
            var inputs = new List<OfflineBlockInput>(blocks.Length);
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputs.Add(ReadInput(block, manifest, epochs, artifactLookup));
            }

            var diagnosedInputs = ApplyIndependentContactDiagnostics(
                inputs,
                manifest,
                diagnosticStates);
            var plans = CreatePlans(diagnosedInputs, manifest);
            progress?.Report(new ExperimentCatchUpProgress(
                experimentRunId,
                ExperimentCatchUpPhase.Reconstructing,
                0,
                plans.Count));
            var completed = 0;
            var presentationScale = new RealtimeImageColorScaleTracker();
            OfflineBlockInput? previousPresentationInput = null;
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var presentation = EvaluateOfflinePresentation(
                    plan,
                    diagnosticStates,
                    previousPresentationInput);
                previousPresentationInput = plan.Input;
                if (plan.Outcome == ReconstructionFrameOutcome.Reconstructed)
                {
                    await ReconstructAndRecordAsync(
                        run,
                        revisionId,
                        fingerprint,
                        manifest,
                        plan,
                        presentation,
                        presentationScale,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RecordTerminalOutcome(run, revisionId, fingerprint, plan, presentation);
                }

                completed++;
                progress?.Report(new ExperimentCatchUpProgress(
                    experimentRunId,
                    ExperimentCatchUpPhase.Reconstructing,
                    completed,
                    plans.Count));
            }

            ValidateStagedCoverage(experimentRunId, revisionId, blocks.Length);
            PublishStagedRevision(run, revisionId, preflight.RawSampleRows, blocks.Length);
            return CreatePublishedReport(experimentRunId, revisionId);
        }
        catch (OperationCanceledException)
        {
            catalog.SetReconstructionRevisionStatus(
                experimentRunId,
                ReconstructionLane.OfflineComplete,
                revisionId,
                ReconstructionRevisionStatus.Canceled,
                DateTimeOffset.UtcNow,
                "operator canceled; staged progress is resumable");
            throw;
        }
        catch (Exception ex)
        {
            catalog.SetReconstructionRevisionStatus(
                experimentRunId,
                ReconstructionLane.OfflineComplete,
                revisionId,
                ReconstructionRevisionStatus.Failed,
                DateTimeOffset.UtcNow,
                ex.Message);
            throw;
        }
    }

    private OfflineBlockInput ReadInput(
        ProcessingBlockCatalogRecord block,
        ReconstructionPipelineManifestPayload manifest,
        IReadOnlyList<ImagingReferenceEpochRecord> epochs,
        IReadOnlyDictionary<(int BlockNumber, string Kind), DerivedArtifactCatalogRecord> artifacts)
    {
        if (!artifacts.TryGetValue((block.BlockNumber, "demod"), out var demodArtifact))
        {
            throw new InvalidDataException($"block {block.BlockNumber} 缺少 demod 工件。");
        }

        double[]? target = null;
        double[]? fullAmplitude256 = null;
        double[]? fullReal256 = null;
        double[]? fullImaginary256 = null;
        Pseudo3dAcquisitionStamp? timeDivision = null;
        var highQuality = block.AcceptedFrameCount >= manifest.Demodulation.MinimumAcceptedFrames;
        using (var file = Hdf5FileAccess.OpenReadWithRetry(
                   layout.ResolveArtifactPath(demodArtifact.ArtifactPath)))
        {
            var blockRoot = file.LinkExists(DataRootLayout.GetDerivedBlockRoot(block.BlockNumber))
                ? DataRootLayout.GetDerivedBlockRoot(block.BlockNumber)
                : string.Empty;
            ValidateBlockIdentity(file, block, blockRoot);
            if (file.LinkExists(At(blockRoot, "/demod/mean_amplitude_208")))
            {
                target = file.Dataset(At(blockRoot, "/demod/mean_amplitude_208")).Read<double[]>();
            }

            if (file.LinkExists(At(blockRoot, "/quality/is_high_quality")))
            {
                highQuality = file.Dataset(At(blockRoot, "/quality/is_high_quality")).Read<bool>();
            }

            fullAmplitude256 = ReadOptionalVector(file, At(blockRoot, "/demod/mean_full_amplitude_256"));
            fullReal256 = ReadOptionalVector(file, At(blockRoot, "/demod/mean_full_real_256"));
            fullImaginary256 = ReadOptionalVector(file, At(blockRoot, "/demod/mean_full_imaginary_256"));
            if (file.LinkExists(At(blockRoot, "/demod/time_division_json")))
            {
                timeDivision = JsonSerializer.Deserialize<Pseudo3dAcquisitionStamp>(
                    file.Dataset(At(blockRoot, "/demod/time_division_json")).Read<string>()) ??
                    throw new InvalidDataException($"block {block.BlockNumber} 的分时采集标记为空。");
                if (timeDivision.Round != block.BlockNumber || timeDivision.SampleMidpoint != block.AcquiredAt)
                    throw new InvalidDataException($"block {block.BlockNumber} 的分时轮次或采样中点不匹配。");
            }
        }

        if (target is not { Length: RealtimeReconstructionRequest.BoundaryVoltageCount })
        {
            throw new InvalidDataException($"block {block.BlockNumber} 的解调边界电压不是 208 点。");
        }

        var epoch = epochs.LastOrDefault(candidate =>
            candidate.LockedStartSampleIndex >= 0 &&
            candidate.LockedStartSampleIndex < block.SourceStartSampleIndex);
        // A preview reference has no formal diagnostic baseline. Preserve these
        // blocks as explicit no-reference outcomes; never reach back to an older
        // epoch or invent candidates/weights for the provisional interval.
        if (epoch?.LockKind == "provisional_preview")
        {
            epoch = null;
        }

        return new OfflineBlockInput(
            block,
            target,
            null,
            fullAmplitude256,
            fullReal256,
            fullImaginary256,
            null,
            string.Empty,
            null,
            highQuality,
            ReferenceInvalidated: false,
            epoch,
            ElectrodeStates: null,
            ContactSummary: null,
            ContactEvidencePolicy: $"{OfflineContactUnavailablePrefix}:not-evaluated",
            TimeDivision: timeDivision);
    }

    private static bool AreConsecutiveInputs(OfflineBlockInput previous, OfflineBlockInput current)
    {
        if (previous.TimeDivision is null && current.TimeDivision is null)
            return previous.Block.SourceEndSampleIndex == current.Block.SourceStartSampleIndex;

        // Finite slots discard quiet leading/trailing ADC rows. Those sample gaps
        // are expected only when persisted acquisition stamps prove adjacent rounds
        // for the same device/session/profile; a missing round still resets state.
        return previous.Block.SourceEndSampleIndex <= current.Block.SourceStartSampleIndex &&
               Pseudo3dTimeDivisionContract.AreConsecutiveAcquisitions(previous.TimeDivision, current.TimeDivision);
    }

    private static IReadOnlyDictionary<int, OfflineDiagnosticState> CreateOfflineDiagnosticStates(
        ReconstructionPipelineManifestPayload manifest,
        IReadOnlyList<ImagingReferenceEpochRecord> epochs,
        IReadOnlyList<ImagingReferenceCandidateRecord> candidates)
    {
        var duplicateCandidate = candidates
            .GroupBy(candidate => candidate.SourceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCandidate is not null)
        {
            throw new InvalidDataException(
                $"参考候选 source id 重复：{duplicateCandidate.Key}；无法独立重建离线参考噪声模型。");
        }

        var candidatesById = candidates.ToDictionary(candidate => candidate.SourceId, StringComparer.Ordinal);
        var states = new Dictionary<int, OfflineDiagnosticState>();
        foreach (var epoch in epochs)
        {
            if (epoch.LockKind == "provisional_preview")
            {
                continue;
            }

            var boundary = CreateOfflineBoundaryState(manifest, epoch, candidatesById);
            ElectrodeContactMonitor? contactMonitor = null;
            if (boundary.ReferenceFullReal256 is not null && boundary.ReferenceFullImaginary256 is not null)
            {
                var baseline = ElectrodeContactBaseline.FromReference(
                    UnflattenFullObservation(boundary.ReferenceFullReal256, nameof(boundary.ReferenceFullReal256)),
                    UnflattenFullObservation(boundary.ReferenceFullImaginary256, nameof(boundary.ReferenceFullImaginary256)));
                contactMonitor = new ElectrodeContactMonitor(
                    baseline,
                    RealtimeReferenceTolerancePolicy.CreateContactMonitorOptions());
            }

            states.Add(
                epoch.ReferenceEpoch,
                new OfflineDiagnosticState(
                    contactMonitor,
                    boundary.Gate,
                    boundary.NoisePrecisionWeight208,
                    boundary.ReferenceAmplitude208,
                    boundary.Status));
        }

        return states;
    }

    private static OfflineBoundaryState CreateOfflineBoundaryState(
        ReconstructionPipelineManifestPayload manifest,
        ImagingReferenceEpochRecord epoch,
        IReadOnlyDictionary<string, ImagingReferenceCandidateRecord> candidatesById)
    {
        if (epoch.SourceCandidateIds is not { Length: >= OfflineReferenceMinimumFrameCount } sourceIds)
        {
            return OfflineBoundaryState.Unavailable("missing-exact-reference-candidate-ids");
        }

        if (sourceIds.Any(string.IsNullOrWhiteSpace) ||
            sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length)
        {
            return OfflineBoundaryState.Unavailable("invalid-exact-reference-candidate-ids");
        }

        var observations = new List<EcdCwrRobustReferenceObservation>(sourceIds.Length);
        foreach (var sourceId in sourceIds)
        {
            if (!candidatesById.TryGetValue(sourceId, out var candidate) ||
                candidate.ImagingRunId != epoch.ImagingRunId)
            {
                return OfflineBoundaryState.Unavailable($"missing-reference-candidate:{sourceId}");
            }

            observations.Add(new EcdCwrRobustReferenceObservation(
                candidate.Voltage208,
                candidate.FullReal256,
                candidate.FullImaginary256));
        }

        try
        {
            var rebuilt = new EcdCwrRobustReferenceBuilder().CreateFromObservations(
                observations,
                new EcdCwrRobustReferenceOptions(
                    MinimumFrameCount: OfflineReferenceMinimumFrameCount,
                    NormalizeCommonScale: EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(
                        epoch.ReferenceScalePolicy),
                    PhysicalAdcLsbVolts: manifest.Demodulation.AdcLsbVolts,
                    DetrendNoiseModel: string.Equals(
                        epoch.NoiseEstimationPolicy,
                        "linear_detrended_residual-v1",
                        StringComparison.Ordinal),
                    UseShortTermNoiseModel: string.Equals(
                        epoch.NoiseEstimationPolicy,
                        EcdCwrBoundaryNoiseModelBuilder.ShortTermResidualPolicy,
                        StringComparison.Ordinal)));
            if (rebuilt.NoiseModel is not { } noiseModel ||
                !VectorsNearlyEqual(rebuilt.Voltage208, epoch.ReferenceAmplitude208) ||
                !VectorsNearlyEqual(rebuilt.FullReal256, epoch.ReferenceFullReal256) ||
                !VectorsNearlyEqual(rebuilt.FullImaginary256, epoch.ReferenceFullImaginary256) ||
                epoch.NoiseGlobalThreshold is { } threshold &&
                !NearlyEqual(noiseModel.GlobalScoreThreshold, threshold) ||
                epoch.NoisePrecisionWeight208 is { } precision &&
                !VectorsNearlyEqual(noiseModel.PrecisionWeight208, precision))
            {
                return OfflineBoundaryState.Unavailable("reference-candidate-rebuild-mismatch");
            }

            return new OfflineBoundaryState(
                new EcdCwrBoundaryChangeGate(noiseModel),
                noiseModel.PrecisionWeight208.ToArray(),
                rebuilt.Voltage208.ToArray(),
                rebuilt.FullReal256.ToArray(),
                rebuilt.FullImaginary256.ToArray(),
                OfflineBoundaryEvidencePolicyVersion);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return OfflineBoundaryState.Unavailable(
                $"reference-candidate-rebuild-failed:{ex.GetType().Name}:{ex.Message}");
        }
    }

    private static IReadOnlyList<OfflineBlockInput> ApplyIndependentContactDiagnostics(
        IReadOnlyList<OfflineBlockInput> inputs,
        ReconstructionPipelineManifestPayload manifest,
        IReadOnlyDictionary<int, OfflineDiagnosticState> diagnosticStates)
    {
        if (manifest.Weighting.OutlierCompensation && !manifest.Demodulation.OutlierDetection)
        {
            throw new InvalidDataException(
                "算法清单启用了异常电极补偿但关闭了异常检测；无法生成独立离线权重。");
        }

        var result = new List<OfflineBlockInput>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input.ReferenceEpoch is null)
            {
                result.Add(input with
                {
                    ContactSummary = "尚无匹配参考 epoch，未执行离线接触诊断。",
                    ContactEvidencePolicy = $"{OfflineContactUnavailablePrefix}:no-reference-epoch"
                });
                continue;
            }

            if (!diagnosticStates.TryGetValue(input.ReferenceEpoch.ReferenceEpoch, out var diagnosticState))
            {
                throw new InvalidDataException(
                    $"block {input.Block.BlockNumber} 缺少 reference epoch {input.ReferenceEpoch.ReferenceEpoch} 的离线诊断状态。");
            }

            var independentReference208 = diagnosticState.ReferenceAmplitude208 ??
                input.ReferenceEpoch.ReferenceAmplitude208;
            var normalizedTarget = EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(
                manifest.Reference.ScalePolicy)
                ? EcdCwrCommonScaleNormalizer.NormalizeVector(independentReference208, input.Target).Values
                : input.Target;

            if (diagnosticState.ContactMonitor is null)
            {
                var policy = $"{OfflineContactUnavailablePrefix}:{diagnosticState.BoundaryStatus}";
                if (input.HighQuality && manifest.Weighting.OutlierCompensation)
                {
                    throw new InvalidDataException(
                        $"block {input.Block.BlockNumber} {policy}；异常电极补偿已启用，禁止读取实时 diagnostics 或回退 persisted/all-one 权重。");
                }

                result.Add(input with
                {
                    Target = normalizedTarget,
                    ReferenceVoltage208 = independentReference208,
                    BaseWeights = Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray(),
                    BasePolicy = "offline-outlier-compensation-disabled-v1",
                    NoisePrecisionWeight208 = diagnosticState.NoisePrecisionWeight208,
                    ReferenceInvalidated = false,
                    ContactSummary = policy,
                    ContactEvidencePolicy = policy
                });
                continue;
            }

            if (!TryValidateFullComplexInput(input, out var unavailableReason))
            {
                var policy = $"{OfflineContactUnavailablePrefix}:{unavailableReason}";
                if (input.HighQuality && manifest.Weighting.OutlierCompensation)
                {
                    throw new InvalidDataException(
                        $"block {input.Block.BlockNumber} {policy}；异常电极补偿已启用，禁止读取实时 diagnostics 或回退 all-one。");
                }

                result.Add(input with
                {
                    Target = normalizedTarget,
                    ReferenceVoltage208 = independentReference208,
                    BaseWeights = manifest.Weighting.OutlierCompensation
                        ? null
                        : Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray(),
                    BasePolicy = "offline-outlier-compensation-disabled-v1",
                    NoisePrecisionWeight208 = diagnosticState.NoisePrecisionWeight208,
                    ReferenceInvalidated = false,
                    ContactSummary = policy,
                    ContactEvidencePolicy = policy
                });
                continue;
            }

            ElectrodeContactDiagnosticResult contact;
            try
            {
                contact = diagnosticState.ContactMonitor.Update(
                    UnflattenFullObservation(input.FullReal256!, nameof(input.FullReal256)),
                    UnflattenFullObservation(input.FullImaginary256!, nameof(input.FullImaginary256)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    $"block {input.Block.BlockNumber} 离线独立接触诊断失败：{ex.Message}",
                    ex);
            }

            var baseWeights = manifest.Weighting.OutlierCompensation
                ? contact.MeasurementWeight208.ToArray()
                : Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray();
            if (baseWeights.Length != RealtimeReconstructionRequest.BoundaryVoltageCount ||
                baseWeights.Any(weight => !double.IsFinite(weight) || weight is < 0.0 or > 1.0))
            {
                throw new InvalidDataException(
                    $"block {input.Block.BlockNumber} 的离线独立诊断权重无效。");
            }

            var basePolicy = manifest.Weighting.OutlierCompensation
                ? $"{OfflineContactEvidencePolicyVersion}:{manifest.Reference.DiagnosticPolicyVersion}+{contact.WeightPolicyVersion}"
                : "offline-outlier-compensation-disabled-v1";
            if (EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(manifest.Reference.ScalePolicy))
            {
                basePolicy += $"+{EcdCwrCommonScaleNormalizer.PolicyVersion}";
            }

            result.Add(input with
            {
                Target = normalizedTarget,
                ReferenceVoltage208 = independentReference208,
                BaseWeights = baseWeights,
                BasePolicy = basePolicy,
                NoisePrecisionWeight208 = diagnosticState.NoisePrecisionWeight208,
                ReferenceInvalidated = manifest.Demodulation.OutlierDetection && contact.ReferenceInvalidated,
                ElectrodeStates = contact.States.Select(state => state.ToString()).ToArray(),
                ContactSummary = contact.Summary,
                ContactEvidencePolicy = OfflineContactEvidencePolicyVersion
            });
        }

        return result;
    }

    private static OfflineFramePresentationEvidence EvaluateOfflinePresentation(
        OfflineFramePlan plan,
        IReadOnlyDictionary<int, OfflineDiagnosticState> diagnosticStates,
        OfflineBlockInput? previousInput)
    {
        var input = plan.Input;
        if (input.ReferenceEpoch is null)
        {
            return new OfflineFramePresentationEvidence(
                "neutral",
                input.ElectrodeStates,
                input.ContactSummary,
                input.ContactEvidencePolicy,
                $"{OfflineBoundaryUnavailablePrefix}:no-reference-epoch",
                plan.ExclusionReason);
        }

        if (!diagnosticStates.TryGetValue(input.ReferenceEpoch.ReferenceEpoch, out var state))
        {
            throw new InvalidDataException(
                $"block {input.Block.BlockNumber} 缺少 reference epoch {input.ReferenceEpoch.ReferenceEpoch} 的离线边界状态。");
        }

        if (state.BoundaryChangeGate is null)
        {
            return new OfflineFramePresentationEvidence(
                plan.Outcome == ReconstructionFrameOutcome.Reconstructed ? "conductivity" : "neutral",
                input.ElectrodeStates,
                input.ContactSummary,
                input.ContactEvidencePolicy,
                state.BoundaryStatus,
                state.BoundaryStatus);
        }

        var continuityReset = previousInput is null ||
            previousInput.ReferenceEpoch?.ReferenceEpoch != input.ReferenceEpoch.ReferenceEpoch ||
            !AreConsecutiveInputs(previousInput, input) ||
            !previousInput.HighQuality ||
            previousInput.ReferenceInvalidated;
        if (continuityReset)
        {
            state.BoundaryChangeGate.Reset();
        }

        if (!input.HighQuality || input.ReferenceInvalidated)
        {
            state.BoundaryChangeGate.Reset();
            return new OfflineFramePresentationEvidence(
                "neutral",
                input.ElectrodeStates,
                input.ContactSummary,
                input.ContactEvidencePolicy,
                $"{OfflineBoundaryUnavailablePrefix}:not-evaluated:{plan.Outcome}",
                plan.ExclusionReason);
        }

        var decision = state.BoundaryChangeGate.Evaluate(plan.Target ?? input.Target);
        var overlay = plan.Outcome == ReconstructionFrameOutcome.Reconstructed &&
            decision.Action == EcdCwrBoundaryChangeAction.Change
            ? "conductivity"
            : "neutral";
        return new OfflineFramePresentationEvidence(
            overlay,
            input.ElectrodeStates,
            input.ContactSummary,
            input.ContactEvidencePolicy,
            decision.Action.ToString(),
            $"{OfflineBoundaryEvidencePolicyVersion}: action={decision.Action}; score={decision.GlobalScore:G6}; threshold={decision.Threshold:G6}; excursions={decision.ExcursionCount}");
    }

    private IReadOnlyList<OfflineFramePlan> CreatePlans(
        IReadOnlyList<OfflineBlockInput> inputs,
        ReconstructionPipelineManifestPayload manifest)
    {
        var plans = new OfflineFramePlan?[inputs.Count];
        var segment = new List<int>();
        var segmentNumber = 0;

        void FlushSegment(bool beginsAfterDiscontinuity)
        {
            if (segment.Count == 0)
            {
                return;
            }

            segmentNumber++;
            var hasKalmanUpdate = false;
            for (var position = 0; position < segment.Count; position++)
            {
                var inputIndex = segment[position];
                var input = inputs[inputIndex];
                if (position == 0 && beginsAfterDiscontinuity)
                {
                    plans[inputIndex] = TerminalPlan(
                        input,
                        inputIndex,
                        ReconstructionFrameOutcome.ExcludedDiscontinuity,
                        "sample discontinuity before block");
                    continue;
                }

                if (!manifest.Weighting.TemporalDespiking)
                {
                    plans[inputIndex] = ReconstructionPlan(
                        input,
                        inputIndex,
                        segmentNumber,
                        resetKalman: !hasKalmanUpdate,
                        input.Target,
                        input.BaseWeights!,
                        input.BasePolicy,
                        false);
                    hasKalmanUpdate = true;
                    continue;
                }

                if (position < manifest.Weighting.TemporalCenterIndex ||
                    position + (manifest.Weighting.TemporalWindowSize - manifest.Weighting.TemporalCenterIndex) > segment.Count)
                {
                    plans[inputIndex] = TerminalPlan(
                        input,
                        inputIndex,
                        ReconstructionFrameOutcome.Neutral,
                        "centered-5 temporal window edge");
                    continue;
                }

                var window = segment
                    .Skip(position - manifest.Weighting.TemporalCenterIndex)
                    .Take(manifest.Weighting.TemporalWindowSize)
                    .Select(index => (IReadOnlyList<double>)inputs[index].Target)
                    .ToArray();
                var temporal = temporalDespiker.Analyze(window, input.BaseWeights!);
                if (temporal.IsGlobalIsolatedSpike)
                {
                    plans[inputIndex] = TerminalPlan(
                        input,
                        inputIndex,
                        ReconstructionFrameOutcome.Neutral,
                        $"isolated global spike {temporal.IsolatedChannelCount}/208");
                    continue;
                }

                var repaired = temporal.RepairedChannelIndices.Length == 0
                    ? "none"
                    : string.Join(',', temporal.RepairedChannelIndices.Select(index => index + 1));
                var policy = $"{input.BasePolicy}+{temporal.WeightPolicyVersion}:repaired1={repaired}";
                var finalWeights = CombineWeights(
                    temporal.CombinedMeasurementWeight208,
                    input.NoisePrecisionWeight208!);
                policy += $"+{manifest.Reference.BoundaryNoisePolicyVersion}";
                plans[inputIndex] = ReconstructionPlan(
                    input,
                    inputIndex,
                    segmentNumber,
                    resetKalman: !hasKalmanUpdate,
                    temporal.RepairedCenter208,
                    finalWeights,
                    policy,
                    temporal.IsolatedChannelCount > 0);
                hasKalmanUpdate = true;
            }

            segment.Clear();
        }

        var beginsAfterGap = false;
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var discontinuity = index > 0 &&
                !AreConsecutiveInputs(inputs[index - 1], input);
            var sameEpoch = segment.Count == 0 ||
                inputs[segment[^1]].ReferenceEpoch?.ReferenceEpoch == input.ReferenceEpoch?.ReferenceEpoch;
            if (discontinuity || !sameEpoch)
            {
                FlushSegment(beginsAfterGap);
                beginsAfterGap = discontinuity;
            }

            if (input.ReferenceEpoch is null)
            {
                FlushSegment(beginsAfterGap);
                plans[index] = TerminalPlan(
                    input,
                    index,
                    ReconstructionFrameOutcome.ExcludedNoReference,
                    "no locked reference epoch before source block");
                beginsAfterGap = false;
                continue;
            }

            if (!input.HighQuality || input.ReferenceInvalidated)
            {
                FlushSegment(beginsAfterGap);
                plans[index] = TerminalPlan(
                    input,
                    index,
                    ReconstructionFrameOutcome.ExcludedInvalid,
                    !input.HighQuality ? "demod block is not high quality" : "reference invalidated by offline diagnostics");
                beginsAfterGap = false;
                continue;
            }

            segment.Add(index);
        }

        FlushSegment(beginsAfterGap);
        return plans.Select(plan => plan ?? throw new InvalidOperationException("Offline frame plan is incomplete.")).ToArray();
    }

    private async Task ReconstructAndRecordAsync(
        ExperimentRunRecord run,
        string revisionId,
        string algorithmFingerprint,
        ReconstructionPipelineManifestPayload manifest,
        OfflineFramePlan plan,
        OfflineFramePresentationEvidence presentation,
        RealtimeImageColorScaleTracker presentationScale,
        CancellationToken cancellationToken)
    {
        var input = plan.Input;
        var sessionId = $"{run.ExperimentRunId:N}:offline:{revisionId}:ref{input.ReferenceEpoch!.ReferenceEpoch}:seg{plan.SegmentNumber}";
        var dynamic = manifest.DynamicKalman.Enabled
            ? new RealtimeDynamicKalmanOptions(
                sessionId,
                $"{algorithmFingerprint};session={sessionId}",
                resetSession: plan.ResetKalman,
                innovationCandidate: plan.TemporalInnovationCandidate,
                upstreamLatencyFrames: manifest.DynamicKalman.UpstreamLatencyFrames,
                processNoiseRelativeStd: manifest.DynamicKalman.ProcessNoiseRelativeStd,
                measurementNoiseRelativeStd: manifest.DynamicKalman.MeasurementNoiseRelativeStd,
                initialRelativeStd: manifest.DynamicKalman.InitialRelativeStd,
                transitionDecayPerBlock: manifest.DynamicKalman.TransitionDecayPerBlock,
                innovationGate: manifest.DynamicKalman.InnovationGate,
                nisThresholdPerDof: manifest.DynamicKalman.NisThresholdPerDof,
                maxVarianceInflation: manifest.DynamicKalman.MaximumVarianceInflation,
                mode: ResolveDynamicMode(manifest.DynamicKalman.Mode))
            : null;
        var request = new RealtimeReconstructionRequest(
            run.SetLabel,
            input.Block.BlockNumber,
            input.Block.AcquiredAt,
            input.ReferenceVoltage208!,
            plan.Target!,
            manifest.Demodulation.ExcitationFrequencyHz,
            manifest.Demodulation.ChannelCycles,
            manifest.Inverse.MeshSize,
            manifest.Inverse.DifferenceLambda,
            persistResultFiles: false,
            manifest.Inverse.Route,
            manifest.Inverse.CustomLambdaEnabled,
            manifest.Inverse.DifferenceOrientation,
            plan.FinalWeights,
            plan.WeightPolicy!,
            dynamic,
            manifest.Inverse.ReconstructionScale,
            manifest.Inverse.ReconstructionScaleProvenance);
        var result = await backend.ReconstructAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidDataException(result.ErrorMessage ?? $"block {input.Block.BlockNumber} 离线重构失败。");
        }

        if (dynamic is not null &&
            (!result.DynamicKalmanApplied ||
             result.DynamicKalmanFallback == true ||
             result.DynamicKalmanAction is not ("initialize" or "update" or "inflate" or "reject")))
        {
            throw new InvalidDataException(
                $"block {input.Block.BlockNumber} 未满足动态 Kalman 等价链：action={result.DynamicKalmanAction ?? "none"}。");
        }

        if (plan.ResetKalman)
        {
            presentationScale.Reset();
        }

        double? scaleCenter = null;
        double? scaleRange = null;
        if (string.Equals(presentation.OverlayDisposition, "neutral", StringComparison.Ordinal))
        {
            presentationScale.Reset();
        }
        else
        {
            var colorScale = presentationScale.Update(result.Conductivity);
            scaleCenter = colorScale.Center;
            scaleRange = colorScale.Range;
        }

        var existing = catalog.GetReconstructionLaneFrame(
            run.ExperimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            input.Block.BlockNumber);
        if (existing is not null && IsPersistedFrameValid(existing))
        {
            return;
        }

        var processedAt = DateTimeOffset.UtcNow;
        var meshReference = meshStore.Ensure(
            run.ExperimentRunId,
            processedAt,
            result.NodeCoords,
            result.CellConnectivity,
            result.Conductivity.Length,
            result.GetMeshIndexMetadata());
        EnsureRevisionMeshIdentity(run.ExperimentRunId, revisionId, meshReference.Fingerprint);
        var outputPath = layout.GetOfflineDerivedBlockPath(
            run.RunDirectory,
            revisionId,
            input.Block.BlockNumber,
            staging: true);
        writer.WriteReconstruction(outputPath, new DerivedReconstructionData(
            run.ExperimentRunId,
            input.Block.BlockNumber,
            input.Block.SourceStartSampleIndex,
            input.Block.SourceEndSampleIndex,
            input.Block.AcquiredAt,
            processedAt,
            result.Conductivity,
            result.RawConductivity,
            null,
            result.WeightedSystemConditionNumber,
            "offline-complete-v1",
            input.ReferenceEpoch.ReferenceEpoch,
            plan.WeightPolicy!,
            input.ReferenceVoltage208!,
            plan.Target,
            plan.FinalWeights,
            dynamic?.SessionId,
            result.DynamicKalmanAction,
            result.DynamicKalmanNisPerDof,
            result.DynamicKalmanGainMean,
            result.DynamicKalmanVarianceInflation,
            result.DynamicKalmanUpdateCount,
            result.DynamicKalmanTotalLatencyFrames,
            result.DynamicKalmanMode,
            result.DynamicKalmanFallback,
            result.DynamicKalmanSolveMilliseconds,
            result.BackendElapsed.TotalMilliseconds,
            meshReference.Fingerprint,
            meshReference.ArtifactPath,
            result.MeshIndexSchema,
            result.ParameterEntity,
            result.LogicalMeshFingerprint,
            result.OrderedIndexFingerprint));
        catalog.RecordReconstructionLaneFrame(new ReconstructionLaneFrameCatalogRecord(
            run.ExperimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            input.Block.BlockNumber,
            plan.SequenceNumber,
            ReconstructionFrameOutcome.Reconstructed,
            input.Block.AcquiredAt,
            processedAt,
            algorithmFingerprint,
            layout.ToRelativeArtifactPath(outputPath),
            DataRootLayout.GetDerivedDatasetPath(input.Block.BlockNumber, "/reconstruction/conductivity"),
            HashDoubles(plan.FinalWeights!),
            dynamic?.SessionId,
            result.DynamicKalmanAction,
            CreatePresentationJson(
                manifest,
                presentation.OverlayDisposition,
                presentation.Stats,
                scaleCenter,
                scaleRange,
                presentation.ElectrodeStates,
                presentation.ContactSummary,
                presentation.ContactEvidencePolicy,
                presentation.BoundaryChangeAction),
            SourceStartSampleIndex: input.Block.SourceStartSampleIndex,
            SourceEndSampleIndex: input.Block.SourceEndSampleIndex,
            ResultHash: HashDoubles(result.Conductivity)));
    }

    private void RecordTerminalOutcome(
        ExperimentRunRecord run,
        string revisionId,
        string algorithmFingerprint,
        OfflineFramePlan plan,
        OfflineFramePresentationEvidence presentation)
    {
        var input = plan.Input;
        catalog.RecordReconstructionLaneFrame(new ReconstructionLaneFrameCatalogRecord(
            run.ExperimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            input.Block.BlockNumber,
            plan.SequenceNumber,
            plan.Outcome,
            input.Block.AcquiredAt,
            DateTimeOffset.UtcNow,
            algorithmFingerprint,
            PresentationJson: CreatePresentationJson(
                catalog.GetOfflinePipelineReadiness(run.ExperimentRunId).Manifest!,
                "neutral",
                plan.ExclusionReason,
                null,
                null,
                presentation.ElectrodeStates,
                presentation.ContactSummary,
                presentation.ContactEvidencePolicy,
                presentation.BoundaryChangeAction),
            ExclusionReason: plan.ExclusionReason,
            SourceStartSampleIndex: input.Block.SourceStartSampleIndex,
            SourceEndSampleIndex: input.Block.SourceEndSampleIndex));
    }

    private bool TryFinishInterruptedPublish(
        ExperimentRunRecord run,
        string revisionId,
        long rawDenominator,
        int demodDenominator)
    {
        var staging = layout.GetOfflineRevisionDirectory(run.RunDirectory, revisionId, staging: true);
        var published = layout.GetOfflineRevisionDirectory(run.RunDirectory, revisionId);
        if (Directory.Exists(staging) || !Directory.Exists(published))
        {
            return false;
        }

        PromoteCatalogPaths(run, revisionId);
        ValidateStagedCoverage(run.ExperimentRunId, revisionId, demodDenominator);
        catalog.PublishReconstructionRevision(
            run.ExperimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            rawDenominator,
            demodDenominator,
            DateTimeOffset.UtcNow);
        return true;
    }

    private void PublishStagedRevision(
        ExperimentRunRecord run,
        string revisionId,
        long rawDenominator,
        int demodDenominator)
    {
        var staging = layout.GetOfflineRevisionDirectory(run.RunDirectory, revisionId, staging: true);
        var published = layout.GetOfflineRevisionDirectory(run.RunDirectory, revisionId);
        if (Directory.Exists(published))
        {
            throw new IOException($"离线发布目录已存在：{published}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(published)!);
        Directory.Move(staging, published);
        PromoteCatalogPaths(run, revisionId);
        catalog.PublishReconstructionRevision(
            run.ExperimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            rawDenominator,
            demodDenominator,
            DateTimeOffset.UtcNow);
    }

    private void PromoteCatalogPaths(ExperimentRunRecord run, string revisionId)
    {
        catalog.PromoteReconstructionRevisionArtifacts(
            run.ExperimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId,
            layout.GetOfflineRevisionRelativeDirectory(run.RunDirectory, revisionId, staging: true),
            layout.GetOfflineRevisionRelativeDirectory(run.RunDirectory, revisionId),
            DateTimeOffset.UtcNow);
    }

    private void ValidateStagedCoverage(Guid experimentRunId, string revisionId, int denominator)
    {
        var frames = catalog.ListReconstructionLaneFrames(
            experimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId);
        if (frames.Count != denominator || frames.Select(frame => frame.SourceBlockNumber).Distinct().Count() != denominator)
        {
            throw new InvalidDataException($"离线完整覆盖校验失败：{frames.Count}/{denominator}。");
        }

        foreach (var frame in frames.Where(frame => frame.Outcome == ReconstructionFrameOutcome.Reconstructed))
        {
            if (!IsPersistedFrameValid(frame))
            {
                throw new InvalidDataException(
                    $"block {frame.SourceBlockNumber} 的离线重构工件缺失、dataset 无效或结果哈希不一致。");
            }
        }
    }

    private bool IsPersistedFrameValid(ReconstructionLaneFrameCatalogRecord frame)
    {
        if (frame.Outcome != ReconstructionFrameOutcome.Reconstructed ||
            frame.ArtifactPath is null ||
            frame.DatasetPath is null ||
            frame.ResultHash is null)
        {
            return false;
        }

        var path = layout.ResolveArtifactPath(frame.ArtifactPath);
        if (!File.Exists(path))
        {
            var run = catalog.GetRun(frame.ExperimentRunId)!;
            var publishedPrefix = layout.GetOfflineRevisionRelativeDirectory(run.RunDirectory, frame.RevisionId);
            var stagingPrefix = layout.GetOfflineRevisionRelativeDirectory(
                run.RunDirectory,
                frame.RevisionId,
                staging: true);
            if (frame.ArtifactPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(layout.ResolveArtifactPath(publishedPrefix)))
            {
                path = layout.ResolveArtifactPath(
                    publishedPrefix + frame.ArtifactPath[stagingPrefix.Length..]);
            }
        }

        if (!File.Exists(path))
        {
            return false;
        }

        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        if (!file.LinkExists(frame.DatasetPath) ||
            !string.Equals(
                frame.ResultHash,
                HashDoubles(file.Dataset(frame.DatasetPath).Read<double[]>()),
                StringComparison.Ordinal))
        {
            return false;
        }

        var metadataPath = DataRootLayout.GetDerivedDatasetPath(
            frame.SourceBlockNumber,
            "/metadata/reconstruction_json");
        if (!file.LinkExists(metadataPath))
        {
            return false;
        }

        var metadata = JsonSerializer.Deserialize<DerivedReconstructionMetadata>(
            file.Dataset(metadataPath).Read<string>());
        if (string.IsNullOrWhiteSpace(metadata?.MeshFingerprint) ||
            string.IsNullOrWhiteSpace(metadata.MeshArtifactPath))
        {
            return false;
        }

        _ = meshStore.Load(metadata.MeshArtifactPath, metadata.MeshFingerprint);
        EnsureRevisionMeshIdentity(frame.ExperimentRunId, frame.RevisionId, metadata.MeshFingerprint);
        return true;
    }

    private void EnsureRevisionMeshIdentity(Guid runId, string revisionId, string fingerprint)
    {
        var expected = revisionMeshFingerprints.GetOrAdd((runId, revisionId), fingerprint);
        if (!string.Equals(expected, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Offline revision mesh changed: {expected} -> {fingerprint}.");
        }
    }

    private OfflineCompleteReport CreatePublishedReport(Guid experimentRunId, string revisionId)
    {
        var revision = catalog.GetReconstructionRevision(
            experimentRunId,
            ReconstructionLane.OfflineComplete,
            revisionId)!;
        return new OfflineCompleteReport(
            experimentRunId,
            revisionId,
            revision.IsComplete,
            revision.DemodDenominator,
            revision.ReconstructedCount,
            revision.NeutralCount,
            revision.ExcludedCount,
            revision.Status);
    }

    private static OfflineFramePlan TerminalPlan(
        OfflineBlockInput input,
        int inputIndex,
        string outcome,
        string reason) =>
        new(input, inputIndex + 1, outcome, 0, false, false, null, null, null, reason);

    private static OfflineFramePlan ReconstructionPlan(
        OfflineBlockInput input,
        int inputIndex,
        int segmentNumber,
        bool resetKalman,
        double[] target,
        double[] weights,
        string policy,
        bool innovationCandidate) =>
        new(
            input,
            inputIndex + 1,
            ReconstructionFrameOutcome.Reconstructed,
            segmentNumber,
            resetKalman,
            innovationCandidate,
            target,
            weights,
            policy,
            null);

    private static double[] CombineWeights(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        if (first.Count != RealtimeReconstructionRequest.BoundaryVoltageCount ||
            second.Count != RealtimeReconstructionRequest.BoundaryVoltageCount)
        {
            throw new InvalidDataException("最终权重合并要求两组 208 点权重。");
        }

        return first.Select((value, index) => Math.Min(value, second[index])).ToArray();
    }

    private static double[]? ReadOptionalVector(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<double[]>() : null;

    private static bool TryValidateFullComplexInput(
        OfflineBlockInput input,
        out string unavailableReason)
    {
        foreach (var (values, label) in new[]
                 {
                     (input.FullAmplitude256, "full-amplitude-256"),
                     (input.FullReal256, "full-real-256"),
                     (input.FullImaginary256, "full-imaginary-256")
                 })
        {
            if (values is null)
            {
                unavailableReason = $"missing-{label}";
                return false;
            }

            if (values.Length != ElectrodeContactBaseline.FullObservationCount)
            {
                unavailableReason = $"invalid-{label}-length:{values.Length}";
                return false;
            }

            if (values.Any(value => !double.IsFinite(value)))
            {
                unavailableReason = $"nonfinite-{label}";
                return false;
            }
        }

        unavailableReason = string.Empty;
        return true;
    }

    private static double[,] UnflattenFullObservation(
        IReadOnlyList<double> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count != ElectrodeContactBaseline.FullObservationCount ||
            values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException(
                "Full electrode observation must contain 256 finite values.",
                parameterName);
        }

        var matrix = new double[ElectrodeContactBaseline.ElectrodeCount, ElectrodeContactBaseline.ElectrodeCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeContactBaseline.ElectrodeCount; stimulation++)
        {
            for (var channel = 0; channel < ElectrodeContactBaseline.ElectrodeCount; channel++)
            {
                matrix[stimulation, channel] = values[offset++];
            }
        }

        return matrix;
    }

    private static bool VectorsNearlyEqual(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => NearlyEqual(pair.First, pair.Second));

    private static bool NearlyEqual(double left, double right)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= 1.0e-10 * scale;
    }

    private static void ValidateReferenceEpochs(IReadOnlyList<ImagingReferenceEpochRecord> epochs)
    {
        if (epochs.Any(epoch =>
                epoch.LockedStartSampleIndex < 0 ||
                epoch.ReferenceAmplitude208.Length != RealtimeReconstructionRequest.BoundaryVoltageCount ||
                epoch.ReferenceAmplitude208.Any(value => !double.IsFinite(value)) ||
                epoch.ReferenceFullReal256.Length != ElectrodeContactBaseline.FullObservationCount ||
                epoch.ReferenceFullReal256.Any(value => !double.IsFinite(value)) ||
                epoch.ReferenceFullImaginary256.Length != ElectrodeContactBaseline.FullObservationCount ||
                epoch.ReferenceFullImaginary256.Any(value => !double.IsFinite(value)) ||
                epoch.NoisePrecisionWeight208 is { } persistedPrecision &&
                (persistedPrecision.Length != RealtimeReconstructionRequest.BoundaryVoltageCount ||
                 persistedPrecision.Any(weight => !double.IsFinite(weight) || weight is <= 0.0 or > 1.0))))
        {
            throw new InvalidDataException(
                "参考 epoch 缺少安全样本锚点、有限 208 点参考、有限 full-complex 256 baseline，或其可选噪声 precision 权重无效。");
        }
    }

    private static void ValidateBlockIdentity(
        IH5Group file,
        ProcessingBlockCatalogRecord block,
        string blockRoot)
    {
        var runId = Guid.Parse(file.Dataset(At(blockRoot, "/metadata/run/experiment_run_id")).Read<string>());
        var blockNumber = file.Dataset(At(blockRoot, "/metadata/run/block_number")).Read<int>();
        if (runId != block.ExperimentRunId || blockNumber != block.BlockNumber)
        {
            throw new InvalidDataException($"block {block.BlockNumber} 的 HDF5 身份与 catalog 不一致。");
        }
    }

    private static bool MatchesFinalizedInventory(
        IReadOnlyList<ProcessingBlockCatalogRecord> blocks,
        IReadOnlyList<ReconstructionDemodInputIdentity> inventory) =>
        blocks.Count == inventory.Count && blocks.Zip(inventory).All(pair =>
            pair.First.BlockNumber == pair.Second.BlockNumber &&
            pair.First.SourceStartSampleIndex == pair.Second.SourceStartSampleIndex &&
            pair.First.SourceEndSampleIndex == pair.Second.SourceEndSampleIndex);

    private static string CreatePresentationJson(
        ReconstructionPipelineManifestPayload manifest,
        string overlay,
        string? reason,
        double? scaleCenter,
        double? scaleRange,
        string[]? electrodeStates = null,
        string? contactSummary = null,
        string? contactEvidencePolicy = null,
        string? boundaryChangeAction = null)
    {
        return JsonSerializer.Serialize(new
        {
            manifest.Presentation.RendererVersion,
            manifest.Presentation.Colormap,
            Polarity = "normal",
            Gain = 1.0,
            ScaleCenter = scaleCenter,
            ScaleRange = scaleRange,
            OverlayDisposition = overlay,
            LowConfidence = false,
            Stats = reason ?? "offline-complete",
            ElectrodeStates = electrodeStates,
            ContactSummary = contactSummary,
            ContactEvidencePolicy = contactEvidencePolicy,
            BoundaryChangeAction = boundaryChangeAction
        });
    }

    private static string HashDoubles(IReadOnlyList<double> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(double))];
        for (var index = 0; index < values.Count; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(double), sizeof(double)), values[index]);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string ResolveDynamicMode(string mode) =>
        mode.StartsWith("auto", StringComparison.Ordinal) ? "fast_image" : mode;

    private static string At(string blockRoot, string path) => blockRoot.Length == 0 ? path : blockRoot + path;

    private static bool IsTerminal(ExperimentRunRecord run) => run.Status is
        ExperimentCatalog.CompletedStatus or ExperimentCatalog.InterruptedStatus or ExperimentCatalog.FailedStatus;

    private static void EnsurePathIsWithinRun(string runDirectory, string candidate)
    {
        var runRoot = Path.GetFullPath(runDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(runRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Offline revision directory escapes the experiment run directory.");
        }
    }

    private static string CreateRevisionId() =>
        $"offline-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

    private long GetAvailableBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(layout.RootPath)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static OfflineCompletePreflight UnavailablePreflight(
        Guid runId,
        string reason,
        ReconstructionPipelineInputInventory? inputs = null,
        string? fingerprint = null) =>
        new(
            runId,
            false,
            reason,
            inputs?.RawSampleRows ?? 0,
            inputs?.RawSegments.Sum(item => item.ArtifactBytes) ?? 0,
            inputs?.DemodBlockCount ?? 0,
            0,
            0,
            -1,
            null,
            fingerprint);

    private sealed record OfflineBlockInput(
        ProcessingBlockCatalogRecord Block,
        double[] Target,
        double[]? ReferenceVoltage208,
        double[]? FullAmplitude256,
        double[]? FullReal256,
        double[]? FullImaginary256,
        double[]? BaseWeights,
        string BasePolicy,
        double[]? NoisePrecisionWeight208,
        bool HighQuality,
        bool ReferenceInvalidated,
        ImagingReferenceEpochRecord? ReferenceEpoch,
        string[]? ElectrodeStates,
        string? ContactSummary,
        string ContactEvidencePolicy,
        Pseudo3dAcquisitionStamp? TimeDivision);

    private sealed record OfflineFramePlan(
        OfflineBlockInput Input,
        int SequenceNumber,
        string Outcome,
        int SegmentNumber,
        bool ResetKalman,
        bool TemporalInnovationCandidate,
        double[]? Target,
        double[]? FinalWeights,
        string? WeightPolicy,
        string? ExclusionReason);

    private sealed class OfflineDiagnosticState(
        ElectrodeContactMonitor? contactMonitor,
        EcdCwrBoundaryChangeGate? boundaryChangeGate,
        double[] noisePrecisionWeight208,
        double[]? referenceAmplitude208,
        string boundaryStatus)
    {
        public ElectrodeContactMonitor? ContactMonitor { get; } = contactMonitor;

        public EcdCwrBoundaryChangeGate? BoundaryChangeGate { get; } = boundaryChangeGate;

        public double[] NoisePrecisionWeight208 { get; } = noisePrecisionWeight208;

        public double[]? ReferenceAmplitude208 { get; } = referenceAmplitude208;

        public string BoundaryStatus { get; } = boundaryStatus;
    }

    private sealed record OfflineBoundaryState(
        EcdCwrBoundaryChangeGate? Gate,
        double[] NoisePrecisionWeight208,
        double[]? ReferenceAmplitude208,
        double[]? ReferenceFullReal256,
        double[]? ReferenceFullImaginary256,
        string Status)
    {
        public static OfflineBoundaryState Unavailable(string reason) =>
            new(
                null,
                Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray(),
                null,
                null,
                null,
                $"{OfflineBoundaryUnavailablePrefix}:{reason}");
    }

    private sealed record OfflineFramePresentationEvidence(
        string OverlayDisposition,
        string[]? ElectrodeStates,
        string? ContactSummary,
        string ContactEvidencePolicy,
        string BoundaryChangeAction,
        string? Stats);
}
