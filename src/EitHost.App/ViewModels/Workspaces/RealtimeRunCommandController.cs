using EitHost.Core.Analysis;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Concurrency;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Domain;
using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Frames;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimeRunCommandController
{
    private const string CatalogNotReadyMessage =
        "数据目录尚未准备完成，不能启动实时成像；请等待初始化完成或检查数据目录初始化失败提示。";
    private readonly RealtimeSessionController sessions;
    private readonly AcquisitionSessionController acquisition;
    private readonly DdsRunController ddsRuns;
    private readonly RealtimeAcquisitionLoopController acquisitionLoop;
    private readonly RealtimePreviewController preview;
    private readonly RealtimeRunCommandCallbacks callbacks;

    internal RealtimeRunCommandController(
        RealtimeSessionController sessions,
        AcquisitionSessionController acquisition,
        DdsRunController ddsRuns,
        RealtimeAcquisitionLoopController acquisitionLoop,
        RealtimePreviewController preview,
        RealtimeRunCommandCallbacks callbacks)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        this.ddsRuns = ddsRuns ?? throw new ArgumentNullException(nameof(ddsRuns));
        this.acquisitionLoop = acquisitionLoop ?? throw new ArgumentNullException(nameof(acquisitionLoop));
        this.preview = preview ?? throw new ArgumentNullException(nameof(preview));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void StartSelected()
    {
        callbacks.AddDiagnostic("start command invoked");
        try
        {
            StartSelectedCore();
        }
        catch (Exception ex)
        {
            ReportStartFailure("启动实时成像失败", "realtime imaging start failed", ex);
        }
    }

    internal void StartSelectedCore()
    {
        if (callbacks.GetPseudo3dSelection?.Invoke() is { Enabled: true } selection)
        {
            StartTimeDivisionGroup(selection);
            return;
        }
        if (callbacks.GetSelectedPairing() is not { } pairing)
        {
            callbacks.AddDiagnostic("start rejected: no selected pairing");
            callbacks.SetStatus("请先选择已绑定设备套。");
            return;
        }

        callbacks.SaveVisibleParameters();
        if (!StartForPairing(pairing, selectForDisplay: true, out var rejectionMessage)
            && !string.IsNullOrWhiteSpace(rejectionMessage))
        {
            callbacks.SetStatus(rejectionMessage);
        }
    }

    internal void StartAll()
    {
        callbacks.AddDiagnostic("start all command invoked");
        try
        {
            StartAllCore();
        }
        catch (Exception ex)
        {
            ReportStartFailure("全部采集 + 成像启动失败", "realtime imaging start all failed", ex);
        }
    }

    internal void StartAllCore()
    {
        if (callbacks.GetPseudo3dSelection?.Invoke() is { Enabled: true } selection)
        {
            StartTimeDivisionGroup(selection);
            return;
        }
        var pairings = callbacks.GetBoundPairings();
        if (pairings.Count == 0)
        {
            callbacks.AddDiagnostic("start all rejected: no bound pairing");
            callbacks.SetStatus("请先至少绑定一套设备。");
            return;
        }

        callbacks.SaveVisibleParameters();
        var started = new List<string>();
        var skipped = new List<string>();
        foreach (var pairing in pairings.ToArray())
        {
            try
            {
                if (StartForPairing(pairing, selectForDisplay: started.Count == 0, out var rejectionMessage))
                {
                    started.Add(pairing.Title);
                }
                else if (!string.IsNullOrWhiteSpace(rejectionMessage))
                {
                    skipped.Add($"{pairing.Title}: {rejectionMessage}");
                }
            }
            catch (Exception ex)
            {
                skipped.Add($"{pairing.Title}: {ex.Message}");
                callbacks.AddDiagnostic($"{pairing.Title} start all item failed: {ex}");
                callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {pairing.Title} realtime imaging batch start failed {ex.Message}");
                sessions.ClearFailedStart();
            }
        }

        if (started.Count == 0)
        {
            callbacks.SetStatus(skipped.Count == 0
                ? "全部采集 + 成像未启动：没有可启动的空闲设备。"
                : $"全部采集 + 成像未启动：{string.Join("；", skipped)}");
            return;
        }

        var suffix = skipped.Count == 0
            ? string.Empty
            : $"；跳过 {skipped.Count} 套：{string.Join("；", skipped)}";
        callbacks.SetStatus($"全部采集 + 成像已启动：{started.Count} 套设备（{string.Join(", ", started)}）{suffix}");
        callbacks.AddLog($"{DateTime.Now:HH:mm:ss} realtime imaging batch start {started.Count} sets {string.Join(",", started)}");
        callbacks.NotifyRunStateChanged();
    }

    internal bool StartForPairing(
        PairingSummaryItem pairing,
        bool selectForDisplay,
        out string? rejectionMessage,
        Pseudo3dAcquisitionGroup? group = null,
        DeviceRunParameterProfile? parameterOverride = null)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        rejectionMessage = null;
        if (!callbacks.IsCatalogReady())
        {
            callbacks.AddDiagnostic($"start rejected: catalog not ready for {pairing.Title}");
            rejectionMessage = CatalogNotReadyMessage;
            return false;
        }

        if (acquisition.IsActive(pairing.Title))
        {
            callbacks.AddDiagnostic($"start rejected: buffered acquisition active for {pairing.Title}");
            rejectionMessage = $"{pairing.Title} 正在普通/同步采集，请先停止该采集后再启动实时成像。";
            return false;
        }

        if (sessions.TryGetState(pairing.Title, out var existingState) && existingState.IsActive)
        {
            callbacks.AddDiagnostic($"start rejected: realtime already active for {pairing.Title}");
            rejectionMessage = $"{pairing.Title} 实时成像已经在运行。";
            return false;
        }

        var portName = pairing.Pairing.DdsSerialCandidate.PortName;
        if (string.IsNullOrWhiteSpace(portName))
        {
            callbacks.AddDiagnostic($"start rejected: {pairing.Title} missing DDS port");
            rejectionMessage = $"{pairing.Title} 没有可用 DDS 串口。";
            return false;
        }

        var parameters = parameterOverride ?? callbacks.GetRunParameters(pairing);
        if (!parameters.TryValidateDemodDiscardCycles(out rejectionMessage))
        {
            callbacks.AddDiagnostic($"start rejected: invalid demod discard cycles for {pairing.Title}: {rejectionMessage}");
            return false;
        }

        if (parameters.ExcitationScanTimes > 0 && group is null)
        {
            rejectionMessage =
                "有限扫描必须先启动 USB2070 再启动 DDS；当前实时成像启动顺序尚未提供该原子流程，请设扫描圈数为 0。";
            callbacks.AddDiagnostic($"start rejected: finite scan is not owned by realtime startup for {pairing.Title}");
            return false;
        }

        try
        {
            callbacks.EnsureStorageCapacity(parameters);
        }
        catch (InsufficientDataRootCapacityException ex)
        {
            rejectionMessage = ex.Message;
            callbacks.AddDiagnostic($"start rejected: insufficient DataRoot capacity for {pairing.Title}: {ex.Message}");
            return false;
        }

        string backendProfile;
        try
        {
            backendProfile = callbacks.GetBackendProfile();
            if (string.IsNullOrWhiteSpace(backendProfile))
            {
                throw new InvalidOperationException("请先选择有效的 PyEIDORS 后端路线，再启动测量。");
            }
        }
        catch (InvalidOperationException ex)
        {
            rejectionMessage = ex.Message;
            callbacks.AddDiagnostic($"start rejected: backend not ready for {pairing.Title}: {ex.Message}");
            return false;
        }

        callbacks.ClearCompletedCalibrations(pairing.Title);
        var state = sessions.CreateState(
            pairing.Title,
            callbacks.PublishRunSnapshot,
            callbacks.PublishReferenceSnapshot);
        RealtimePreviewController.ResetTimers(state);
        if (callbacks.GetDisplayPairing() is null || selectForDisplay)
        {
            callbacks.SelectDisplayPairing(pairing);
        }

        preview.Clear(pairing.Title);
        preview.StartPump();
        var presentation = CreateStartingPresentation(parameters);
        PublishStartingPresentation(pairing.Title, parameters, presentation);
        if (callbacks.IsDisplaySet(pairing.Title))
        {
            preview.ResetPresentation();
            callbacks.ResetDisplayedPresentation(presentation);
        }

        callbacks.SetStatus($"{pairing.Title} 实时成像正在启动：准备 DDS、USB2070 和 PyEIDORS。");
        var config = CreateConfig(pairing, portName, parameters, backendProfile) with { TimeDivisionGroup = group };
        state.Config = config;
        state.VisualizationWorker = new LatestOnlyAsyncWorker<RealtimeVisualizationWorkItem>(
            (item, _) =>
            {
                preview.ProcessVisualization(config, state, item);
                return ValueTask.CompletedTask;
            },
            ex => callbacks.AddDiagnostic($"{config.SetLabel} visualization worker failed: {ex}"),
            isNonReplaceable: static item => item.NonReplaceable);
        preview.PublishMultiFrequencySummary(
            pairing.Title,
            RealtimeContactDiagnosticController.CreateMultiFrequencySummary(config));
        ddsRuns.MarkActive(pairing.Title);
        LogAcceptedStart(config);
        sessions.Start(
            state,
            cancellationToken => acquisitionLoop.RunAsync(config, state, cancellationToken));
        callbacks.ObserveTask(state);
        callbacks.NotifyRunStateChanged();
        callbacks.SetStatus($"{pairing.Title} 实时成像已启动：请求 {config.DacSettings.FrequencyHz} Hz，实际 {config.DacSettings.ActualFrequencyHz:0.########} Hz，采集→解调→PyEIDORS 重构。");
        return true;
    }

    internal void StopSelected()
    {
        var label = callbacks.GetSelectedPairing()?.Title ?? callbacks.GetDisplayPairing()?.Title;
        RequestStop(showIdleMessage: true, label);
    }

    internal bool RequestStop(bool showIdleMessage, string? setLabel = null)
    {
        if (GetStatesToStop(setLabel).FirstOrDefault()?.Config?.TimeDivisionGroup is { } group)
        {
            group.Cancel();
            foreach (var member in sessions.GetStatesToStop(null).Where(item => item.Config?.TimeDivisionGroup == group))
                sessions.RequestStop(member.SetLabel);
            callbacks.SetStatus("双设备分时组正在停止：关闭两套激励并结束采集。");
            callbacks.NotifyCanExecuteChanged();
            return true;
        }
        var request = sessions.RequestStop(setLabel);
        var states = request.States;
        if (states.Count == 0)
        {
            if (request.LegacyCancellationRequested)
            {
                callbacks.SetStatus("实时成像正在停止：已发送取消信号。");
                callbacks.NotifyCanExecuteChanged();
                return true;
            }

            if (showIdleMessage)
            {
                callbacks.AddDiagnostic("stop requested while realtime idle");
                callbacks.SetStatus(string.IsNullOrWhiteSpace(setLabel)
                    ? "实时成像未运行。"
                    : $"{setLabel} 实时成像未运行。");
            }

            return false;
        }

        foreach (var state in request.NewlyRequestedStates)
        {
            callbacks.AddDiagnostic($"{state.SetLabel} stop requested");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {state.SetLabel} realtime imaging stop requested");
        }

        callbacks.SetStatus(states.Count == 1
            ? $"{states[0].SetLabel} 实时成像正在停止：已发送取消信号。"
            : $"实时成像正在停止：已向 {states.Count} 套设备发送取消信号。");
        callbacks.NotifyCanExecuteChanged();
        return true;
    }

    internal RealtimeRunState[] GetStatesToStop(string? setLabel)
    {
        var states = sessions.GetStatesToStop(setLabel).ToArray();
        if (setLabel is not null && states.FirstOrDefault()?.Config?.TimeDivisionGroup is { } group)
            return sessions.GetStatesToStop(null).Where(item => item.Config?.TimeDivisionGroup == group).ToArray();
        return states;
    }

    internal bool CanStartSelected() =>
        callbacks.GetPseudo3dSelection?.Invoke() is { Enabled: true } selection
            ? CanStartTimeDivision(selection)
            : callbacks.GetSelectedPairing() is { } pairing && CanStartPairing(pairing);

    internal bool CanStartAll()
    {
        if (callbacks.GetPseudo3dSelection?.Invoke() is { Enabled: true } selection)
            return CanStartTimeDivision(selection);
        var pairings = callbacks.GetBoundPairings();
        return pairings.Count > 0 && pairings.Any(CanStartPairing);
    }

    internal bool CanStartPairing(PairingSummaryItem pairing) =>
        callbacks.IsCatalogReady()
        && !acquisition.IsActive(pairing.Title)
        && !sessions.IsSetActive(pairing.Title);

    private bool CanStartTimeDivision(Pseudo3dRunSelection selection) =>
        !sessions.IsAnyActive && selection.Lower is { } lower && selection.Upper is { } upper &&
        lower.Title != upper.Title && CanStartPairing(lower) && CanStartPairing(upper);

    private void StartTimeDivisionGroup(Pseudo3dRunSelection selection)
    {
        var bound = callbacks.GetBoundPairings();
        if (bound.Count < 2 || selection.Lower is not { } lower || selection.Upper is not { } upper ||
            !bound.Contains(lower) || !bound.Contains(upper) || !CanStartTimeDivision(selection))
            throw new InvalidOperationException("同频分时伪三维需要选择两套空闲且不同的已绑定设备；请先停止正在运行的采集。");
        if (callbacks.CreateUsbDevice(lower).DeviceNumber == callbacks.CreateUsbDevice(upper).DeviceNumber ||
            string.Equals(lower.Pairing.DdsSerialCandidate.PortName, upper.Pairing.DdsSerialCandidate.PortName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("伪三维的两套设备必须使用不同 USB2070 和不同 DDS 串口。");
        callbacks.SaveVisibleParameters();
        // The lower layer supplies one shared inverse configuration. Saved
        // independent 2-D device profiles remain available after leaving this mode.
        var parameters = Pseudo3dAcquisitionGroup.ApplyProfile(callbacks.GetRunParameters(lower));
        callbacks.EnsureStorageCapacity(parameters);
        if (string.IsNullOrWhiteSpace(callbacks.GetBackendProfile()))
            throw new InvalidOperationException("请先选择有效的 PyEIDORS 后端路线。");
        var group = new Pseudo3dAcquisitionGroup(lower.Title, upper.Title);
        try
        {
            if (!StartForPairing(lower, true, out var lowerError, group, parameters))
                throw new InvalidOperationException(lowerError);
            if (!StartForPairing(upper, false, out var upperError, group, parameters))
                throw new InvalidOperationException(upperError);
            callbacks.SetStatus($"同频分时伪三维已启动：{lower.Title} → {upper.Title}；3125 Hz、10 µA、20 周期、前 8 后 4。两层网格及算法使用下层设置。");
        }
        catch
        {
            group.Cancel();
            throw;
        }
    }

    internal bool CanStopSelected()
    {
        var label = callbacks.GetSelectedPairing()?.Title ?? callbacks.GetDisplayPairing()?.Title;
        return label is not null
            ? sessions.TryGetState(label, out var state) && state.IsActive || sessions.HasUnfinishedTask
            : sessions.IsAnyActive;
    }

    internal static string CreateStartSummary(DeviceRunParameterProfile parameters)
    {
        var storagePolicy = RealtimeStoragePolicy.From(parameters.RealtimeStorageMode);
        var storage = storagePolicy.PersistContinuousRaw
            ? "完整记录：连续 raw + 解调状态 + 重构结果"
            : "仅预览：不持久化";
        var saveRecon = parameters.RealtimeSaveReconstructionResults
            ? "归档后端诊断 HDF5（随实验）"
            : "不归档后端诊断 HDF5";
        var saveFrames = storagePolicy.PersistImagingFrames
            ? "规范记录解调/诊断/参考/电导率 HDF5"
            : "不记录派生数据";
        var outlierDetection = parameters.RealtimeEnableOutlierDetection ? "异常值检测开" : "异常值检测关";
        var outlierCompensation = parameters.RealtimeEnableOutlierCompensation ? "异常值补偿开" : "异常值补偿关";
        var temporalDespiking = parameters.RealtimeEnableTemporalDespiking ? "时序去毛刺开（延迟2块）" : "时序去毛刺关";
        var dynamicKalman = parameters.RealtimeEnableDynamicKalman && parameters.RealtimeEnableTemporalDespiking
            ? $"动态Kalman开（{parameters.RealtimeDynamicKalmanMode}，lag0）"
            : parameters.RealtimeEnableDynamicKalman
                ? "动态Kalman待机（需开启时序去毛刺）"
                : "动态Kalman关";
        var scalePolicy = parameters.RealtimeReferenceScalePolicy == EcdCwrReferenceScalePolicy.CommonScaleNormalized
            ? "公共尺度归一化（移除目标公共 α）"
            : "保留物理尺度（保留全局慢变）";
        return $"实时成像启动中：{parameters.RealtimeReconstructionRoute}，{storage}；{saveRecon}；{saveFrames}；{outlierDetection}；{outlierCompensation}；{temporalDespiking}；{dynamicKalman}；{scalePolicy}。";
    }

    private RealtimeImagingRunConfig CreateConfig(
        PairingSummaryItem pairing,
        string portName,
        DeviceRunParameterProfile parameters,
        string backendProfile) =>
        new(
            pairing,
            pairing.Title,
            portName,
            callbacks.CreateUsbDevice(pairing),
            new DdsDacSettings(
                checked((byte)parameters.DdsDacChannel),
                parameters.DdsFrequencyHz,
                parameters.DdsGain,
                parameters.DdsPhaseDegrees),
            new DdsExcitationSettings(
                parameters.ExcitationMode,
                parameters.DdsFrequencyHz,
                parameters.ExcitationChannelCycles,
                parameters.ExcitationScanTimes),
            checked((byte)parameters.DdsPgaGain),
            parameters.CreateAcquisitionSettings(),
            callbacks.GetReadRows(parameters.AcquisitionReadSampleRows),
            parameters.RealtimeFramesPerBlock,
            parameters.RealtimeMinimumAcceptedFrames,
            parameters.DemodDiscardLeadingCycles,
            parameters.DemodDiscardTrailingCycles,
            parameters.RealtimeMeshSize,
            parameters.RealtimeDifferenceLambda,
            parameters.RealtimeReconstructionRoute,
            parameters.RealtimeUseCustomLambda,
            parameters.RealtimeDifferenceOrientation,
            RealtimeStoragePolicy.From(parameters.RealtimeStorageMode),
            parameters.RealtimeSaveReconstructionResults,
            parameters.RealtimeEnableOutlierDetection,
            parameters.RealtimeEnableOutlierCompensation,
            parameters.RealtimeEnableTemporalDespiking,
            parameters.RealtimeEnableDynamicKalman && parameters.RealtimeEnableTemporalDespiking,
            parameters.RealtimeDynamicKalmanMode,
            backendProfile,
            Guid.NewGuid(),
            parameters.CreateExcitationMetadata(),
            parameters.RealtimeUseFrequencyDivisionLockIn
                ? callbacks.GetInterferenceFrequencies(pairing, parameters)
                : [],
            parameters.RealtimeUseFrequencyDivisionLockIn,
            callbacks.GetContactSubjectProfile(),
            NormalizeFirmwareBuildId(callbacks.GetContactFirmwareBuildId()),
            callbacks.GetContactHealthyCalibrationAuthorized(),
            callbacks.CreatePairingMapSummary(),
            EcdCwrReferenceScalePolicy.Normalize(parameters.RealtimeReferenceScalePolicy));

    private void PublishStartingPresentation(
        string setLabel,
        DeviceRunParameterProfile parameters,
        RealtimeStartPresentation presentation)
    {
        preview.PublishImageStats(setLabel, presentation.ImageStats);
        preview.PublishReconstructionActivity(setLabel, "重构状态：等待参考");
        preview.PublishReferenceInvalidated(setLabel, false);
        preview.PublishContactSummary(setLabel, presentation.ContactSummary);
        preview.PublishMultiFrequencySummary(setLabel, presentation.MultiFrequencySummary);
        preview.PublishReferenceSummary(setLabel, presentation.ReferenceSummary);
        preview.PublishQualityAxes(
            setLabel,
            "数据质量：等待首个解调块",
            $"参考模式：尚未锁定 · {parameters.RealtimeReferenceScalePolicy}",
            "重构质量：尚未开始",
            "ROI 就绪：否 · 等待参考与重构");
        preview.PublishBaselineIntegritySummary(setLabel, presentation.BaselineIntegritySummary);
        preview.PublishSummary(setLabel, presentation.ImagingSummary);
    }

    private static RealtimeStartPresentation CreateStartingPresentation(DeviceRunParameterProfile parameters) =>
        new(
            "重构图像：等待第一帧。",
            parameters.RealtimeEnableOutlierDetection ? "接触诊断：等待 qc_ref。" : "异常值检测：已关闭。",
            "多频证据：单频模式，证据 E 未启用。",
            "参考帧：正在收集高质量候选；100 帧后可由用户锁定并开始正常 ROI。",
            "基线诊断：等待参考锁定。",
            CreateStartSummary(parameters));

    private void LogAcceptedStart(RealtimeImagingRunConfig config)
    {
        var blockProfile = RealtimeBlockAggregationProfile.Resolve(config.FramesPerBlock, config.MinimumAcceptedFrames);
        var estimatedBlockLatencyMilliseconds = blockProfile.EstimateAcquisitionLatencyMilliseconds(
            config.DacSettings.ActualFrequencyHz,
            config.ExcitationSettings.ChannelCycles);
        var estimatedBlockRate = estimatedBlockLatencyMilliseconds > 0
            ? 1000.0 / estimatedBlockLatencyMilliseconds
            : 0.0;
        var usableCycles = Math.Max(
            0.0,
            config.ExcitationSettings.ChannelCycles -
            config.DemodDiscardLeadingCycles -
            config.DemodDiscardTrailingCycles);
        callbacks.AddDiagnostic(
            $"{config.SetLabel} start accepted dds={config.DdsPortName} usb=#{config.Pairing.Pairing.Usb2070DeviceNumber} requested_freq={config.DacSettings.FrequencyHz}Hz ftw={config.DacSettings.FrequencyTuningWord} actual_freq={config.DacSettings.ActualFrequencyHz:0.########}Hz storage={config.StoragePolicy.Value} fd_lockin={(config.UseFrequencyDivisionLockIn ? "on" : "off")} outlier_detect={(config.EnableOutlierDetection ? "on" : "off")} outlier_comp={(config.EnableOutlierCompensation ? "on" : "off")} interference=[{string.Join(",", config.InterferenceFrequencyHz.Select(frequency => FormattableString.Invariant($"{frequency:g}Hz")))}] gain={config.DacSettings.Gain:g} pga={config.PgaGain} readRows={config.ReadRows} block_mode={blockProfile.Code} frames={config.FramesPerBlock}/{config.MinimumAcceptedFrames} estimated_block_latency_ms={estimatedBlockLatencyMilliseconds:0.###} estimated_block_rate={estimatedBlockRate:0.###}block/s usable_cycles={usableCycles:0.###}/{config.ExcitationSettings.ChannelCycles:0.###} discard_mode=manual discard={config.DemodDiscardLeadingCycles:g}/{config.DemodDiscardTrailingCycles:g} route={config.ReconstructionRoute}");
        callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime imaging start requested={config.DacSettings.FrequencyHz}Hz actual={config.DacSettings.ActualFrequencyHz:0.########}Hz FTW={config.DacSettings.FrequencyTuningWord}");
    }

    private void ReportStartFailure(string userPrefix, string logPrefix, Exception ex)
    {
        callbacks.AddDiagnostic(logPrefix + ": " + ex);
        callbacks.SetStatus($"{userPrefix}：{ex.Message}");
        callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {logPrefix} {ex.Message}");
        sessions.ClearFailedStart();
        callbacks.NotifyRunStateChanged();
    }

    private static string NormalizeFirmwareBuildId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unreported" : value.Trim();
}

internal sealed record RealtimeRunCommandCallbacks(
    Func<PairingSummaryItem?> GetSelectedPairing,
    Func<PairingSummaryItem?> GetDisplayPairing,
    Func<IReadOnlyList<PairingSummaryItem>> GetBoundPairings,
    Func<bool> IsCatalogReady,
    Action SaveVisibleParameters,
    Func<PairingSummaryItem, DeviceRunParameterProfile> GetRunParameters,
    Action<DeviceRunParameterProfile> EnsureStorageCapacity,
    Action<string> ClearCompletedCalibrations,
    Action<PairingSummaryItem> SelectDisplayPairing,
    Func<string, bool> IsDisplaySet,
    Action<RealtimeStartPresentation> ResetDisplayedPresentation,
    Func<PairingSummaryItem, Usb2070Device> CreateUsbDevice,
    Func<int, int> GetReadRows,
    Func<PairingSummaryItem, DeviceRunParameterProfile, double[]> GetInterferenceFrequencies,
    Func<string> GetBackendProfile,
    Func<string> GetContactSubjectProfile,
    Func<string> GetContactFirmwareBuildId,
    Func<bool> GetContactHealthyCalibrationAuthorized,
    Func<string> CreatePairingMapSummary,
    Action<RealtimeRunState> PublishRunSnapshot,
    Action<ReferenceReconstructionSnapshot> PublishReferenceSnapshot,
    Action<RealtimeRunState> ObserveTask,
    Action<string> AddDiagnostic,
    Action<string> AddLog,
    Action<string> SetStatus,
    Action NotifyRunStateChanged,
    Action NotifyCanExecuteChanged,
    Func<Pseudo3dRunSelection>? GetPseudo3dSelection = null);

internal sealed record RealtimeStartPresentation(
    string ImageStats,
    string ContactSummary,
    string MultiFrequencySummary,
    string ReferenceSummary,
    string BaselineIntegritySummary,
    string ImagingSummary);
