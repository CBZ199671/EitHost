using System.Diagnostics;
using System.IO;
using EitHost.Core.Acquisition;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RawAcquisitionPersistenceController
{
    private const string CatalogNotReadyMessage =
        "数据目录尚未准备完成，不能保存采集块；请等待初始化完成或检查数据目录初始化失败提示。";
    private const long BytesPerAdcValue = sizeof(ushort);
    private const long MaxRealtimeRawFlushBytes = 8L * 1024L * 1024L;
    private const long Hdf5MetadataAllowanceBytes = 1024L * 1024L;
    private const int RealtimeReadRowsPerBlock = 2048;

    private readonly ExperimentWorkspaceViewModel workspace;
    private readonly AcquisitionSessionController acquisition;
    private readonly DataRootLayout dataLayout;
    private readonly ExperimentCatalog catalog;
    private readonly IDataRootStorageService storage;
    private readonly RawSegmentHdf5Writer rawWriter;
    private readonly RealtimeRawPersistenceService realtimePersistence;
    private readonly Guid sessionId;
    private readonly long autoFlushByteThreshold;
    private readonly RawAcquisitionPersistenceCallbacks callbacks;

    internal RawAcquisitionPersistenceController(
        ExperimentWorkspaceViewModel workspace,
        AcquisitionSessionController acquisition,
        DataRootLayout dataLayout,
        ExperimentCatalog catalog,
        IDataRootStorageService storage,
        RawSegmentHdf5Writer rawWriter,
        RealtimeRawPersistenceService realtimePersistence,
        Guid sessionId,
        long autoFlushByteThreshold,
        RawAcquisitionPersistenceCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.rawWriter = rawWriter ?? throw new ArgumentNullException(nameof(rawWriter));
        this.realtimePersistence = realtimePersistence ?? throw new ArgumentNullException(nameof(realtimePersistence));
        this.sessionId = sessionId;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(autoFlushByteThreshold);
        this.autoFlushByteThreshold = autoFlushByteThreshold;
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal async Task SaveSelectedAsync()
    {
        if (!callbacks.IsCatalogReady())
        {
            callbacks.SetStatus($"保存采集块失败：{CatalogNotReadyMessage}");
            return;
        }

        if (callbacks.GetSelectedPairing() is not { } pairing)
        {
            callbacks.SetStatus("请先选择已绑定设备套。");
            return;
        }

        if (!acquisition.TryGetCapture(pairing.Title, out var capture))
        {
            callbacks.SetStatus($"{pairing.Title} 尚无可保存采集块。");
            return;
        }

        try
        {
            var result = await Task.Run(() =>
            {
                var saved = SaveCapture(capture, logPanel: false);
                var summary = saved.CatalogRegistered
                    ? catalog.GetRunSummary(saved.RunId)
                    : null;
                return (Saved: saved, Summary: summary);
            }).ConfigureAwait(true);
            var saved = result.Saved;
            ApplySavedArtifact(saved);
            LogSavedCapture(capture, saved);
            if (result.Summary is { } summary)
            {
                callbacks.UpsertCanonicalRun(summary);
            }

            callbacks.SetStatus(saved.CatalogRegistered
                ? $"{pairing.Title} 已保存 HDF5 并登记 catalog。"
                : $"{pairing.Title} 已保存 HDF5，但 catalog 未登记：{saved.CatalogError}");
        }
        catch (Exception ex)
        {
            callbacks.SetStatus($"保存采集块失败：{ex.Message}");
        }
    }

    internal async Task SaveAllAsync()
    {
        if (!callbacks.IsCatalogReady())
        {
            callbacks.SetStatus($"批量保存采集块失败：{CatalogNotReadyMessage}");
            return;
        }

        var captures = acquisition.GetCaptures();
        if (captures.Count == 0)
        {
            callbacks.SetStatus("尚无可批量保存的采集块。");
            return;
        }

        var result = await Task.Run(() =>
        {
            var savedCaptures = new List<ManualSavedCapture>();
            var failures = new List<string>();
            foreach (var capture in captures)
            {
                try
                {
                    savedCaptures.Add(new ManualSavedCapture(
                        capture,
                        SaveCapture(capture, logPanel: false)));
                }
                catch (Exception ex)
                {
                    failures.Add($"{capture.Pairing.Title}: {ex.Message}");
                }
            }

            return (SavedCaptures: savedCaptures, Failures: failures);
        }).ConfigureAwait(true);
        var savedRuns = result.SavedCaptures.Select(item => item.Saved).ToArray();
        var failures = result.Failures;
        foreach (var savedCapture in result.SavedCaptures)
        {
            LogSavedCapture(savedCapture.Capture, savedCapture.Saved);
        }

        if (savedRuns.Length > 0)
        {
            ApplySavedArtifact(savedRuns[^1]);
            if (savedRuns.Any(saved => saved.CatalogRegistered))
            {
                callbacks.RefreshCanonicalRuns();
            }
        }

        var uncatalogedCount = savedRuns.Count(saved => !saved.CatalogRegistered);
        callbacks.SetStatus(failures.Count == 0 && uncatalogedCount == 0
            ? $"批量保存完成：{savedRuns.Length} 套 HDF5 已登记 catalog。"
            : $"批量保存 HDF5 {savedRuns.Length} 套，catalog 未登记 {uncatalogedCount} 套，写入失败 {failures.Count} 套" +
              (failures.Count == 0 ? "。" : $"：{string.Join("；", failures)}"));
    }

    internal bool CanSaveSelected() =>
        callbacks.IsCatalogReady()
        && callbacks.GetSelectedPairing() is { } pairing
        && acquisition.HasCapture(pairing.Title);

    internal bool CanSaveAll() => callbacks.IsCatalogReady() && acquisition.CaptureCount > 0;

    internal SavedRawRun SaveCapture(CapturedRawBlock capture, bool logPanel = true)
    {
        ArgumentNullException.ThrowIfNull(capture);
        EnsureWriteCapacity(checked(capture.AdcCounts.LongLength * BytesPerAdcValue));
        var runData = new Hdf5RunData(
            sessionId,
            Guid.NewGuid(),
            capture.CapturedAt,
            CreateDeviceRunMetadata(capture.Pairing),
            capture.Excitation,
            capture.Acquisition,
            capture.AdcCounts);
        var hdf5Path = CreateRunHdf5Path(runData);
        var runDirectory = dataLayout.GetRunRelativeDirectory(runData.RunId, runData.CapturedAt);
        var experimentRunStarted = false;
        string? catalogError = null;
        if (callbacks.IsCatalogReady())
        {
            try
            {
                catalog.BeginRun(ExperimentRunRecord.CreateRecording(
                    runData.RunId,
                    sessionId,
                    capture.Pairing.Title,
                    runData.CapturedAt,
                    "manual_capture",
                    runDirectory));
                experimentRunStarted = true;
            }
            catch (Exception ex)
            {
                catalogError = $"SQLite catalog 未准备好：{ex.Message}";
            }
        }
        else
        {
            catalogError = "SQLite catalog 未准备好";
        }

        try
        {
            rawWriter.Write(
                hdf5Path,
                new RawSegmentData(
                    runData.RunId,
                    runData.SessionId,
                    segmentSequence: 0,
                    startSampleIndex: 0,
                    endSampleIndex: runData.AdcCounts.GetLength(0),
                    runData.CapturedAt,
                    runData.Device,
                    runData.Excitation,
                    runData.Acquisition,
                    runData.AdcCounts));
        }
        catch (Exception writeException)
        {
            if (experimentRunStarted)
            {
                try
                {
                    catalog.EndRun(
                        runData.RunId,
                        DateTimeOffset.UtcNow,
                        ExperimentCatalog.FailedStatus,
                        "raw HDF5 write failed");
                }
                catch (Exception catalogException)
                {
                    writeException.Data["CatalogEndRunError"] = catalogException.Message;
                }
            }

            throw;
        }

        var catalogRegistered = false;
        if (experimentRunStarted)
        {
            try
            {
                catalog.RegisterRawSegment(new RawSegmentCatalogRecord(
                    runData.RunId,
                    SegmentSequence: 0,
                    dataLayout.ToRelativeArtifactPath(hdf5Path),
                    "/raw/adc_counts",
                    StartSampleIndex: 0,
                    EndSampleIndex: runData.AdcCounts.GetLength(0),
                    SampleRows: runData.AdcCounts.GetLength(0),
                    ChannelCount: runData.AdcCounts.GetLength(1),
                    runData.CapturedAt,
                    "ready"));
                catalog.SetRunStageStatuses(runData.RunId, "ready", "pending", "pending");
                catalog.EndRun(runData.RunId, DateTimeOffset.UtcNow, ExperimentCatalog.CompletedStatus);
                catalogRegistered = true;
            }
            catch (Exception ex)
            {
                catalogError = $"SQLite catalog 登记失败：{ex.Message}";
                try
                {
                    catalog.EndRun(
                        runData.RunId,
                        DateTimeOffset.UtcNow,
                        ExperimentCatalog.FailedStatus,
                        catalogError);
                }
                catch (Exception endRunException)
                {
                    catalogError += $"；catalog 失败状态登记失败：{endRunException.Message}";
                }
            }
        }

        var saved = new SavedRawRun(runData.RunId, hdf5Path, catalogRegistered, catalogError);
        if (logPanel)
        {
            LogSavedCapture(capture, saved);
        }

        return saved;
    }

    internal BufferedAcquisitionAutoFlushResult AutoSave(
        ActiveBufferedAcquisitionSession<PairingSummaryItem> activeSession,
        ushort[] values,
        DateTimeOffset capturedAt,
        string reason)
    {
        var capture = AcquisitionSessionController.CreateCapturedRawBlock(
            activeSession.Pairing,
            capturedAt,
            values,
            activeSession.Excitation,
            activeSession.Acquisition);
        var saved = SaveCapture(capture, logPanel: false);
        var rowCount = capture.AdcCounts.GetLength(0);

        var completion = callbacks.InvokeOnUiAsync(() =>
        {
            ApplySavedArtifact(saved);
            if (saved.CatalogRegistered && catalog.GetRunSummary(saved.RunId) is { } summary)
            {
                callbacks.UpsertCanonicalRun(summary);
            }

            callbacks.AddAcquisitionLog(
                $"{DateTime.Now:HH:mm:ss} {activeSession.Pairing.Title} auto saved HDF5 {rowCount} rows ({reason}) {saved.Hdf5Path}");
            callbacks.SetStatus(saved.CatalogRegistered
                ? $"{activeSession.Pairing.Title} 内存水位触发自动保存 HDF5：{rowCount} 行。"
                : $"{activeSession.Pairing.Title} 自动保存 HDF5 完成，但 catalog 未登记：{saved.CatalogError}");
        });

        return new BufferedAcquisitionAutoFlushResult(saved.Hdf5Path, rowCount, values.LongLength)
        {
            Completion = completion
        };
    }

    internal async Task PersistRealtimeAsync(
        RealtimeRawBatch<RealtimeRawPersistenceContext> batch,
        RealtimeImagingRunConfig config,
        RealtimeRunState state)
    {
        var context = batch.Context;
        try
        {
            var result = await realtimePersistence.PersistAsync(
                    batch,
                    new RealtimeRawPersistenceMetadata(
                        config.ImagingRunId,
                        sessionId,
                        state.ExperimentStartedAt,
                        CreateDeviceRunMetadata(context.Pairing),
                        context.Excitation,
                        context.Acquisition,
                        new RawSegmentDemodulationMetadata(
                            config.FramesPerBlock,
                            config.MinimumAcceptedFrames,
                            config.DemodDiscardLeadingCycles,
                            config.DemodDiscardTrailingCycles,
                            config.InterferenceFrequencyHz)))
                .ConfigureAwait(false);
            var metricTimestamp = Stopwatch.GetTimestamp();
            var totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            var previousMetricTimestamp = Interlocked.Exchange(
                ref state.RawMetricLastTimestamp,
                metricTimestamp);
            var previousAllocatedBytes = Interlocked.Exchange(
                ref state.RawMetricLastAllocatedBytes,
                totalAllocatedBytes);
            var allocationMegabytesPerSecond = previousMetricTimestamp == 0 ||
                                               totalAllocatedBytes < previousAllocatedBytes
                ? 0.0
                : (totalAllocatedBytes - previousAllocatedBytes) /
                  Stopwatch.GetElapsedTime(previousMetricTimestamp, metricTimestamp).TotalSeconds /
                  (1024.0 * 1024.0);
            var memoryInfo = GC.GetGCMemoryInfo();
            var lohAfterGcMegabytes = memoryInfo.GenerationInfo.Length > 3
                ? memoryInfo.GenerationInfo[3].SizeAfterBytes / (1024.0 * 1024.0)
                : 0.0;
            var persistenceMetric =
                $"{context.Pairing.Title} raw persistence rows={result.SampleRows} " +
                $"write_ms={result.WriteElapsed.TotalMilliseconds:0.###} " +
                $"checkpoint_ms={result.DurabilityCheckpointElapsed.TotalMilliseconds:0.###} " +
                $"durable={(result.DurabilityCheckpointed ? 1 : 0)} " +
                $"catalog={(result.CatalogCheckpointed ? 1 : 0)} " +
                $"queue={state.RunCoordinator.Snapshot.PendingRawPersistenceCount} " +
                $"allocated_mb_s={allocationMegabytesPerSecond:0.###} " +
                $"loh_after_gc_mb={lohAfterGcMegabytes:0.###}";
            if (!result.CatalogCheckpointed)
            {
                callbacks.AddDiagnostic(persistenceMetric);
                return;
            }

            var dispatcherPostedAt = Stopwatch.GetTimestamp();
            callbacks.PostToUi(() =>
            {
                var dispatcherDelay = Stopwatch.GetElapsedTime(dispatcherPostedAt).TotalMilliseconds;
                workspace.DataTools.ApplySavedRawArtifact(
                    config.ImagingRunId,
                    result.Hdf5Path,
                    catalogRegistered: true);
                if (result.CanonicalSummary is { } summary)
                {
                    callbacks.UpsertCanonicalRun(summary);
                }

                callbacks.AddRealtimeLog(
                    $"{DateTime.Now:HH:mm:ss} {context.Pairing.Title} realtime raw checkpoint {result.SampleRows} rows ({batch.Reason}) " +
                    $"write={result.WriteElapsed.TotalMilliseconds:0.0}ms flush={result.DurabilityCheckpointElapsed.TotalMilliseconds:0.0}ms " +
                    $"ui={dispatcherDelay:0.0}ms q={state.RunCoordinator.Snapshot.PendingRawPersistenceCount} " +
                    $"{result.Hdf5Path} catalog=ok");
                callbacks.SetStatus(
                    $"{context.Pairing.Title} 实时采集检查点已保存并登记 catalog：{result.SampleRows} 行。");
                callbacks.AddDiagnostic(
                    $"{persistenceMetric} dispatcher_ms={dispatcherDelay:0.###}");
            });
        }
        catch (Exception ex)
        {
            callbacks.PostToUi(() =>
            {
                callbacks.AddRealtimeLog($"{DateTime.Now:HH:mm:ss} {context.Pairing.Title} realtime raw save failed {ex.Message}");
                callbacks.SetStatus($"{context.Pairing.Title} 实时采集 HDF5 保存失败：{ex.Message}");
            });
            throw;
        }
    }

    internal async Task CompleteRealtimeAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        bool publishReady)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        var summary = await realtimePersistence.CompleteRunAsync(
                config.ImagingRunId,
                publishReady)
            .ConfigureAwait(false);
        if (summary is null)
        {
            return;
        }

        callbacks.PostToUi(() =>
        {
            callbacks.UpsertCanonicalRun(summary);
            callbacks.SetStatus($"{config.SetLabel} raw 队列已排空，HDF5 尾分片已封片并登记 catalog。");
        });
    }

    internal long GetRealtimeFlushByteThreshold(int sampleRateHz, int readRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readRows);
        var oneReadBytes = checked(
            (long)readRows * Usb2070Constants.RequiredMeasurementChannelCount * BytesPerAdcValue);
        var oneSecondBytes = checked(
            (long)sampleRateHz * Usb2070Constants.RequiredMeasurementChannelCount * BytesPerAdcValue);
        var timeBoundedBytes = Math.Min(oneSecondBytes, MaxRealtimeRawFlushBytes);
        return Math.Max(oneReadBytes, Math.Min(autoFlushByteThreshold, timeBoundedBytes));
    }

    internal void EnsureRealtimeStartCapacity(DeviceRunParameterProfile parameters)
    {
        if (!RealtimeStoragePolicy.From(parameters.RealtimeStorageMode).PersistContinuousRaw)
        {
            return;
        }

        var readRows = Math.Max(RealtimeReadRowsPerBlock, parameters.AcquisitionReadSampleRows);
        EnsureWriteCapacity(GetRealtimeFlushByteThreshold(parameters.AcquisitionSampleRateHz, readRows));
    }

    internal void NotifyDroppedValues(
        ActiveBufferedAcquisitionSession<PairingSummaryItem> activeSession,
        long droppedValues,
        long totalDroppedValues)
    {
        callbacks.PostToUi(() =>
        {
            callbacks.AddAcquisitionLog(
                $"{DateTime.Now:HH:mm:ss} {activeSession.Pairing.Title} memory ring dropped live {droppedValues} values / total {totalDroppedValues}");
            callbacks.SetStatus($"{activeSession.Pairing.Title} 内存缓存已丢弃 {totalDroppedValues} 个旧采样值，请降低采样/缩短缓存或提高写盘速度。");
        });
    }

    private void ApplySavedArtifact(SavedRawRun saved) =>
        workspace.DataTools.ApplySavedRawArtifact(saved.RunId, saved.Hdf5Path, saved.CatalogRegistered);

    private void LogSavedCapture(CapturedRawBlock capture, SavedRawRun saved)
    {
        var catalogState = saved.CatalogRegistered
            ? "cataloged"
            : $"catalog-unregistered {saved.CatalogError}";
        callbacks.AddAcquisitionLog(
            $"{DateTime.Now:HH:mm:ss} {capture.Pairing.Title} saved {saved.Hdf5Path} {catalogState}");
    }

    private void EnsureWriteCapacity(long payloadBytes) =>
        storage.EnsureWriteCapacity(EstimateHdf5ArtifactBytes(payloadBytes));

    private static long EstimateHdf5ArtifactBytes(long payloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);
        return payloadBytes > long.MaxValue - Hdf5MetadataAllowanceBytes
            ? long.MaxValue
            : payloadBytes + Hdf5MetadataAllowanceBytes;
    }

    private string CreateRunHdf5Path(Hdf5RunData runData)
    {
        var rawDirectory = dataLayout.EnsureRawDirectory(runData.RunId, runData.CapturedAt);
        var safeLabel = string.Concat(
            runData.Device.SetLabel.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(rawDirectory, $"raw_000000_{safeLabel}.h5");
    }

    private static DeviceRunMetadata CreateDeviceRunMetadata(PairingSummaryItem pairing)
    {
        var usb = pairing.Pairing.Usb2070Candidate;
        var dds = pairing.Pairing.DdsSerialCandidate;
        return new DeviceRunMetadata(
            pairing.Pairing.Label,
            EitSet.MeasurementChannelCount,
            pairing.Pairing.Usb2070DeviceNumber,
            usb.DeviceId,
            usb.DisplayName,
            usb.Vid,
            usb.Pid,
            usb.LocationPath,
            dds.PortName ?? string.Empty,
            dds.DeviceId,
            dds.DisplayName,
            dds.Vid,
            dds.Pid,
            dds.LocationPath);
    }
}

internal sealed record ManualSavedCapture(CapturedRawBlock Capture, SavedRawRun Saved);

internal sealed record RawAcquisitionPersistenceCallbacks(
    Func<PairingSummaryItem?> GetSelectedPairing,
    Func<bool> IsCatalogReady,
    Action<ExperimentRunCatalogSummary> UpsertCanonicalRun,
    Action RefreshCanonicalRuns,
    Action<Action> PostToUi,
    Func<Action, Task> InvokeOnUiAsync,
    Action<string> AddAcquisitionLog,
    Action<string> AddRealtimeLog,
    Action<string> SetStatus,
    Action<string> AddDiagnostic);

internal sealed record SavedRawRun(
    Guid RunId,
    string Hdf5Path,
    bool CatalogRegistered,
    string? CatalogError);
