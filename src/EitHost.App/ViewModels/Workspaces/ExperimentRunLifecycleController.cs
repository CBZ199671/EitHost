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
    private readonly ExperimentReconstructionCatchUpService reconstructionCatchUpService;
    private readonly ExperimentRunOperationGate operationGate;
    private readonly Guid sessionId;
    private readonly ExperimentRunLifecycleCallbacks callbacks;
    private readonly object catchUpGate = new();
    private readonly HashSet<Guid> queuedCatchUpRuns = [];
    private readonly SemaphoreSlim reconstructionCatchUpGate = new(1, 1);
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
    /// Requests that offline catch-up stops after the block it is working on. Every block is
    /// committed independently, so the remainder simply stays pending for a later retry.
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
        ExperimentReconstructionCatchUpService reconstructionCatchUpService,
        ExperimentRunOperationGate operationGate,
        Guid sessionId,
        ExperimentRunLifecycleCallbacks callbacks)
    {
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        this.experimentCatalog = experimentCatalog ?? throw new ArgumentNullException(nameof(experimentCatalog));
        this.demodCatchUpService = demodCatchUpService ?? throw new ArgumentNullException(nameof(demodCatchUpService));
        this.reconstructionCatchUpService = reconstructionCatchUpService ?? throw new ArgumentNullException(nameof(reconstructionCatchUpService));
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
            if ((config.PersistRawAcquisitionHdf5 && demodStatus is not "complete") ||
                (config.PersistImagingFrames && reconstructionStatus is not ("complete" or "not_requested")))
            {
                QueueCatchUp(config.ImagingRunId, config.SetLabel, "run-stop");
            }
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
            callbacks.PublishCatchUpProgress($"{setLabel} 离线追赶：正在准备…");
            var report = await Task
                .Run(
                    () => demodCatchUpService.Run(experimentRunId, progress, cancellation.Token),
                    cancellation.Token)
                .ConfigureAwait(false);
            ExperimentReconstructionCatchUpReport reconstructionReport;
            await reconstructionCatchUpGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                reconstructionReport = await reconstructionCatchUpService
                    .RunAsync(experimentRunId, progress, cancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                reconstructionCatchUpGate.Release();
            }

            callbacks.RunDiagnostic(
                experimentRunId,
                $"{setLabel} catch-up {reason} run={experimentRunId:D} recovered={report.RecoveredBlockCount} " +
                $"skipped={report.SkippedBlockCount} discardedRows={report.DiscardedRawRows} " +
                $"pendingRows={report.PendingRawRows} failures={report.FailedBlockCount} " +
                $"missingSegments={report.MissingSegmentCount} status={report.DemodStatus}; " +
                $"reconRecovered={reconstructionReport.RecoveredBlockCount} imported={reconstructionReport.ImportedExistingCount} " +
                $"reconPending={reconstructionReport.PendingBlockCount} reconFailures={reconstructionReport.FailedBlockCount} " +
                $"reconStatus={reconstructionReport.ReconstructionStatus}");
            callbacks.RefreshRuns();
            callbacks.PublishStatus(
                report.PendingRawRows == 0 &&
                report.FailedBlockCount == 0 &&
                reconstructionReport.PendingBlockCount == 0 &&
                reconstructionReport.FailedBlockCount == 0
                    ? $"{setLabel} 离线追赶完成：解调补齐 {report.RecoveredBlockCount} 块，重构补齐 {reconstructionReport.RecoveredBlockCount} 块。"
                    : $"{setLabel} 离线追赶结束：raw 待解调 {report.PendingRawRows} 行；重构待处理 {reconstructionReport.PendingBlockCount} 块，失败 {reconstructionReport.FailedBlockCount} 块。");
        }
        catch (OperationCanceledException)
        {
            // Blocks commit one at a time, so the untouched remainder stays pending and the
            // operator can rerun catch-up later without duplicating work.
            callbacks.RunDiagnostic(
                experimentRunId,
                $"{setLabel} catch-up canceled {reason} run={experimentRunId:D}");
            callbacks.PublishStatus(
                $"{setLabel} 离线追赶已取消；未处理部分仍标记为待处理，可稍后用“补齐所选”继续。");
        }
        catch (Exception ex)
        {
            callbacks.RunDiagnostic(
                experimentRunId,
                $"{setLabel} catch-up failed {reason} run={experimentRunId:D}: {ex}");
            callbacks.PublishStatus($"{setLabel} 离线追赶失败：{ex.Message}");
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
        var phase = update.Phase == ExperimentCatchUpPhase.Demodulating ? "补解调" : "补重构";
        var unit = update.Phase == ExperimentCatchUpPhase.Demodulating ? "段" : "块";
        return update.TotalUnits <= 0
            ? $"{setLabel} 离线追赶 · {phase}：无待处理{unit}。"
            : $"{setLabel} 离线追赶 · {phase} {update.CompletedUnits}/{update.TotalUnits} {unit}" +
              $"（{update.CompletedFraction:P0}）";
    }
}
