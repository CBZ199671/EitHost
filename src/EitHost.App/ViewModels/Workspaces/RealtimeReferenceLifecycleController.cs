using System.Diagnostics;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.Baseline;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Frames;

namespace EitHost.App.ViewModels.Workspaces;

internal enum RealtimeReferenceUiChange
{
    RefreshWindows,
    RefreshWindowsAndUseCurrentCommand,
    RefreshWindowsAndAllCommands,
    UseCurrentCommand,
    CalibrationState
}

internal sealed record RealtimeReferenceSwitchUiUpdate(
    string SetLabel,
    int BlockNumber,
    int OldEpoch,
    int NewEpoch,
    string? ActionGroupId);

internal sealed record RealtimeReferenceLifecycleCallbacks(
    Action<string> Diagnostic,
    Action<string, bool> PublishReferenceInvalidated,
    Action<string, string> PublishReferenceSummary,
    Action<string, string> PublishContactSummary,
    Action<string, string> PublishBoundaryFitUnavailable,
    Action<string, string> PublishRoiUnavailable,
    PublishRealtimeQualityAxesCallback PublishQualityAxes,
    Action<string> PublishProvisionalRoiUnavailable,
    Action<string> QueueLog,
    Action<string> ClearCachedLowConfidenceImage,
    Action<string, RealtimeRunState> PublishReferenceNeutralImage,
    Func<RealtimeImagingRunConfig, RealtimeRunState, string> CreateReferenceModeStatus,
    Action<string> ClearCompletedCalibrations,
    Action<string, RealtimeReferenceUiChange> NotifyUi,
    Action<RealtimeReferenceSwitchUiUpdate> ReferenceSwitchCommitted);

internal sealed class RealtimeReferenceLifecycleController
{
    private const int RealtimeContactCalibrationMinimumFrames = 100;
    private const int RealtimeContactCalibrationMaximumFrames = 300;
    private static readonly TimeSpan RealtimeUiStatusInterval = TimeSpan.FromMilliseconds(250);
    private static readonly EitBaselineIntegrityAnalyzer RealtimeBaselineIntegrityAnalyzer = new();
    private readonly object synchronizedReferenceActionGate;
    private readonly RealtimeDerivedPersistenceController derivedPersistence;
    private readonly RealtimeContactCalibrationController contactCalibration;
    private readonly RealtimeReferenceLifecycleCallbacks callbacks;

    internal RealtimeReferenceLifecycleController(
        object synchronizedReferenceActionGate,
        RealtimeDerivedPersistenceController derivedPersistence,
        RealtimeContactCalibrationController contactCalibration,
        RealtimeReferenceLifecycleCallbacks callbacks)
    {
        this.synchronizedReferenceActionGate = synchronizedReferenceActionGate ?? throw new ArgumentNullException(nameof(synchronizedReferenceActionGate));
        this.derivedPersistence = derivedPersistence ?? throw new ArgumentNullException(nameof(derivedPersistence));
        this.contactCalibration = contactCalibration ?? throw new ArgumentNullException(nameof(contactCalibration));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal static double? TryEstimateCommonScale(
        RealtimeRunState state,
        IReadOnlyList<double> target)
    {
        return state.ReferenceUsesCommonScaleNormalization &&
            state.ReferenceVoltage208 is { Length: RealtimeReconstructionRequest.BoundaryVoltageCount } reference &&
            EcdCwrCommonScaleNormalizer.TryEstimateRobustPositiveScale(reference, target, out var scale)
                ? scale
                : null;
    }

    internal static void ResetStartupProgress(RealtimeRunState state)
    {
        state.StartupDegradedReferenceWarmupCount = 0;
        state.StartupDegradedReferenceAggregateCount = 0;
        state.StartupDegradedReferenceFaultElectrodes = [];
    }

    internal void InvalidateProvisionalReference(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        string reason)
    {
        if (!state.ReferenceIsProvisional)
        {
            return;
        }

        state.ReferenceIsProvisional = false;
        state.ReferenceVoltage208 = null;
        state.ReferenceUsesCommonScaleNormalization = false;
        state.LatestCommonScaleNormalizationFactor = null;
        state.ReferenceReal208 = null;
        state.ReferenceImaginary208 = null;
        state.ReferenceDemodulation = null;
        state.BaselineIntegrityNoiseModel = null;
        state.LastBaselineClassification = null;
        state.RobustReference = null;
        state.BoundaryNoiseModel = null;
        state.BoundaryChangeGate = null;
        state.BoundaryNoChangeActive = false;
        state.ReferenceBlockNumber = 0;
        state.ReferenceStartSampleIndex = -1;
        state.ReferenceCandidateFrames.Clear();
        Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
        Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
        state.ReferenceStationarity.Reset();
        state.LatestReferenceStationarity = null;
        state.ContactMonitor = null;
        state.ContactCalibration = null;
        state.ExportableContactCalibration = null;
        state.ExportableSessionCalibration = null;
        state.ContactCalibrationFrames.Clear();
        state.ImageRasterCache.ResetColorScale();
        ResetRealtimeTemporalWindow(state);
        state.DynamicKalmanGeneration++;
        state.DynamicKalmanResetPending = true;
        callbacks.Diagnostic($"{config.SetLabel} provisional reference invalidated: {reason}");
        callbacks.PublishReferenceSummary(
            config.SetLabel,
            $"快速预览参考已失效（{reason}）；正在重新收集 100 个严格全绿帧。");
        callbacks.PublishRoiUnavailable(
            config.SetLabel,
            "ROI：快速预览参考已失效；等待重新锁定参考。");
        callbacks.PublishBoundaryFitUnavailable(
            config.SetLabel,
            $"边界电压：快速预览参考已失效（{reason}），已清除旧拟合。");
        callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.CalibrationState);
    }

    internal void ResetIncompatibleStartupReference(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        ElectrodeContactDiagnosticResult? contactResult)
    {
        var current = state.StartupDegradedReference;
        if (current is null || contactResult is null ||
            state.StartupDegradedReferenceAccumulator.IsCompatible(contactResult))
        {
            return;
        }

        var previousFaults = string.Join(',', current.FaultElectrodes);
        state.ReferenceVoltage208 = null;
        state.ReferenceUsesCommonScaleNormalization = false;
        state.LatestCommonScaleNormalizationFactor = null;
        state.ReferenceReal208 = null;
        state.ReferenceImaginary208 = null;
        state.ReferenceDemodulation = null;
        state.BaselineIntegrityNoiseModel = null;
        state.LastBaselineClassification = null;
        state.PendingReferenceLockKind = "fault_recovery";
        state.StartupDegradedReference = null;
        state.BoundaryNoiseModel = null;
        state.BoundaryChangeGate = null;
        state.BoundaryNoChangeActive = false;
        state.ImageRasterCache.ResetColorScale();
        state.StartupDegradedReferenceAccumulator.Reset();
        ResetStartupProgress(state);
        state.ReferenceBlockNumber = 0;
        state.ReferenceStartSampleIndex = -1;
        state.ReferenceInvalidated = false;
        state.DynamicKalmanGeneration++;
        state.DynamicKalmanResetPending = true;
        ResetRealtimeTemporalWindow(state);
        callbacks.PublishReferenceInvalidated(config.SetLabel, false);
        callbacks.PublishReferenceSummary(
            config.SetLabel,
            $"降级参考已自动作废：故障集合 [{previousFaults}] 已变化，正在按当前健康通道重建。");
        callbacks.Diagnostic(
            $"{config.SetLabel} startup degraded reference reset previous-faults={previousFaults} reason=fault-set-changed");
        callbacks.ClearCachedLowConfidenceImage(config.SetLabel);
    }

    internal EcdCwrStartupDegradedReference? TryLockStartupDegradedReference(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        ElectrodeContactDiagnosticResult? contactResult)
    {
        var update = state.StartupDegradedReferenceAccumulator.Update(
            block.Frames,
            contactResult,
            diagnosticAggregate: block.DiagnosticAverage);
        state.StartupDegradedReferenceWarmupCount = update.UsableFrameCount;
        state.StartupDegradedReferenceAggregateCount = update.AggregateFrameEquivalentCount;
        state.StartupDegradedReferenceFaultElectrodes = update.FaultElectrodes.ToArray();
        if (!update.Eligible)
        {
            ResetStartupProgress(state);
            return null;
        }

        if (!update.Locked || update.Reference is null)
        {
            if (ShouldUpdateRealtimeStatus(state))
            {
                callbacks.PublishReferenceSummary(
                    config.SetLabel,
                    $"降级参考预热：故障电极 [{string.Join(',', update.FaultElectrodes)}] 已确认；掩膜后稳定健康帧等效 {update.UsableFrameCount}/{RealtimeContactCalibrationMinimumFrames}（聚合后备 {update.AggregateFrameEquivalentCount}）。");
            }

            return null;
        }

        var reference = update.Reference;
        state.StartupDegradedReferenceWarmupCount = reference.RobustReference.FrameCount;
        state.StartupDegradedReferenceFaultElectrodes = reference.FaultElectrodes.ToArray();
        state.ReferenceVoltage208 = reference.RobustReference.Voltage208.ToArray();
        ActivateRealtimeReferenceEpoch(state, block, reference.RobustReference);
        state.ReferenceIsProvisional = false;
        state.StartupDegradedReference = reference;
        state.RobustReference = null;
        state.BoundaryNoiseModel = null;
        state.BoundaryChangeGate = null;
        state.BoundaryNoChangeActive = false;
        state.ReferenceBlockNumber = block.BlockNumber;
        state.ReferenceResetRequested = false;
        state.ReferenceInvalidated = false;
        state.ContactMonitor = null;
        state.ContactCalibration = null;
        state.ExportableContactCalibration = null;
        state.ExportableSessionCalibration = null;
        state.ReferenceCandidateFrames.Clear();
        Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
        Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
        state.ContactCalibrationFrames.Clear();
        state.DynamicKalmanGeneration++;
        state.DynamicKalmanResetPending = true;
        state.ResetReconstructionCircuitBreaker("startup_degraded_reference_locked");
        callbacks.Diagnostic($"{config.SetLabel} {update.Status} block={block.BlockNumber}");
        callbacks.PublishReferenceInvalidated(config.SetLabel, false);
        callbacks.PublishReferenceSummary(
            config.SetLabel,
            $"降级参考：已用 {reference.RobustReference.FrameCount} 个稳定健康帧锁定；屏蔽电极 [{string.Join(',', reference.FaultElectrodes)}]，仅对后续变化低置信重构。");
        callbacks.PublishContactSummary(
            config.SetLabel,
            $"启动接触诊断：红 [{string.Join(',', reference.FaultElectrodes)}]；降级加权重构已启用。");
        callbacks.PublishReferenceNeutralImage(config.SetLabel, state);
        return reference;
    }

    internal async Task AccumulateCandidatesAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        var sequenceBefore = state.ReferenceCandidateNextSequence;
        var fingerprint = new EcdCwrReferenceOperatingPoint(
            config.DacSettings.ActualFrequencyHz,
            config.DacSettings.Gain,
            config.DacSettings.Channel,
            config.DacSettings.PhaseDegrees,
            config.PgaGain,
            config.AcquisitionSettings.SampleRateHz,
            config.AcquisitionSettings.Range.ToString(),
            config.ExcitationSettings.Mode.ToString(),
            config.ExcitationSettings.ChannelCycles,
            config.ExcitationSettings.ScanTimes,
            config.DemodDiscardLeadingCycles,
            config.DemodDiscardTrailingCycles,
            config.FramesPerBlock,
            config.MinimumAcceptedFrames,
            config.PairingMapSummary,
            config.DifferenceOrientation,
            config.UseFrequencyDivisionLockIn,
            config.InterferenceFrequencyHz).Fingerprint;
        var blockCapturedAt = DateTimeOffset.Now;
        var persistedCandidates = new List<ImagingReferenceCandidateRecord>();
        foreach (var frame in block.Frames)
        {
            if (!EcdCwrRobustReferenceBuilder.IsStrictGreenFrame(frame))
            {
                state.ReferenceCandidateContinuityBreakPending = true;
                continue;
            }

            var globalStart = block.StartSampleIndex + frame.StartSample;
            var globalEnd = block.StartSampleIndex + frame.EndSample;
            var gapBefore = state.ReferenceCandidateContinuityBreakPending ? 1 : 0;

            var sequence = ++state.ReferenceCandidateNextSequence;
            var sourceId = $"{config.ImagingRunId:N}:{sequence}";
            var secondsBeforeBlockEnd = Math.Max(
                0.0,
                (block.EndSampleIndex - globalEnd) /
                (double)config.AcquisitionSettings.SampleRateHz);
            var capturedAt = blockCapturedAt - TimeSpan.FromSeconds(secondsBeforeBlockEnd);
            var candidate = new EcdCwrReferenceCandidate(
                sequence,
                sourceId,
                capturedAt,
                block.BlockNumber,
                frame.FrameNumber,
                globalStart,
                globalEnd,
                fingerprint,
                gapBefore,
                frame.WindowQualities.Sum(quality => quality.AdcSaturationCount),
                state.LatestContactResult?.Summary ?? "预参考接触未评估",
                new EcdCwrRobustReferenceObservation(
                    frame.FlattenAmplitudesRowMajor(),
                    frame.FlattenFullRealRowMajor(),
                    frame.FlattenFullImaginaryRowMajor()));
            lock (state.ReferenceCandidateGate)
            {
                state.ReferenceCandidateHistory.Add(candidate);
                state.ReferenceCandidateFrameBySourceId[sourceId] = frame;
                var memoryIds = state.ReferenceCandidateHistory.MemoryCandidates
                    .Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var evictedId in state.ReferenceCandidateFrameBySourceId.Keys
                    .Where(id => !memoryIds.Contains(id))
                    .ToArray())
                {
                    state.ReferenceCandidateFrameBySourceId.Remove(evictedId);
                }
            }

            if (config.PersistImagingFrames)
            {
                persistedCandidates.Add(new ImagingReferenceCandidateRecord(
                    config.ImagingRunId,
                    candidate.Sequence,
                    candidate.SourceId,
                    candidate.CapturedAt,
                    candidate.BlockNumber,
                    candidate.FrameNumber,
                    candidate.StartSampleIndex,
                    candidate.EndSampleIndex,
                    candidate.Fingerprint,
                    candidate.GapBeforeSamples,
                    candidate.SaturationCount,
                    candidate.ContactEvidence,
                    candidate.Observation.Voltage208,
                    candidate.Observation.FullReal256,
                    candidate.Observation.FullImaginary256));
            }

            state.ReferenceCandidateContinuityBreakPending = false;
        }

        if (state.ReferenceCandidateNextSequence == sequenceBefore)
        {
            return;
        }

        await derivedPersistence.PersistReferenceCandidatesAsync(
            config,
            state,
            block,
            persistedCandidates).ConfigureAwait(false);

        int memoryCount;
        int continuousCount;
        lock (state.ReferenceCandidateGate)
        {
            memoryCount = state.ReferenceCandidateHistory.MemoryCount;
            continuousCount = state.ReferenceCandidateHistory.LatestContiguousCount;
        }

        Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, memoryCount);
        Volatile.Write(ref state.ReferenceCandidateContinuousCount, continuousCount);
        if (state.ReferenceCandidateNextSequence / 25 != sequenceBefore / 25)
        {
            callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.RefreshWindowsAndAllCommands);
        }
    }

    internal static EcdCwrRobustReferenceOptions CreateRobustReferenceOptions(
        RealtimeImagingRunConfig config)
    {
        return new EcdCwrRobustReferenceOptions(
            MinimumFrameCount: RealtimeContactCalibrationMinimumFrames,
            NormalizeCommonScale: EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(
                config.ReferenceScalePolicy),
            PhysicalAdcLsbVolts: Usb2070VoltageScale.GetLsbVolts(
                config.AcquisitionSettings.Range),
            DetrendNoiseModel: true);
    }

    internal bool CommitPreparedSwitch(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        EcdCwrRobustReference replacement;
        EcdCwrReferenceWindow? replacementWindow;
        DemodulatedFrame[] replacementFrames;
        string lockKind;
        var oldEpoch = state.ReferenceEpoch;
        lock (synchronizedReferenceActionGate)
        {
            lock (state.ReplacementReferenceGate)
            {
                if (!state.ReplacementReferenceCollecting
                    || Volatile.Read(ref state.ReplacementSwitchRequested) == 0
                    || state.ReplacementPreparedReference is not { } prepared)
                {
                    return false;
                }

                replacement = prepared;
                replacementWindow = state.ReplacementPreparedWindow;
                replacementFrames = state.ReplacementPreparedFrames;
                lockKind = state.ReplacementPreparedLockKind;

                state.ReferenceVoltage208 = replacement.Voltage208.ToArray();
                state.RobustReference = replacement;
                state.ReferenceIsProvisional = false;
                state.ActiveReferenceWindow = replacementWindow;
                state.PendingReferenceLockKind = lockKind;
                ActivateRealtimeReferenceEpoch(state, block, replacement, lockKind);
                state.ActiveReferenceActionGroupId = state.ReplacementReferenceActionGroupId;
                state.ActiveReferenceCommonActionAt = state.ReplacementReferenceCommonActionAt;
                state.ActiveReferenceWindowSkewMilliseconds =
                    state.ReplacementReferenceWindowSkewMilliseconds;
                state.ActiveReferenceSwitchSkewMilliseconds =
                    state.ReplacementReferenceCommonActionAt is { } commonActionAt
                        ? (state.ReferenceLockedAt - commonActionAt).TotalMilliseconds
                        : null;
                state.ActiveReferenceSynchronizedSetCount =
                    state.ReplacementReferenceSynchronizedSetCount;
                state.ReferenceBlockNumber = block.BlockNumber;
                state.ReferenceResetRequested = false;
                state.ReferenceInvalidated = false;
                state.BaselineIntegrityNoiseModel = replacement.NoiseModel;
                state.BoundaryNoiseModel = replacement.NoiseModel;
                state.BoundaryChangeGate = replacement.NoiseModel is null
                    ? null
                    : new EcdCwrBoundaryChangeGate(replacement.NoiseModel);
                state.BoundaryNoChangeActive = false;
                state.ReferenceCandidateFrames.Clear();
                state.ReferenceCandidateFrames.AddRange(replacementFrames);
                state.ContactMonitor = null;
                state.ContactCalibration = null;
                state.ExportableContactCalibration = null;
                state.ExportableSessionCalibration = null;
                state.ContactCalibrationFrames.Clear();
                state.StartupDegradedReferenceAccumulator.Reset();
                state.StartupDegradedReference = null;
                ResetStartupProgress(state);
                state.LastBaselineClassification = null;
                state.LatestContactResult = null;
                state.ImageRasterCache.ResetColorScale();
                ResetRealtimeTemporalWindow(state);
                state.DynamicKalmanGeneration++;
                state.DynamicKalmanResetPending = true;
                state.ResetReconstructionCircuitBreaker("replacement_reference_switched");
                Volatile.Write(
                    ref state.ContactSubspaceEvidence,
                    EcdCwrContactSubspaceEvidenceInput.Unavailable(
                        "unavailable: reference changed; waiting for compatible backend J_z"));

                state.ClearReplacementPreparation();
            }
        }

        callbacks.ClearCompletedCalibrations(config.SetLabel);
        if (config.PersistImagingFrames)
        {
            derivedPersistence.PersistReferenceEpoch(config, state, replacement);
        }

        contactCalibration.LockBaseline(config, state, block, replacement);
        callbacks.PublishReferenceInvalidated(config.SetLabel, false);
        callbacks.PublishBoundaryFitUnavailable(
            config.SetLabel,
            $"边界电压：参考已从 e{oldEpoch} 原子切换到 e{state.ReferenceEpoch}，等待新 epoch 目标拟合。");
        callbacks.PublishReferenceSummary(
            config.SetLabel,
            $"参考已确认切换：e{oldEpoch} → e{state.ReferenceEpoch}；新参考 {replacement.FrameCount} 帧，时序/Kalman/图像尺度已重置，ROI 从新 epoch 分段继续。");
        callbacks.PublishQualityAxes(
            config.SetLabel,
            referenceMode: callbacks.CreateReferenceModeStatus(config, state),
            reconstructionQuality: "重构质量：等待新参考后的首个目标",
            roiReadiness: $"ROI 就绪：待新 epoch e{state.ReferenceEpoch} 首帧；历史曲线保留且断线分段");
        callbacks.PublishReferenceNeutralImage(config.SetLabel, state);
        callbacks.Diagnostic(
            $"{config.SetLabel} atomic reference switch oldEpoch={oldEpoch} newEpoch={state.ReferenceEpoch} block={block.BlockNumber} kind={lockKind} frames={replacement.FrameCount}");
        callbacks.QueueLog(
            $"{DateTime.Now:HH:mm:ss} {config.SetLabel} 参考原子切换 e{oldEpoch}→e{state.ReferenceEpoch} block {block.BlockNumber}; ROI epoch boundary");
        callbacks.ReferenceSwitchCommitted(new RealtimeReferenceSwitchUiUpdate(
            config.SetLabel,
            block.BlockNumber,
            oldEpoch,
            state.ReferenceEpoch,
            state.ActiveReferenceActionGroupId));
        return true;
    }

    internal EcdCwrReferenceLockAction TryLockReference(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        out EcdCwrRobustReference? lockedReference)
    {
        lockedReference = null;
        if (state.ReferenceResetRequested)
        {
            state.ReferenceCandidateFrames.Clear();
            Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
            Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
            state.ReferenceStationarity.Reset();
            state.LatestReferenceStationarity = null;
            state.RobustReference = null;
            state.BoundaryNoiseModel = null;
            state.BoundaryChangeGate = null;
            state.BoundaryNoChangeActive = false;
            state.ReferenceIsProvisional = false;
            state.ReferenceResetRequested = false;
        }

        var userLockRequested = Volatile.Read(ref state.ManualReferenceLockRequested) != 0;
        if (!userLockRequested)
        {
            // V376: formal-reference stability must be judged in the same trusted
            // block-mean domain consumed by reconstruction. Intra-block frame jitter
            // has already been rejected/averaged and must not restart the quiet window.
            state.LatestReferenceStationarity = state.ReferenceStationarity.Update(
                block.EndSampleIndex / (double)config.AcquisitionSettings.SampleRateHz,
                block.MeanAmplitude208);

            state.ReferenceCandidateFrames.AddRange(block.Frames);
            if (state.ReferenceCandidateFrames.Count > RealtimeContactCalibrationMaximumFrames)
            {
                state.ReferenceCandidateFrames.RemoveRange(
                    0,
                    state.ReferenceCandidateFrames.Count - RealtimeContactCalibrationMaximumFrames);
            }

        }

        var strictGreenCount = state.ReferenceCandidateFrames.Count(EcdCwrRobustReferenceBuilder.IsStrictGreenFrame);
        var priorStrictGreenCount = Volatile.Read(ref state.ReferenceCandidateStrictGreenCount);
        Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, strictGreenCount);
        if (priorStrictGreenCount < RealtimeContactCalibrationMinimumFrames &&
            strictGreenCount >= RealtimeContactCalibrationMinimumFrames)
        {
            callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.RefreshWindowsAndUseCurrentCommand);
        }
        else if (strictGreenCount >= RealtimeContactCalibrationMinimumFrames &&
            strictGreenCount / 25 != priorStrictGreenCount / 25)
        {
            callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.RefreshWindows);
        }

        var stationarity = state.LatestReferenceStationarity;
        var userSelectedCandidateCount = state.PendingSelectedReferenceObservations?.Length ?? 0;
        var candidateCountForPolicy = userLockRequested
            ? userSelectedCandidateCount
            : strictGreenCount;
        var currentStage = state.ReferenceIsProvisional
            ? EcdCwrReferenceTrustStage.Provisional
            : state.ReferenceVoltage208 is null
                ? EcdCwrReferenceTrustStage.None
                : EcdCwrReferenceTrustStage.Formal;
        var action = EcdCwrReferenceLockPolicy.Decide(
            currentStage,
            candidateCountForPolicy,
            stationarity?.CanLock == true,
            RealtimeContactCalibrationMinimumFrames,
            userLockRequested);
        if (action == EcdCwrReferenceLockAction.None)
        {
            if (userLockRequested)
            {
                Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
                callbacks.PublishReferenceSummary(
                    config.SetLabel,
                    $"用户锁定参考未执行：所选窗口质量合格帧 {userSelectedCandidateCount}/{RealtimeContactCalibrationMinimumFrames}；请重新选择完整窗口。");
                callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.UseCurrentCommand);
            }

            if (ShouldUpdateRealtimeStatus(state))
            {
                var status = stationarity?.CanLock != true
                    ? stationarity is null
                        ? "稳定性预热：等待严格全绿物理帧"
                        : FormatReferenceStationarity(stationarity)
                    : "正式稳定性已满足，正在建立参考";
                callbacks.PublishReferenceSummary(
                    config.SetLabel,
                    state.ReferenceIsProvisional
                        ? $"快速预览参考 e{state.ReferenceEpoch} 已启用（低置信）；正式参考后台：{status}；候选全绿帧 {strictGreenCount}。"
                        : $"快速预览预热：{status}；稳定全绿帧 {Math.Min(strictGreenCount, RealtimeContactCalibrationMinimumFrames)}/{RealtimeContactCalibrationMinimumFrames}，达到后立即启动低置信成像。");
                callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.CalibrationState);
            }

            return EcdCwrReferenceLockAction.None;
        }

        try
        {
            if (action == EcdCwrReferenceLockAction.LockUserSelected)
            {
                Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
            }

            var selectedReferenceFrames = action == EcdCwrReferenceLockAction.LockUserSelected
                ? state.PendingSelectedReferenceFrames
                : state.ReferenceCandidateFrames.ToArray();
            var referenceOptions = CreateRobustReferenceOptions(config);
            var robustReference = action == EcdCwrReferenceLockAction.LockUserSelected
                ? new EcdCwrRobustReferenceBuilder().CreateFromObservations(
                    state.PendingSelectedReferenceObservations ?? [],
                    referenceOptions)
                : new EcdCwrRobustReferenceBuilder().Create(
                    selectedReferenceFrames,
                    referenceOptions);
            state.ReferenceVoltage208 = robustReference.Voltage208.ToArray();
            state.ReferenceIsProvisional = action == EcdCwrReferenceLockAction.LockProvisional;
            if (action == EcdCwrReferenceLockAction.LockUserSelected)
            {
                state.ReferenceCandidateFrames.Clear();
                state.ReferenceCandidateFrames.AddRange(selectedReferenceFrames);
                Volatile.Write(
                    ref state.ReferenceCandidateStrictGreenCount,
                    selectedReferenceFrames.Length);
            }

            if (!state.ReferenceIsProvisional)
            {
                Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
            }

            state.RobustReference = state.ReferenceIsProvisional ? null : robustReference;
            ActivateRealtimeReferenceEpoch(
                state,
                block,
                robustReference,
                state.ReferenceIsProvisional
                    ? "provisional_preview"
                    : action == EcdCwrReferenceLockAction.LockUserSelected
                        ? "user_selected"
                        : null);
            state.ActiveReferenceWindow = action == EcdCwrReferenceLockAction.LockUserSelected
                ? state.PendingSelectedReferenceWindow
                : null;
            state.PendingSelectedReferenceWindow = null;
            state.PendingSelectedReferenceObservations = null;
            state.PendingSelectedReferenceFrames = [];
            state.BaselineIntegrityNoiseModel = state.ReferenceIsProvisional
                ? null
                : robustReference.NoiseModel;
            state.BoundaryNoiseModel = state.ReferenceIsProvisional
                ? null
                : robustReference.NoiseModel;
            state.BoundaryChangeGate = state.ReferenceIsProvisional || robustReference.NoiseModel is null
                ? null
                : new EcdCwrBoundaryChangeGate(robustReference.NoiseModel);
            state.BoundaryNoChangeActive = false;
            state.ImageRasterCache.ResetColorScale();
            state.StartupDegradedReferenceAccumulator.Reset();
            state.StartupDegradedReference = null;
            ResetStartupProgress(state);
            state.ReferenceBlockNumber = block.BlockNumber;
            state.ReferenceResetRequested = false;
            state.ReferenceInvalidated = false;
            ResetRealtimeTemporalWindow(state);
            state.DynamicKalmanGeneration++;
            state.DynamicKalmanResetPending = true;
            state.ResetReconstructionCircuitBreaker("reference_locked");
            if (state.ReferenceIsProvisional)
            {
                state.ContactMonitor = null;
                state.ContactCalibration = null;
                state.ExportableContactCalibration = null;
                state.ExportableSessionCalibration = null;
                state.ContactCalibrationFrames.Clear();
                callbacks.PublishProvisionalRoiUnavailable(config.SetLabel);
            }
            else
            {
                contactCalibration.LockBaseline(config, state, block, robustReference);
            }

            lockedReference = robustReference;
            callbacks.PublishReferenceInvalidated(config.SetLabel, false);
            callbacks.PublishReferenceNeutralImage(config.SetLabel, state);
            callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.CalibrationState);
            return action;
        }
        catch (InvalidOperationException ex)
        {
            if (action == EcdCwrReferenceLockAction.LockUserSelected)
            {
                Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
                state.PendingSelectedReferenceWindow = null;
                state.PendingSelectedReferenceObservations = null;
                state.PendingSelectedReferenceFrames = [];
                callbacks.NotifyUi(config.SetLabel, RealtimeReferenceUiChange.UseCurrentCommand);
            }

            if (ShouldUpdateRealtimeStatus(state))
            {
                callbacks.PublishReferenceSummary(
                    config.SetLabel,
                    $"参考预热：正在剔除不稳定帧（候选 {strictGreenCount}），{ex.Message}");
            }

            return EcdCwrReferenceLockAction.None;
        }
    }

    internal static string FormatReferenceStationarity(
        EcdCwrReferenceStationarityResult stationarity)
    {
        var progress =
            $"时间 {Math.Min(stationarity.DurationSeconds, stationarity.RequiredDurationSeconds):F0}/" +
            $"{stationarity.RequiredDurationSeconds:F0}s · 观察 " +
            $"{Math.Min(stationarity.ObservationCount, stationarity.RequiredObservationCount)}/" +
            $"{stationarity.RequiredObservationCount}";
        if (stationarity.QuietWindowRestarted)
        {
            return $"检测到尺度归一化后多通道结构变化，稳定计时已重启（第 {stationarity.QuietWindowRestartCount} 次） · {progress}";
        }

        if (stationarity.State == EcdCwrReferenceStationarityState.Warming)
        {
            var restartEvidence = stationarity.QuietWindowRestartCount > 0
                ? $" · 结构变化后稳定计时已重启 {stationarity.QuietWindowRestartCount} 次"
                : string.Empty;
            var mode = stationarity.CommonScaleNormalizedMode
                ? "公共尺度归一化预热"
                : "稳定性预热";
            return $"{mode} {progress}{restartEvidence}";
        }

        var adaptiveLimits = stationarity.CommonScaleNormalizedMode
            ? $"形状自适应上限≤{stationarity.EffectiveShapeResidualLimitPerMinute * 100.0:F4}%/min；α不参与锁定"
            : $"自适应上限 α≤{stationarity.EffectiveCommonScaleDriftLimitPerMinute * 100.0:F4}%/min，" +
              $"形状≤{stationarity.EffectiveShapeResidualLimitPerMinute * 100.0:F4}%/min";
        var noiseState = stationarity.AdaptiveThresholdLimitedBySafetyCeiling
            ? "噪声估计触及安全封顶"
            : "会话噪声自适应";
        if (!stationarity.MeetsDriftThresholds)
        {
            return $"尺度归一化后形状仍在变化：α仅观测 " +
                $"{stationarity.CommonScaleDriftPerMinute * 100.0:+0.0000;-0.0000;0.0000}%/min，" +
                $"形状 {stationarity.ShapeResidualPerMinute * 100.0:F4}%/min · " +
                $"{adaptiveLimits} · {noiseState} · {progress}";
        }

        return $"稳定确认 {stationarity.ConsecutiveStableUpdates}/" +
            $"{stationarity.RequiredStableUpdates} · " +
            $"α仅观测 {stationarity.CommonScaleDriftPerMinute * 100.0:+0.0000;-0.0000;0.0000}%/min，" +
            $"形状 {stationarity.ShapeResidualPerMinute * 100.0:F4}%/min · " +
            $"{adaptiveLimits} · {noiseState} · {progress}";
    }

    internal static void ActivateRealtimeReferenceEpoch(
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        EcdCwrRobustReference reference,
        string? lockKindOverride = null)
    {
        state.ReferenceReal208 = EitBaselineIntegrityAnalyzer.SelectRetainedMeasurements(reference.FullReal256);
        state.ReferenceImaginary208 = EitBaselineIntegrityAnalyzer.SelectRetainedMeasurements(reference.FullImaginary256);
        state.ReferenceDemodulation = CreateDemodulationFingerprint(block);
        state.BaselineIntegrityNoiseModel = reference.NoiseModel;
        state.ReferenceUsesCommonScaleNormalization = reference.CommonScaleNormalized;
        state.LatestCommonScaleNormalizationFactor = null;
        state.ActivateReferenceEpoch(
            block.BlockNumber,
            block.StartSampleIndex,
            DateTimeOffset.Now,
            lockKindOverride ?? state.PendingReferenceLockKind);
    }

    private static EitDemodulationFingerprint CreateDemodulationFingerprint(
        RealtimeDemodulatedBlock block)
    {
        return new EitDemodulationFingerprint(
            block.EstimatedWindowSamples,
            block.UniformOffsetSamples,
            block.RotationStartChannelOneBased,
            block.RotationDirection);
    }

    internal static EitBaselineIntegrityResult? AnalyzeBaseline(
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!block.IsHighQuality ||
            state.ReferenceVoltage208 is not { Length: DemodulatedFrame.FlattenedMeasurementCount } referenceAmplitude ||
            state.ReferenceReal208 is not { Length: DemodulatedFrame.FlattenedMeasurementCount } referenceReal ||
            state.ReferenceImaginary208 is not { Length: DemodulatedFrame.FlattenedMeasurementCount } referenceImaginary ||
            state.BaselineIntegrityNoiseModel is null ||
            state.ReferenceDemodulation is null)
        {
            return null;
        }

        try
        {
            return RealtimeBaselineIntegrityAnalyzer.Analyze(
                referenceAmplitude,
                referenceReal,
                referenceImaginary,
                block.MeanAmplitude208,
                block.MeanReal208,
                block.MeanImaginary208,
                state.BaselineIntegrityNoiseModel,
                state.ReferenceDemodulation,
                CreateDemodulationFingerprint(block));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }




    internal static string CreateReferenceStatus(RealtimeRunState state)
    {
        if (state.ReferenceInvalidated)
        {
            return "invalidated-contact-recovery";
        }

        if (state.ReferenceResetRequested)
        {
            return "reset-requested";
        }

        if (state.StartupDegradedReference is { } degraded)
        {
            return $"degraded-startup-fault-mask:{string.Join(',', degraded.FaultElectrodes)}";
        }

        if (state.ReferenceIsProvisional)
        {
            return "provisional-preview-low-confidence";
        }

        if (state.ReferenceVoltage208 is null)
        {
            return "unlocked";
        }

        if (string.Equals(state.ActiveReferenceLockKind, "user_selected", StringComparison.Ordinal))
        {
            return state.ReferenceUsesCommonScaleNormalization
                ? "valid-user-selected-common-scale-normalized-model-relative"
                : "valid-user-selected";
        }

        return state.ReferenceUsesCommonScaleNormalization
            ? "valid-common-scale-normalized-model-relative"
            : "valid";
    }

    private static bool ShouldUpdateRealtimeStatus(RealtimeRunState state)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref state.LastStatusTicks);
        var intervalTicks = (long)(RealtimeUiStatusInterval.TotalSeconds * Stopwatch.Frequency);
        if (previous != 0 && now - previous < intervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref state.LastStatusTicks, now, previous) == previous;
    }

    private static void ResetRealtimeTemporalWindow(RealtimeRunState state) => state.TemporalWindow.Reset();
}
