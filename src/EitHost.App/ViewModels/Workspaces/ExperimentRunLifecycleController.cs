using System.Text.Encodings.Web;
using System.Text.Json;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record ExperimentRunLifecycleCallbacks(
    Action<string> Diagnostic,
    Action<Guid, string> BeginDiagnosticMirror,
    Action<Guid> EndDiagnosticMirror,
    Action<Guid, string> RunDiagnostic,
    Action RefreshRuns,
    Action<string> PublishStatus,
    Action<string> PublishCatchUpProgress);

internal sealed class ExperimentRunLifecycleController
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly DataRootLayout dataLayout;
    private readonly ExperimentCatalog experimentCatalog;
    private readonly ExperimentDemodCatchUpService demodCatchUpService;
    private readonly ExperimentOfflineCompleteService offlineCompleteService;
    private readonly ExperimentRunOperationGate operationGate;
    private readonly Guid sessionId;
    private readonly ExperimentRunLifecycleCallbacks callbacks;
    private readonly object catchUpGate = new();
    private readonly HashSet<Guid> queuedCatchUpRuns = [];
    private readonly SemaphoreSlim offlineCompleteGate = new(1, 1);
    private CancellationTokenSource? catchUpCancellation;

    internal bool IsCatchUpRunning
    {
        get
        {
            lock (catchUpGate)
            {
                return queuedCatchUpRuns.Count > 0;
            }
        }
    }

    /// <summary>
    /// Requests that the manual offline-complete job stops after its current block. Its staged
    /// revision remains resumable and is never exposed as a published replay lane.
    /// </summary>
    internal void CancelCatchUp()
    {
        CancellationTokenSource? active;
        lock (catchUpGate)
        {
            active = catchUpCancellation;
        }

        active?.Cancel();
    }

    internal ExperimentRunLifecycleController(
        DataRootLayout dataLayout,
        ExperimentCatalog experimentCatalog,
        ExperimentDemodCatchUpService demodCatchUpService,
        ExperimentOfflineCompleteService offlineCompleteService,
        ExperimentRunOperationGate operationGate,
        Guid sessionId,
        ExperimentRunLifecycleCallbacks callbacks)
    {
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        this.experimentCatalog = experimentCatalog ?? throw new ArgumentNullException(nameof(experimentCatalog));
        this.demodCatchUpService = demodCatchUpService ?? throw new ArgumentNullException(nameof(demodCatchUpService));
        this.offlineCompleteService = offlineCompleteService ?? throw new ArgumentNullException(nameof(offlineCompleteService));
        this.operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
        this.sessionId = sessionId;
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void RegisterConfig(RealtimeImagingRunConfig config, RealtimeRunState state)
    {
        if (!config.PersistImagingFrames || state.ContactOperatingFingerprint is null)
        {
            return;
        }

        var match = state.AdaptiveContactProfileMatch;
        var runConfig = new ExperimentRunConfigRecord(
            config.ImagingRunId,
            config.ReconstructionRoute,
            config.DifferenceLambda,
            config.CustomLambdaEnabled,
            config.MeshSize,
            config.DacSettings.ActualFrequencyHz,
            state.ExecutionReceipt?.CalculateEffectiveChannelCycles(config.DacSettings.ActualFrequencyHz)
                ?? config.ExcitationSettings.ChannelCycles,
            config.AcquisitionSettings.SampleRateHz,
            config.DifferenceOrientation,
            ReconstructionScale.ModelRelative,
            EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(config.ReferenceScalePolicy)
                ? ReconstructionScale.CommonScaleNormalizedRelativeProvenance
                : ReconstructionScale.NormalizedModelProvenance,
            config.ReferenceScalePolicy,
            JsonSerializer.Serialize(state.ContactOperatingFingerprint, JsonOptions),
            match?.Profile?.ProfileId,
            RealtimeContactDiagnosticController.CreateAdaptiveContactThresholdMode(match),
            config.DacSettings.FrequencyHz,
            config.DacSettings.ActualFrequencyHz,
            config.DacSettings.FrequencyTuningWord,
            state.ExecutionReceipt?.RequestedTimeUs,
            state.ExecutionReceipt?.EffectiveTimeUs,
            (int)config.AcquisitionSettings.Range,
            Usb2070VoltageScale.GetFullSpanVolts(config.AcquisitionSettings.Range),
            Usb2070VoltageScale.GetLsbVolts(config.AcquisitionSettings.Range));
        experimentCatalog.SaveRunConfig(runConfig);
        experimentCatalog.SavePipelineManifest(ReconstructionPipelineManifestFactory.CreateRecording(
            config,
            state,
            runConfig,
            DateTimeOffset.UtcNow));
    }

    internal void BeginRun(RealtimeImagingRunConfig config, RealtimeRunState state)
    {
        if (!config.PersistImagingFrames && !config.PersistRawAcquisitionHdf5)
        {
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var runDirectory = dataLayout.GetRunRelativeDirectory(config.ImagingRunId, startedAt);
        dataLayout.EnsureRunDirectory(config.ImagingRunId, startedAt);
        experimentCatalog.BeginRun(ExperimentRunRecord.CreateRecording(
            config.ImagingRunId,
            sessionId,
            config.SetLabel,
            startedAt,
            config.StoragePolicy.Value,
            runDirectory));
        state.ExperimentCatalogRunStarted = true;
        state.ExperimentStartedAt = startedAt;
        callbacks.BeginDiagnosticMirror(config.ImagingRunId, runDirectory);
        callbacks.Diagnostic(
            $"{config.SetLabel} experiment begin id={config.ImagingRunId:D} directory={runDirectory}");
    }

    internal void CompleteRun(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        Exception? failure)
    {
        if (!state.ExperimentCatalogRunStarted)
        {
            return;
        }

        try
        {
            var coverage = experimentCatalog.GetCoverage(config.ImagingRunId);
            var rawStatus = !config.PersistRawAcquisitionHdf5
                ? "not_requested"
                : failure is not null
                    ? "incomplete"
                    : state.TotalRawSamples == 0
                        ? "empty"
                        : "complete";
            var demodStatus = !config.PersistImagingFrames
                ? "not_requested"
                : coverage.DemodFailedCount > 0
                    ? "incomplete"
                    : coverage.DemodReadyCount == state.BlocksProcessed
                        ? "complete"
                        : "pending";
            var reconstructionStatus = !config.PersistImagingFrames
                ? "not_requested"
                : coverage.ReconstructionFailedCount > 0
                    ? "incomplete"
                    : coverage.ReconstructionReadyCount > 0 && coverage.ReconstructionPendingCount == 0
                        ? "complete"
                        : coverage.ReconstructionReadyCount > 0
                            ? "partial"
                            : "pending";
            experimentCatalog.SetRunStageStatuses(
                config.ImagingRunId,
                rawStatus,
                demodStatus,
                reconstructionStatus);
            FinalizePipelineManifest(config);
            experimentCatalog.EndRun(
                config.ImagingRunId,
                DateTimeOffset.UtcNow,
                failure is null ? ExperimentCatalog.CompletedStatus : ExperimentCatalog.FailedStatus,
                failure?.Message);
            state.ExperimentCatalogRunStarted = false;
            callbacks.Diagnostic(
                $"{config.SetLabel} experiment end id={config.ImagingRunId:D} " +
                $"status={(failure is null ? ExperimentCatalog.CompletedStatus : ExperimentCatalog.FailedStatus)} " +
                $"raw={rawStatus} demod={demodStatus} reconstruction={reconstructionStatus}");
            callbacks.RefreshRuns();
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic(
                $"{config.SetLabel} experiment end catalog failed id={config.ImagingRunId:D}: {ex.Message}");
        }
        finally
        {
            callbacks.EndDiagnosticMirror(config.ImagingRunId);
        }
    }

    private void FinalizePipelineManifest(RealtimeImagingRunConfig config)
    {
        try
        {
            var recording = experimentCatalog.GetPipelineManifest(config.ImagingRunId);
            if (recording is null)
            {
                return;
            }

            var finalized = ReconstructionPipelineManifestFactory.Finalize(
                recording,
                experimentCatalog,
                dataLayout,
                DateTimeOffset.UtcNow);
            experimentCatalog.SavePipelineManifest(finalized);
            callbacks.Diagnostic(
                $"{config.SetLabel} pipeline manifest {finalized.Status} fingerprint={finalized.AlgorithmFingerprint}" +
                (string.IsNullOrWhiteSpace(finalized.UnavailableReason)
                    ? string.Empty
                    : $" reason={finalized.UnavailableReason}"));
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic(
                $"{config.SetLabel} pipeline manifest finalization failed: {ex.Message}; offline-complete disabled");
        }
    }

    internal void QueueCatchUp(Guid experimentRunId, string setLabel, string reason)
    {
        IDisposable operationLease;
        lock (catchUpGate)
        {
            if (!queuedCatchUpRuns.Add(experimentRunId))
            {
                return;
            }

            try
            {
                operationLease = operationGate.Enter(
                    experimentRunId,
                    ExperimentRunOperation.OfflineCatchUp);
            }
            catch (ExperimentRunOperationConflictException ex)
            {
                queuedCatchUpRuns.Remove(experimentRunId);
                callbacks.PublishStatus(
                    $"{setLabel} 离线追赶暂未启动：实验正在执行 {ex.ActiveOperation} 操作。");
                return;
            }
        }

        _ = RunCatchUpAsync(experimentRunId, setLabel, reason, operationLease);
    }

    internal OfflineCompletePreflight PreflightOfflineComplete(Guid experimentRunId) =>
        offlineCompleteService.Preflight(experimentRunId);

    private async Task RunCatchUpAsync(
        Guid experimentRunId,
        string setLabel,
        string reason,
        IDisposable operationLease)
    {
        using var cancellation = new CancellationTokenSource();
        lock (catchUpGate)
        {
            catchUpCancellation = cancellation;
        }

        var progress = new Progress<ExperimentCatchUpProgress>(
            update => callbacks.PublishCatchUpProgress(DescribeProgress(setLabel, update)));
        try
        {
            callbacks.PublishCatchUpProgress($"{setLabel} 离线完整重算：正在补齐原始解调…");
            var report = await Task
                .Run(
                    () => demodCatchUpService.Run(experimentRunId, progress, cancellation.Token),
                    cancellation.Token)
                .ConfigureAwait(false);
            RefreshPipelineManifestInputs(experimentRunId, setLabel);
            var preflight = offlineCompleteService.Preflight(experimentRunId);
            if (!preflight.CanStart)
            {
                callbacks.RunDiagnostic(
                    experimentRunId,
                    $"{setLabel} offline-complete unavailable {reason} run={experimentRunId:D}: {preflight.Reason}");
                callbacks.RefreshRuns();
                callbacks.PublishStatus($"{setLabel} 无法等价生成离线完整回放：{preflight.Reason}");
                return;
            }

            OfflineCompleteReport reconstructionReport;
            await offlineCompleteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                reconstructionReport = await offlineCompleteService
                    .RunAsync(experimentRunId, progress, cancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                offlineCompleteGate.Release();
            }

            callbacks.RunDiagnostic(
                experimentRunId,
                $"{setLabel} catch-up {reason} run={experimentRunId:D} recovered={report.RecoveredBlockCount} " +
                $"skipped={report.SkippedBlockCount} discardedRows={report.DiscardedRawRows} " +
                $"pendingRows={report.PendingRawRows} failures={report.FailedBlockCount} " +
                $"missingSegments={report.MissingSegmentCount} status={report.DemodStatus}; " +
                $"offlineRevision={reconstructionReport.RevisionId} published={reconstructionReport.Published} " +
                $"reconstructed={reconstructionReport.ReconstructedCount} neutral={reconstructionReport.NeutralCount} " +
                $"excluded={reconstructionReport.ExcludedCount} status={reconstructionReport.Status}");
            callbacks.RefreshRuns();
            callbacks.PublishStatus(
                report.PendingRawRows == 0 &&
                report.FailedBlockCount == 0 &&
                reconstructionReport.Published
                    ? $"{setLabel} 离线完整版本已发布：解调补齐 {report.RecoveredBlockCount} 块；" +
                      $"重构 {reconstructionReport.ReconstructedCount} 帧，中性 {reconstructionReport.NeutralCount} 帧，" +
                      $"排除 {reconstructionReport.ExcludedCount} 帧。"
                    : $"{setLabel} 离线完整重算未发布：{reconstructionReport.UnavailableReason ?? reconstructionReport.Status}");
        }
        catch (OperationCanceledException)
        {
            callbacks.RunDiagnostic(
                experimentRunId,
                $"{setLabel} catch-up canceled {reason} run={experimentRunId:D}");
            callbacks.PublishStatus(
                $"{setLabel} 离线完整重算已取消；暂存 revision 未发布，可稍后继续。");
        }
        catch (Exception ex)
        {
            callbacks.RunDiagnostic(
                experimentRunId,
                $"{setLabel} catch-up failed {reason} run={experimentRunId:D}: {ex}");
            callbacks.PublishStatus($"{setLabel} 离线完整重算失败：{ex.Message}");
        }
        finally
        {
            lock (catchUpGate)
            {
                queuedCatchUpRuns.Remove(experimentRunId);
                if (ReferenceEquals(catchUpCancellation, cancellation))
                {
                    catchUpCancellation = null;
                }
            }

            operationLease.Dispose();
            callbacks.PublishCatchUpProgress(string.Empty);
        }
    }

    private static string DescribeProgress(string setLabel, ExperimentCatchUpProgress update)
    {
        var phase = update.Phase == ExperimentCatchUpPhase.Demodulating ? "补解调" : "完整重构";
        var unit = update.Phase == ExperimentCatchUpPhase.Demodulating ? "段" : "块";
        return update.TotalUnits <= 0
            ? $"{setLabel} 离线完整重算 · {phase}：无待处理{unit}。"
            : $"{setLabel} 离线完整重算 · {phase} {update.CompletedUnits}/{update.TotalUnits} {unit}" +
              $"（{update.CompletedFraction:P0}）";
    }

    private void RefreshPipelineManifestInputs(Guid experimentRunId, string setLabel)
    {
        var current = experimentCatalog.GetPipelineManifest(experimentRunId);
        if (current is null)
        {
            return;
        }

        var refreshed = ReconstructionPipelineManifestFactory.Finalize(
            current,
            experimentCatalog,
            dataLayout,
            DateTimeOffset.UtcNow);
        experimentCatalog.SavePipelineManifest(refreshed);
        callbacks.Diagnostic(
            $"{setLabel} pipeline manifest refreshed after manual demod status={refreshed.Status}" +
            (string.IsNullOrWhiteSpace(refreshed.UnavailableReason)
                ? string.Empty
                : $" reason={refreshed.UnavailableReason}"));
    }
}
