using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimeDerivedPersistenceController : IAsyncDisposable
{
    private const int DerivedPersistenceQueueCapacity = 128;
    private const string LiveRevisionId = "live-v1";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly DataRootLayout dataLayout;
    private readonly ExperimentCatalog catalog;
    private readonly DerivedArtifactHdf5Writer writer;
    private readonly GlobalReconstructionMeshStore meshStore;
    private readonly ExperimentBackendExchangeArchiver backendExchangeArchiver;
    private readonly Action<string> diagnostic;
    private readonly Action<string> backendArchiveWarning;
    private readonly RealtimePersistenceQueue<DerivedPersistenceWork> derivedPersistenceQueue;
    private readonly SemaphoreSlim liveCommitGate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid RunId, int BlockNumber), Task> pendingLiveCommits = new();
    private readonly ConcurrentDictionary<(Guid RunId, int BlockNumber), Task> pendingTrustedNeutralEvidence = new();

    internal RealtimeDerivedPersistenceController(
        DataRootLayout dataLayout,
        ExperimentCatalog catalog,
        DerivedArtifactHdf5Writer writer,
        ExperimentBackendExchangeArchiver backendExchangeArchiver,
        Action<string> diagnostic,
        Action<string> backendArchiveWarning)
    {
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        meshStore = new GlobalReconstructionMeshStore(this.dataLayout, this.writer);
        this.backendExchangeArchiver = backendExchangeArchiver ?? throw new ArgumentNullException(nameof(backendExchangeArchiver));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        this.backendArchiveWarning = backendArchiveWarning ?? throw new ArgumentNullException(nameof(backendArchiveWarning));
        derivedPersistenceQueue = new RealtimePersistenceQueue<DerivedPersistenceWork>(
            DerivedPersistenceQueueCapacity,
            work => work.Execute(),
            work => work.OnAbandoned?.Invoke());
    }

    internal async Task PersistDemodulatedBlockAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return;
        }

        await EnqueuePersistenceAsync(
            () => PersistDemodulatedBlockCoreAsync(config, state, block)).ConfigureAwait(false);
    }

    internal void QueueTrustedNeutralEvidence(
        RealtimeDemodulatedBlock block,
        RealtimeRunState state)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(state);
        var config = state.Config;
        if (config is null ||
            !config.PersistImagingFrames ||
            !state.ExperimentCatalogRunStarted ||
            state.ReferenceEpoch <= 0 ||
            string.IsNullOrWhiteSpace(state.ActiveReferenceLockKind))
        {
            return;
        }

        var evidence = new RealtimeRoiEvidenceCatalogRecord(
            config.ImagingRunId,
            LiveRevisionId,
            block.BlockNumber,
            CalculateBlockAcquiredAt(config, state, block),
            DateTimeOffset.UtcNow,
            RealtimeRoiEvidenceValueSource.TrustedNeutral,
            block.QualityWeight,
            state.ReferenceEpoch,
            state.ActiveReferenceLockKind,
            block.StartSampleIndex,
            block.EndSampleIndex,
            ModelRelativeValue: 1.0);
        var key = (evidence.ExperimentRunId, evidence.SourceBlockNumber);
        var task = PersistTrustedNeutralEvidenceCoreAsync(config.SetLabel, evidence);
        pendingTrustedNeutralEvidence[key] = task;
        _ = task.ContinueWith(
            (_, callbackState) =>
            {
                var tuple = ((ConcurrentDictionary<(Guid, int), Task> Dictionary, (Guid, int) Key))callbackState!;
                tuple.Dictionary.TryRemove(tuple.Key, out Task? _);
            },
            (pendingTrustedNeutralEvidence, key),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PersistTrustedNeutralEvidenceCoreAsync(
        string setLabel,
        RealtimeRoiEvidenceCatalogRecord evidence)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await EnqueuePersistenceAsync(
                () =>
                {
                    try
                    {
                        catalog.RecordRealtimeRoiEvidence(evidence);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }

                    return Task.CompletedTask;
                },
                () => completion.TrySetCanceled()).ConfigureAwait(false);
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            diagnostic(
                $"{setLabel} trusted-neutral ROI evidence failed block={evidence.SourceBlockNumber}: {ex.Message}");
        }
    }

    private async Task PersistDemodulatedBlockCoreAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return;
        }

        var acquiredAt = CalculateBlockAcquiredAt(config, state, block);
        var processedAt = DateTimeOffset.UtcNow;
        var processingRecord = CreateProcessingBlockRecord(config, block, acquiredAt, processedAt, "ready");
        try
        {
            var path = dataLayout.GetDerivedBlockPath(
                config.ImagingRunId,
                state.ExperimentStartedAt,
                block.BlockNumber);
            await Task.Run(() => writer.WriteDemodulatedBlock(
                path,
                new DerivedDemodulatedBlockData(
                    config.ImagingRunId,
                    acquiredAt,
                    processedAt,
                    block))).ConfigureAwait(false);
            catalog.RecordDemodulatedBlock(
                processingRecord,
                new DerivedArtifactCatalogRecord(
                    config.ImagingRunId,
                    block.BlockNumber,
                    "demod",
                    dataLayout.ToRelativeArtifactPath(path),
                    DataRootLayout.GetDerivedDatasetPath(block.BlockNumber, "/demod"),
                    processedAt));
            Interlocked.Increment(ref state.DemodPersistedBlocks);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref state.DemodPersistenceFailures);
            try
            {
                catalog.RecordDemodulatedBlock(
                    processingRecord with { DemodStatus = "failed", FailureMessage = ex.Message });
            }
            catch (Exception catalogError)
            {
                diagnostic($"{config.SetLabel} demod failure ledger failed block={block.BlockNumber}: {catalogError.Message}");
            }

            diagnostic($"{config.SetLabel} derived demod persistence failed block={block.BlockNumber}: {ex.Message}");
        }
    }

    internal async Task<RealtimePersistedLiveFrameEvidence?> PersistReconstructionResultAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        RealtimeReconstructionResult result,
        double? imageQualityScore,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        IReadOnlyList<double> measurementWeights,
        string weightPolicyVersion,
        RealtimeDynamicKalmanOptions? dynamicKalman)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return null;
        }

        var processedAt = DateTimeOffset.UtcNow;
        var persistenceReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveEvidence = CreateLiveEvidence(
            config,
            state,
            block,
            result,
            measurementWeights,
            dynamicKalman,
            processedAt,
            persistenceReady.Task);
        await EnqueuePersistenceAsync(
            () => PersistReconstructionResultCoreAsync(
                config,
                state,
                block,
                result,
                imageQualityScore,
                reference,
                target,
                measurementWeights,
                weightPolicyVersion,
                dynamicKalman,
                processedAt,
                liveEvidence,
                persistenceReady),
            () => persistenceReady.TrySetResult(false)).ConfigureAwait(false);
        return liveEvidence;
    }

    internal async Task<ReconstructionMeshReference> EnsureCanonicalMeshAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeReconstructionResult result,
        DateTimeOffset observedAt)
    {
        var meshReference = await Task.Run(() => meshStore.Ensure(
            config.ImagingRunId,
            observedAt,
            result.NodeCoords,
            result.CellConnectivity,
            result.Conductivity.Length,
            result.GetMeshIndexMetadata())).ConfigureAwait(false);
        if (state.CanonicalMeshFingerprint is null)
        {
            state.CanonicalMeshFingerprint = meshReference.Fingerprint;
        }
        else if (!string.Equals(
                     state.CanonicalMeshFingerprint,
                     meshReference.Fingerprint,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Realtime reconstruction mesh changed within one run: " +
                $"{state.CanonicalMeshFingerprint} -> {meshReference.Fingerprint}.");
        }

        return meshReference;
    }

    private async Task PersistReconstructionResultCoreAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        RealtimeReconstructionResult result,
        double? imageQualityScore,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        IReadOnlyList<double> measurementWeights,
        string weightPolicyVersion,
        RealtimeDynamicKalmanOptions? dynamicKalman,
        DateTimeOffset processedAt,
        RealtimePersistedLiveFrameEvidence? liveEvidence,
        TaskCompletionSource<bool> persistenceReady)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            persistenceReady.TrySetResult(false);
            return;
        }

        var acquiredAt = CalculateBlockAcquiredAt(config, state, block);
        var processingRecord = CreateProcessingBlockRecord(config, block, acquiredAt, processedAt, "ready");
        try
        {
            _ = dataLayout.EnsureDerivedDirectory(config.ImagingRunId, state.ExperimentStartedAt);
            var meshReference = await EnsureCanonicalMeshAsync(
                config,
                state,
                result,
                processedAt).ConfigureAwait(false);
            if (!string.Equals(state.CanonicalMeshFingerprint, meshReference.Fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Persisted reconstruction mesh does not match the live run mesh: {state.CanonicalMeshFingerprint} / {meshReference.Fingerprint}.");
            }

            if (Interlocked.CompareExchange(ref state.DerivedMeshPersisted, 1, 0) == 0)
            {
                try
                {
                    catalog.RegisterDerivedArtifact(new DerivedArtifactCatalogRecord(
                        config.ImagingRunId,
                        -1,
                        "mesh",
                        meshReference.ArtifactPath,
                        "/mesh",
                        processedAt));
                }
                catch
                {
                    Interlocked.Exchange(ref state.DerivedMeshPersisted, 0);
                    throw;
                }
            }

            var path = dataLayout.GetDerivedBlockPath(
                config.ImagingRunId,
                state.ExperimentStartedAt,
                block.BlockNumber);
            await Task.Run(() => writer.WriteReconstruction(
                path,
                new DerivedReconstructionData(
                    config.ImagingRunId,
                    block.BlockNumber,
                    block.StartSampleIndex,
                    block.EndSampleIndex,
                    acquiredAt,
                    processedAt,
                    result.Conductivity,
                    result.RawConductivity,
                    imageQualityScore,
                    result.WeightedSystemConditionNumber,
                    ReferenceEpoch: state.ReferenceEpoch > 0 ? state.ReferenceEpoch : null,
                    WeightPolicyVersion: weightPolicyVersion,
                    ReferenceVoltage208: reference.ToArray(),
                    TargetVoltage208: target.ToArray(),
                    MeasurementWeight208: measurementWeights.ToArray(),
                    DynamicKalmanSessionId: result.DynamicKalmanApplied ? dynamicKalman?.SessionId : null,
                    DynamicKalmanAction: result.DynamicKalmanAction,
                    DynamicKalmanNisPerDof: result.DynamicKalmanNisPerDof,
                    DynamicKalmanGainMean: result.DynamicKalmanGainMean,
                    DynamicKalmanVarianceInflation: result.DynamicKalmanVarianceInflation,
                    DynamicKalmanUpdateCount: result.DynamicKalmanUpdateCount,
                    DynamicKalmanTotalLatencyFrames: result.DynamicKalmanTotalLatencyFrames,
                    DynamicKalmanMode: result.DynamicKalmanMode,
                    DynamicKalmanFallback: result.DynamicKalmanFallback,
                    DynamicKalmanSolveMilliseconds: result.DynamicKalmanSolveMilliseconds,
                    ReconstructionBackendElapsedMilliseconds: result.BackendElapsed.TotalMilliseconds,
                    MeshFingerprint: meshReference.Fingerprint,
                    MeshArtifactPath: meshReference.ArtifactPath,
                    MeshIndexSchema: result.MeshIndexSchema,
                    ParameterEntity: result.ParameterEntity,
                    LogicalMeshFingerprint: result.LogicalMeshFingerprint,
                    OrderedIndexFingerprint: result.OrderedIndexFingerprint))).ConfigureAwait(false);
            catalog.RecordReconstructionOutcome(
                processingRecord,
                "ready",
                processedAt,
                new DerivedArtifactCatalogRecord(
                    config.ImagingRunId,
                    block.BlockNumber,
                    "reconstruction",
                    dataLayout.ToRelativeArtifactPath(path),
                    DataRootLayout.GetDerivedDatasetPath(block.BlockNumber, "/reconstruction"),
                    processedAt));
            if (liveEvidence is not null)
            {
                EnsureLiveRevision(liveEvidence);
            }

            Interlocked.Increment(ref state.ReconstructionPersistedBlocks);
            await ArchiveBackendExchangeDiagnosticAsync(config, block, result).ConfigureAwait(false);
            persistenceReady.TrySetResult(true);
        }
        catch (Exception ex)
        {
            persistenceReady.TrySetResult(false);
            Interlocked.Increment(ref state.ReconstructionPersistenceFailures);
            await RecordReconstructionFailureAsync(
                config,
                state,
                block,
                $"derived persistence failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    internal Task CommitLivePresentationAsync(RealtimeLiveFrameCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var key = (commit.Frame.ExperimentRunId, commit.Frame.SourceBlockNumber);
        var task = CommitLivePresentationCoreAsync(commit);
        pendingLiveCommits[key] = task;
        _ = task.ContinueWith(
            (_, state) =>
            {
                var tuple = ((ConcurrentDictionary<(Guid, int), Task> Dictionary, (Guid, int) Key))state!;
                tuple.Dictionary.TryRemove(tuple.Key, out Task? _);
            },
            (pendingLiveCommits, key),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    internal async Task PublishLiveRevisionAsync(Guid experimentRunId, long rawDenominator)
    {
        var neutralEvidence = pendingTrustedNeutralEvidence
            .Where(item => item.Key.RunId == experimentRunId)
            .Select(item => item.Value)
            .ToArray();
        var commits = pendingLiveCommits
            .Where(item => item.Key.RunId == experimentRunId)
            .Select(item => item.Value)
            .ToArray();
        if (neutralEvidence.Length > 0 || commits.Length > 0)
        {
            await Task.WhenAll(neutralEvidence.Concat(commits)).ConfigureAwait(false);
        }

        await liveCommitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var revision = catalog.GetReconstructionRevision(
                experimentRunId,
                ReconstructionLane.Live,
                LiveRevisionId);
            if (revision is null || revision.IsPublished)
            {
                return;
            }

            var frames = catalog.ListReconstructionLaneFrames(
                experimentRunId,
                ReconstructionLane.Live,
                LiveRevisionId);
            catalog.PublishReconstructionRevision(
                experimentRunId,
                ReconstructionLane.Live,
                LiveRevisionId,
                Math.Max(0, rawDenominator),
                frames.Count,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            liveCommitGate.Release();
        }
    }

    private async Task CommitLivePresentationCoreAsync(RealtimeLiveFrameCommit commit)
    {
        if (!await commit.Frame.PersistenceReady.ConfigureAwait(false))
        {
            diagnostic(
                $"{commit.Frame.SetLabel} live replay excluded block={commit.Frame.SourceBlockNumber}: reconstruction artifact was not persisted");
            return;
        }

        await liveCommitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (catalog.GetReconstructionLaneFrame(
                    commit.Frame.ExperimentRunId,
                    ReconstructionLane.Live,
                    commit.Frame.RevisionId,
                    commit.Frame.SourceBlockNumber) is not null)
            {
                return;
            }

            var existing = catalog.ListReconstructionLaneFrames(
                commit.Frame.ExperimentRunId,
                ReconstructionLane.Live,
                commit.Frame.RevisionId);
            var sequence = existing.Count == 0 ? 1 : existing.Max(item => item.SequenceNumber) + 1;
            catalog.RecordReconstructionLaneFrame(new ReconstructionLaneFrameCatalogRecord(
                commit.Frame.ExperimentRunId,
                ReconstructionLane.Live,
                commit.Frame.RevisionId,
                commit.Frame.SourceBlockNumber,
                sequence,
                string.Equals(commit.Presentation.OverlayDisposition, "neutral", StringComparison.Ordinal)
                    ? ReconstructionFrameOutcome.Neutral
                    : ReconstructionFrameOutcome.Reconstructed,
                commit.Frame.AcquiredAt,
                DateTimeOffset.UtcNow,
                commit.Frame.AlgorithmFingerprint,
                commit.Frame.ArtifactPath,
                commit.Frame.DatasetPath,
                commit.Frame.FinalWeightHash,
                commit.Frame.KalmanSessionId,
                commit.Frame.KalmanDisposition,
                JsonSerializer.Serialize(commit.Presentation, EvidenceJsonOptions),
                SourceStartSampleIndex: commit.Frame.SourceStartSampleIndex,
                SourceEndSampleIndex: commit.Frame.SourceEndSampleIndex,
                ResultHash: commit.Frame.ResultHash));
        }
        catch (Exception ex)
        {
            diagnostic(
                $"{commit.Frame.SetLabel} live replay index failed block={commit.Frame.SourceBlockNumber}: {ex.Message}");
        }
        finally
        {
            liveCommitGate.Release();
        }
    }

    private void EnsureLiveRevision(RealtimePersistedLiveFrameEvidence evidence)
    {
        var existing = catalog.GetReconstructionRevision(
            evidence.ExperimentRunId,
            ReconstructionLane.Live,
            evidence.RevisionId);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.AlgorithmFingerprint,
                    evidence.AlgorithmFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Live reconstruction algorithm fingerprint changed within one run.");
            }

            return;
        }

        catalog.UpsertReconstructionRevision(new ReconstructionRevisionCatalogRecord(
            evidence.ExperimentRunId,
            ReconstructionLane.Live,
            evidence.RevisionId,
            ReconstructionRevisionStatus.Staged,
            evidence.AlgorithmFingerprint,
            RawDenominator: 0,
            DemodDenominator: 0,
            TerminalOutcomeCount: 0,
            ReconstructedCount: 0,
            NeutralCount: 0,
            ExcludedCount: 0,
            EstimatedIncrementalBytes: 0,
            evidence.ProcessedAt,
            evidence.ProcessedAt));
    }

    private RealtimePersistedLiveFrameEvidence? CreateLiveEvidence(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        RealtimeReconstructionResult result,
        IReadOnlyList<double> measurementWeights,
        RealtimeDynamicKalmanOptions? dynamicKalman,
        DateTimeOffset processedAt,
        Task<bool> persistenceReady)
    {
        if (!config.EnableDynamicKalman ||
            !result.DynamicKalmanApplied ||
            result.DynamicKalmanFallback == true ||
            !string.Equals(result.DynamicKalmanAction, "update", StringComparison.Ordinal) ||
            dynamicKalman is null ||
            string.IsNullOrWhiteSpace(dynamicKalman.SessionId))
        {
            return null;
        }

        var path = dataLayout.GetDerivedBlockPath(
            config.ImagingRunId,
            state.ExperimentStartedAt,
            block.BlockNumber);
        var manifest = catalog.GetPipelineManifest(config.ImagingRunId);
        if (manifest is null)
        {
            diagnostic($"{config.SetLabel} live replay excluded block={block.BlockNumber}: pipeline manifest missing");
            return null;
        }

        try
        {
            _ = ReconstructionPipelineManifestCodec.ReadPayload(manifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            diagnostic($"{config.SetLabel} live replay excluded block={block.BlockNumber}: {ex.Message}");
            return null;
        }

        return new RealtimePersistedLiveFrameEvidence(
            config.SetLabel,
            config.ImagingRunId,
            LiveRevisionId,
            block.BlockNumber,
            block.StartSampleIndex,
            block.EndSampleIndex,
            CalculateBlockAcquiredAt(config, state, block),
            processedAt,
            manifest.AlgorithmFingerprint,
            dataLayout.ToRelativeArtifactPath(path),
            DataRootLayout.GetDerivedDatasetPath(block.BlockNumber, "/reconstruction/conductivity"),
            HashDoubles(measurementWeights),
            HashDoubles(result.Conductivity),
            dynamicKalman.SessionId,
            "updated",
            state.ReferenceEpoch,
            persistenceReady);
    }

    private static string HashDoubles(IReadOnlyList<double> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(value));
            hash.AppendData(buffer);
        }

        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal async Task PersistFrameDiagnosticsAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        ImagingFrameRecord frame,
        bool persistReplayDemodOverride)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return;
        }

        await EnqueuePersistenceAsync(
            () => PersistFrameDiagnosticsCoreAsync(
                config,
                state,
                block,
                frame,
                persistReplayDemodOverride)).ConfigureAwait(false);
    }

    private async Task PersistFrameDiagnosticsCoreAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        ImagingFrameRecord frame,
        bool persistReplayDemodOverride)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return;
        }

        var processedAt = DateTimeOffset.UtcNow;
        var acquiredAt = CalculateBlockAcquiredAt(config, state, block);
        try
        {
            var path = dataLayout.GetDerivedBlockPath(
                config.ImagingRunId,
                state.ExperimentStartedAt,
                block.BlockNumber);
            await Task.Run(() => writer.WriteFrameDiagnostics(
                path,
                new DerivedFrameDiagnosticsData(
                    config.ImagingRunId,
                    block.StartSampleIndex,
                    block.EndSampleIndex,
                    acquiredAt,
                    processedAt,
                    frame,
                    persistReplayDemodOverride))).ConfigureAwait(false);
            catalog.RegisterDerivedArtifact(new DerivedArtifactCatalogRecord(
                config.ImagingRunId,
                block.BlockNumber,
                "diagnostics",
                dataLayout.ToRelativeArtifactPath(path),
                DataRootLayout.GetDerivedDatasetPath(block.BlockNumber, "/diagnostics"),
                processedAt));
        }
        catch (Exception ex)
        {
            diagnostic($"{config.SetLabel} derived diagnostics persistence failed block={block.BlockNumber}: {ex.Message}");
        }
    }

    internal Task RecordReconstructionFailureAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        string error)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return Task.CompletedTask;
        }

        try
        {
            var processedAt = DateTimeOffset.UtcNow;
            var acquiredAt = CalculateBlockAcquiredAt(config, state, block);
            catalog.RecordReconstructionOutcome(
                CreateProcessingBlockRecord(config, block, acquiredAt, processedAt, "ready"),
                "failed",
                processedAt,
                failureMessage: error);
        }
        catch (Exception ex)
        {
            diagnostic($"{config.SetLabel} reconstruction failure ledger failed block={block.BlockNumber}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    internal async Task PersistReferenceCandidatesAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        IReadOnlyList<ImagingReferenceCandidateRecord> candidates)
    {
        if (candidates.Count == 0 || !state.ExperimentCatalogRunStarted)
        {
            return;
        }

        await EnqueuePersistenceAsync(
            () => PersistReferenceCandidatesCoreAsync(config, state, block, candidates)).ConfigureAwait(false);
    }

    private Task PersistReferenceCandidatesCoreAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        IReadOnlyList<ImagingReferenceCandidateRecord> candidates)
    {
        if (candidates.Count == 0 || !state.ExperimentCatalogRunStarted)
        {
            return Task.CompletedTask;
        }

        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var acquiredAt = CalculateBlockAcquiredAt(config, state, block);
            var path = dataLayout.GetDerivedBlockPath(
                config.ImagingRunId,
                state.ExperimentStartedAt,
                block.BlockNumber);
            writer.WriteReferenceCandidates(
                path,
                new DerivedReferenceCandidateBlockData(
                    config.ImagingRunId,
                    block.BlockNumber,
                    block.StartSampleIndex,
                    block.EndSampleIndex,
                    acquiredAt,
                    createdAt,
                    candidates));
            catalog.RegisterDerivedArtifact(new DerivedArtifactCatalogRecord(
                config.ImagingRunId,
                block.BlockNumber,
                "reference_candidates",
                dataLayout.ToRelativeArtifactPath(path),
                DataRootLayout.GetDerivedDatasetPath(block.BlockNumber, "/candidates"),
                createdAt));
        }
        catch (Exception ex)
        {
            diagnostic($"{config.SetLabel} reference candidate persistence failed block={block.BlockNumber}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    internal async Task DrainAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueuePersistenceAsync(
            () =>
            {
                drained.TrySetResult();
                return Task.CompletedTask;
            },
            () => drained.TrySetException(
                new IOException("Derived persistence queue failed before the drain barrier."))).ConfigureAwait(false);
        await drained.Task.ConfigureAwait(false);
        derivedPersistenceQueue.ThrowIfFaulted();
    }

    public async ValueTask DisposeAsync()
    {
        await derivedPersistenceQueue.DisposeAsync().ConfigureAwait(false);
        liveCommitGate.Dispose();
    }

    internal void PersistReferenceEpoch(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        EcdCwrRobustReference reference)
    {
        if (!state.ExperimentCatalogRunStarted ||
            state.ReferenceEpoch <= 0 ||
            state.ReferenceStartSampleIndex < 0 ||
            state.ReferenceDemodulation is null)
        {
            return;
        }

        var epoch = new ImagingReferenceEpochRecord(
            config.ImagingRunId,
            state.ReferenceEpoch,
            state.ReferenceBlockNumber,
            state.ReferenceLockedAt,
            reference.FrameCount,
            reference.RejectedFrameCount,
            reference.Voltage208.ToArray(),
            reference.FullReal256.ToArray(),
            reference.FullImaginary256.ToArray(),
            reference.NoiseModel?.GlobalScoreThreshold,
            state.ReferenceDemodulation.EstimatedWindowSamples,
            state.ReferenceDemodulation.UniformOffsetSamples,
            state.ReferenceDemodulation.RotationStartChannelOneBased,
            state.ReferenceDemodulation.RotationDirection,
            config.DacSettings.ActualFrequencyHz,
            config.DacSettings.Gain,
            config.PgaGain,
            state.ActiveReferenceLockKind,
            reference.CommonScaleNormalized,
            reference.CommonScaleNormalizationPolicy,
            reference.MedianInputCommonScale,
            ReferenceScalePolicy: config.ReferenceScalePolicy,
            SourceCandidateIds: state.ActiveReferenceWindow?.SourceCandidateIds.ToArray(),
            SelectedWindowStartedAt: state.ActiveReferenceWindow?.StartedAt,
            SelectedWindowEndedAt: state.ActiveReferenceWindow?.EndedAt,
            EffectiveReferenceAt: state.ActiveReferenceWindow?.EffectiveReferenceAt,
            SelectedWindowDriftPerMinute: state.ActiveReferenceWindow?.DriftPerMinute,
            SelectedWindowGapCount: state.ActiveReferenceWindow?.GapCount ?? 0,
            SelectedWindowSaturationCount: state.ActiveReferenceWindow?.SaturationCount ?? 0,
            SelectedWindowContactEvidence: state.ActiveReferenceWindow?.ContactEvidence,
            NoiseEstimationPolicy: reference.NoiseModel?.NoiseEstimationPolicy ?? "unavailable",
            ActionGroupId: state.ActiveReferenceActionGroupId,
            CommonActionAt: state.ActiveReferenceCommonActionAt,
            WindowSkewMilliseconds: state.ActiveReferenceWindowSkewMilliseconds,
            SwitchSkewMilliseconds: state.ActiveReferenceSwitchSkewMilliseconds,
            SynchronizedSetCount: state.ActiveReferenceSynchronizedSetCount,
            LockedStartSampleIndex: state.ReferenceStartSampleIndex,
            NoisePrecisionWeight208: reference.NoiseModel?.PrecisionWeight208.ToArray());
        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var directory = dataLayout.EnsureDerivedDirectory(config.ImagingRunId, state.ExperimentStartedAt);
            var path = Path.Combine(directory, $"reference_{epoch.ReferenceEpoch:D4}.h5");
            writer.WriteReferenceEpoch(path, epoch);
            catalog.RegisterReferenceEpoch(new ExperimentReferenceEpochCatalogRecord(
                config.ImagingRunId,
                epoch.ReferenceEpoch,
                epoch.LockedBlockNumber,
                epoch.LockedAt,
                epoch.LockKind,
                dataLayout.ToRelativeArtifactPath(path),
                "/reference",
                createdAt,
                state.ReferenceStartSampleIndex));
        }
        catch (Exception ex)
        {
            diagnostic($"{config.SetLabel} reference epoch persistence failed e{epoch.ReferenceEpoch}: {ex.Message}");
        }
    }

    internal static DateTimeOffset CalculateBlockAcquiredAt(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        var startedAt = state.ExperimentStartedAt == default
            ? DateTimeOffset.UtcNow
            : state.ExperimentStartedAt;
        return state.CalculateAcquiredAt(
            startedAt,
            block.StartSampleIndex,
            config.AcquisitionSettings.SampleRateHz);
    }

    private async Task ArchiveBackendExchangeDiagnosticAsync(
        RealtimeImagingRunConfig config,
        RealtimeDemodulatedBlock block,
        RealtimeReconstructionResult result)
    {
        if (!config.PersistReconstructionResults || !result.OutputPersisted)
        {
            return;
        }

        try
        {
            var run = catalog.GetRun(config.ImagingRunId)
                ?? throw new InvalidOperationException(
                    $"Experiment run {config.ImagingRunId:D} is missing from the canonical catalog.");
            var archivedPath = await Task.Run(() => backendExchangeArchiver.Archive(
                    config.ImagingRunId,
                    run.RunDirectory,
                    block.BlockNumber,
                    result.OutputHdf5Path,
                    result.CompletedAt))
                .ConfigureAwait(false);
            diagnostic($"{config.SetLabel} backend diagnostic archived block={block.BlockNumber}: {archivedPath}");
        }
        catch (Exception ex)
        {
            backendArchiveWarning(
                $"{config.SetLabel} 后端诊断归档失败（规范重构结果已保存）：{ex.Message}");
        }
    }

    private static ProcessingBlockCatalogRecord CreateProcessingBlockRecord(
        RealtimeImagingRunConfig config,
        RealtimeDemodulatedBlock block,
        DateTimeOffset acquiredAt,
        DateTimeOffset processedAt,
        string demodStatus) =>
        new(
            config.ImagingRunId,
            block.BlockNumber,
            block.StartSampleIndex,
            block.EndSampleIndex,
            acquiredAt,
            processedAt,
            demodStatus,
            QualityWeight: block.QualityWeight,
            AcceptedFrameCount: block.AcceptedFrameCount,
            RejectedFrameCount: block.RejectedFrameCount);

    private async Task EnqueuePersistenceAsync(Func<Task> work, Action? onAbandoned = null)
    {
        var item = new DerivedPersistenceWork(work, onAbandoned);
        if (!derivedPersistenceQueue.TryEnqueue(item))
        {
            await derivedPersistenceQueue.EnqueueAsync(item).ConfigureAwait(false);
        }
    }

    private sealed record DerivedPersistenceWork(Func<Task> Execute, Action? OnAbandoned);
}
