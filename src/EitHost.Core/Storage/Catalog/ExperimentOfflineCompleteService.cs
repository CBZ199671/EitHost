using System.Security.Cryptography;
using System.Text.Json;
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
    string? AlgorithmFingerprint);

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
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly IRealtimeReconstructionBackend backend;
    private readonly DerivedArtifactHdf5Writer writer;
    private readonly CanonicalExperimentReplaySource replaySource;
    private readonly EcdCwrCenteredTemporalDespiker temporalDespiker = new();

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
                fingerprint);
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
            var artifactLookup = catalog.ListDerivedArtifacts(experimentRunId)
                .GroupBy(item => (item.BlockNumber, item.Kind))
                .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First());
            var inputs = new List<OfflineBlockInput>(blocks.Length);
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputs.Add(ReadInput(block, manifest, epochs, artifactLookup));
            }

            var plans = CreatePlans(inputs, manifest);
            progress?.Report(new ExperimentCatchUpProgress(
                experimentRunId,
                ExperimentCatchUpPhase.Reconstructing,
                0,
                plans.Count));
            var completed = 0;
            var presentationScale = new RealtimeImageColorScaleTracker();
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.Outcome == ReconstructionFrameOutcome.Reconstructed)
                {
                    await ReconstructAndRecordAsync(
                        run,
                        revisionId,
                        fingerprint,
                        manifest,
                        plan,
                        presentationScale,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RecordTerminalOutcome(run, revisionId, fingerprint, plan);
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

        artifacts.TryGetValue((block.BlockNumber, "diagnostics"), out var diagnosticsArtifact);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            demodArtifact.ArtifactPath
        };
        if (diagnosticsArtifact is not null)
        {
            paths.Add(diagnosticsArtifact.ArtifactPath);
        }

        double[]? target = null;
        double[]? baseWeights = null;
        var highQuality = block.AcceptedFrameCount >= manifest.Demodulation.MinimumAcceptedFrames;
        var invalidated = false;
        var basePolicy = string.Empty;
        foreach (var relativePath in paths)
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(layout.ResolveArtifactPath(relativePath));
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

            if (file.LinkExists(At(blockRoot, "/diagnostics/measurement_weight_208")))
            {
                baseWeights = file.Dataset(At(blockRoot, "/diagnostics/measurement_weight_208")).Read<double[]>();
            }

            if (file.LinkExists(At(blockRoot, "/diagnostics/metadata_json")))
            {
                var metadata = JsonSerializer.Deserialize<DerivedFrameDiagnosticsMetadata>(
                    file.Dataset(At(blockRoot, "/diagnostics/metadata_json")).Read<string>())
                    ?? throw new InvalidDataException($"block {block.BlockNumber} 的诊断元数据无效。");
                invalidated |= metadata.ReferenceInvalidated;
                basePolicy = metadata.WeightPolicyVersion;
            }
        }

        if (target is not { Length: RealtimeReconstructionRequest.BoundaryVoltageCount })
        {
            throw new InvalidDataException($"block {block.BlockNumber} 的解调边界电压不是 208 点。");
        }

        var epoch = epochs.LastOrDefault(candidate =>
            candidate.LockedStartSampleIndex >= 0 &&
            candidate.LockedStartSampleIndex < block.SourceStartSampleIndex);
        if (epoch is not null && baseWeights is not { Length: RealtimeReconstructionRequest.BoundaryVoltageCount })
        {
            throw new InvalidDataException(
                $"block {block.BlockNumber} 缺少时序前诊断权重；禁止回退为 all-one。");
        }

        if (baseWeights is not null && baseWeights.Any(weight => !double.IsFinite(weight) || weight is < 0.0 or > 1.0))
        {
            throw new InvalidDataException($"block {block.BlockNumber} 的诊断权重越界。");
        }

        var normalizedTarget = target;
        var policy = string.IsNullOrWhiteSpace(basePolicy) ? "recorded-diagnostic-v1" : basePolicy;
        if (epoch is not null && EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(
                manifest.Reference.ScalePolicy))
        {
            normalizedTarget = EcdCwrCommonScaleNormalizer
                .NormalizeVector(epoch.ReferenceAmplitude208, target)
                .Values;
            if (!policy.Contains(EcdCwrCommonScaleNormalizer.PolicyVersion, StringComparison.Ordinal))
            {
                policy += $"+{EcdCwrCommonScaleNormalizer.PolicyVersion}";
            }
        }

        return new OfflineBlockInput(
            block,
            normalizedTarget,
            baseWeights,
            policy,
            highQuality,
            invalidated,
            epoch);
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
                    input.ReferenceEpoch!.NoisePrecisionWeight208!);
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
                inputs[index - 1].Block.SourceEndSampleIndex != input.Block.SourceStartSampleIndex;
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
                    !input.HighQuality ? "demod block is not high quality" : "reference invalidated by recorded diagnostics");
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
            input.ReferenceEpoch.ReferenceAmplitude208,
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

        var colorScale = presentationScale.Update(result.Conductivity);

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
            input.ReferenceEpoch.ReferenceAmplitude208,
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
            result.BackendElapsed.TotalMilliseconds));
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
                "conductivity",
                null,
                colorScale.Center,
                colorScale.Range),
            SourceStartSampleIndex: input.Block.SourceStartSampleIndex,
            SourceEndSampleIndex: input.Block.SourceEndSampleIndex,
            ResultHash: HashDoubles(result.Conductivity)));
    }

    private void RecordTerminalOutcome(
        ExperimentRunRecord run,
        string revisionId,
        string algorithmFingerprint,
        OfflineFramePlan plan)
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
                null),
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
        return file.LinkExists(frame.DatasetPath) &&
               string.Equals(
                   frame.ResultHash,
                   HashDoubles(file.Dataset(frame.DatasetPath).Read<double[]>()),
                   StringComparison.Ordinal);
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

    private static void ValidateReferenceEpochs(IReadOnlyList<ImagingReferenceEpochRecord> epochs)
    {
        if (epochs.Any(epoch =>
                epoch.LockedStartSampleIndex < 0 ||
                epoch.ReferenceAmplitude208.Length != RealtimeReconstructionRequest.BoundaryVoltageCount ||
                epoch.NoisePrecisionWeight208 is not { Length: RealtimeReconstructionRequest.BoundaryVoltageCount }))
        {
            throw new InvalidDataException("参考 epoch 缺少安全样本锚点、208 点参考电压或噪声 precision 权重。");
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
        double? scaleRange)
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
            Stats = reason ?? "offline-complete"
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
        double[]? BaseWeights,
        string BasePolicy,
        bool HighQuality,
        bool ReferenceInvalidated,
        ImagingReferenceEpochRecord? ReferenceEpoch);

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
}
