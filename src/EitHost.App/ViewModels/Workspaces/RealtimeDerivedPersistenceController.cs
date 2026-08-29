using System.IO;
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
    private readonly DataRootLayout dataLayout;
    private readonly ExperimentCatalog catalog;
    private readonly DerivedArtifactHdf5Writer writer;
    private readonly ExperimentBackendExchangeArchiver backendExchangeArchiver;
    private readonly Action<string> diagnostic;
    private readonly Action<string> backendArchiveWarning;
    private readonly RealtimePersistenceQueue<DerivedPersistenceWork> derivedPersistenceQueue;

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

    internal async Task PersistReconstructionResultAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        RealtimeReconstructionResult result,
        double? imageQualityScore,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        IReadOnlyList<double> measurementWeights,
        string weightPolicyVersion,
        string? dynamicKalmanSessionId)
    {
        if (!config.PersistImagingFrames || !state.ExperimentCatalogRunStarted)
        {
            return;
        }

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
                dynamicKalmanSessionId)).ConfigureAwait(false);
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
        string? dynamicKalmanSessionId)
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
            var derivedDirectory = dataLayout.EnsureDerivedDirectory(config.ImagingRunId, state.ExperimentStartedAt);
            if (Interlocked.CompareExchange(ref state.DerivedMeshPersisted, 1, 0) == 0)
            {
                try
                {
                    var meshPath = Path.Combine(derivedDirectory, "mesh.h5");
                    await Task.Run(() => writer.WriteMesh(
                        meshPath,
                        new DerivedMeshData(
                            config.ImagingRunId,
                            processedAt,
                            result.NodeCoords,
                            result.CellConnectivity))).ConfigureAwait(false);
                    catalog.RegisterDerivedArtifact(new DerivedArtifactCatalogRecord(
                        config.ImagingRunId,
                        -1,
                        "mesh",
                        dataLayout.ToRelativeArtifactPath(meshPath),
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
                    DynamicKalmanSessionId: dynamicKalmanSessionId,
                    DynamicKalmanAction: result.DynamicKalmanAction,
                    DynamicKalmanNisPerDof: result.DynamicKalmanNisPerDof,
                    DynamicKalmanGainMean: result.DynamicKalmanGainMean,
                    DynamicKalmanVarianceInflation: result.DynamicKalmanVarianceInflation,
                    DynamicKalmanUpdateCount: result.DynamicKalmanUpdateCount,
                    DynamicKalmanTotalLatencyFrames: result.DynamicKalmanTotalLatencyFrames,
                    DynamicKalmanMode: result.DynamicKalmanMode,
                    DynamicKalmanFallback: result.DynamicKalmanFallback,
                    DynamicKalmanSolveMilliseconds: result.DynamicKalmanSolveMilliseconds,
                    ReconstructionBackendElapsedMilliseconds: result.BackendElapsed.TotalMilliseconds))).ConfigureAwait(false);
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
            Interlocked.Increment(ref state.ReconstructionPersistedBlocks);
            await ArchiveBackendExchangeDiagnosticAsync(config, block, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref state.ReconstructionPersistenceFailures);
            await RecordReconstructionFailureAsync(
                config,
                state,
                block,
                $"derived persistence failed: {ex.Message}").ConfigureAwait(false);
        }
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

    public ValueTask DisposeAsync() => derivedPersistenceQueue.DisposeAsync();

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
            LockedStartSampleIndex: state.ReferenceStartSampleIndex);
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
