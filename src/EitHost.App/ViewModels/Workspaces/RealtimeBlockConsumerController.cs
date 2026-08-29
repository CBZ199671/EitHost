using System.Globalization;
using System.Diagnostics;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.Baseline;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal delegate EcdCwrReferenceLockAction TryLockRealtimeReferenceCallback(
    RealtimeImagingRunConfig config,
    RealtimeRunState state,
    RealtimeDemodulatedBlock block,
    out EcdCwrRobustReference? lockedReference);

internal delegate void PublishRealtimeQualityAxesCallback(
    string setLabel,
    string? dataQuality = null,
    string? referenceMode = null,
    string? reconstructionQuality = null,
    string? roiReadiness = null);

internal sealed record RealtimeBlockAnalysisCallbacks(
    Action<RealtimeImagingRunConfig, RealtimeRunState> ApplyPendingDiscontinuities,
    Action<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock> PublishRawPreview,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, ElectrodeContactDiagnosticResult?> UpdateContactDiagnostics,
    Action<RealtimeImagingRunConfig, RealtimeRunState, ElectrodeContactDiagnosticResult?> ResetIncompatibleStartupReference,
    Action<RealtimeRunState> ResetStartupProgress,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, Task> AccumulateReferenceCandidates,
    Action<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, ElectrodeContactDiagnosticResult?> UpdateContactCalibration,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, EcdCwrWaveformTemplateDisplayPackage?> BuildTemplateDisplayPackage,
    Func<EcdCwrWaveformTemplateDisplayPackage?, string?> SerializeTemplateDisplayPackage,
    Action<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock> UpdatePairingSelfCheck,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, bool> CommitPreparedReferenceSwitch,
    Func<RealtimeRunState, RealtimeDemodulatedBlock, EitBaselineIntegrityResult?> AnalyzeBaseline,
    Func<ElectrodeContactDiagnosticResult?, RealtimeRunState, string?> SerializeCandidateDiagnostic,
    Func<RealtimeRunState, string> CreateReferenceStatus,
    Func<RealtimeRunState, IReadOnlyList<double>, double?> EstimateCommonScale,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, ElectrodeContactDiagnosticResult?, EcdCwrStartupDegradedReference?> TryLockStartupDegradedReference,
    Action<RealtimeRunState> ResetTemporalWindow,
    TryLockRealtimeReferenceCallback TryLockReference,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulatedBlock, IReadOnlyList<double>, ElectrodeContactDiagnosticResult?, EcdCwrWaveformTemplateDisplayPackage?, RealtimeTemporalSelection?> CreateTemporalSelection,
    Action<RealtimeImagingRunConfig, RealtimeRunState, RealtimeTemporalSelection, EcdCwrBoundaryChangeDecision> HandleNoChange);

internal sealed record RealtimeBlockPresentationCallbacks(
    Action<string> Diagnostic,
    Action<string> QueueLog,
    Action<string> PublishStatus,
    PublishRealtimeQualityAxesCallback PublishQualityAxes,
    Action<string, string> PublishReferenceSummary,
    Action<string, string> PublishReconstructionActivity,
    Action<string, string> PublishBaselineSummary,
    Action<string, RealtimeSignalPreviewSource, string> PublishSignalPreview,
    Action ReferenceCommandsChanged,
    Func<RealtimeImagingRunConfig, RealtimeRunState, string> CreateReferenceModeStatus,
    Func<string, RealtimeRunState, string> ComposeSummary);

internal sealed class RealtimeBlockConsumerController
{
    private const double RealtimeProvisionalReferenceImageQualityCap = 0.55;
    private static readonly TimeSpan DemodPreviewInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(250);
    private readonly RealtimeDerivedPersistenceController derivedPersistence;
    private readonly RealtimeTimingGateController timingGate;
    private readonly RealtimeReconstructionController reconstruction;
    private readonly RealtimeBlockAnalysisCallbacks analysis;
    private readonly RealtimeBlockPresentationCallbacks presentation;
    private readonly EcdCwrDegradedDemodulationSelector degradedSelector = new();

    internal RealtimeBlockConsumerController(
        RealtimeDerivedPersistenceController derivedPersistence,
        RealtimeTimingGateController timingGate,
        RealtimeReconstructionController reconstruction,
        RealtimeBlockAnalysisCallbacks analysis,
        RealtimeBlockPresentationCallbacks presentation)
    {
        this.derivedPersistence = derivedPersistence ?? throw new ArgumentNullException(nameof(derivedPersistence));
        this.timingGate = timingGate ?? throw new ArgumentNullException(nameof(timingGate));
        this.reconstruction = reconstruction ?? throw new ArgumentNullException(nameof(reconstruction));
        this.analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
    }

    internal async Task ConsumeAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulationPipeline pipeline,
        CancellationToken cancellationToken)
    {
        await foreach (var block in pipeline.ReadBlocksAsync(cancellationToken).ConfigureAwait(false))
        {
            analysis.ApplyPendingDiscontinuities(config, state);
            state.BlocksProcessed++;
            await derivedPersistence.PersistDemodulatedBlockAsync(config, state, block).ConfigureAwait(false);
            if (!timingGate.AllowsProcessing(config, state, block))
            {
                continue;
            }
            var recoveredFromLowQuality = block.IsHighQuality && state.ConsecutiveLowQualityBlocks > 0;
            if (block.IsHighQuality)
            {
                state.ConsecutiveLowQualityBlocks = 0;
            }
            else
            {
                state.ConsecutiveLowQualityBlocks++;
            }

            analysis.PublishRawPreview(config, state, block);

            if (state.BlocksProcessed == 1 || state.BlocksProcessed % 100 == 0)
            {
                presentation.Diagnostic(
                    $"{config.SetLabel} demod block={block.BlockNumber} accepted={block.AcceptedFrameCount}/{config.FramesPerBlock} high={block.IsHighQuality} rejects={FormatRejectReasonSummary(block)}");
            }

            var contactResult = analysis.UpdateContactDiagnostics(config, state, block);
            analysis.ResetIncompatibleStartupReference(config, state, contactResult);
            var preReferenceContactClean = IsPreReferenceContactCleanForReference(contactResult);
            var continueProvisionalTarget = ShouldContinueProvisionalTargetReconstruction(
                state.ReferenceIsProvisional,
                preReferenceContactClean,
                block.IsHighQuality);
            if (!preReferenceContactClean)
            {
                state.ReferenceCandidateFrames.Clear();
                Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
                Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
                state.ContactCalibrationFrames.Clear();
                state.ReferenceStationarity.Reset();
                state.LatestReferenceStationarity = null;
                if (!state.ReferenceIsProvisional)
                {
                    state.RobustReference = null;
                }
            }
            else if (state.StartupDegradedReference is null)
            {
                state.StartupDegradedReferenceAccumulator.Reset();
                analysis.ResetStartupProgress(state);
            }

            if (block.IsHighQuality && preReferenceContactClean)
            {
                // V380/V383: candidate history continues while the old formal
                // epoch keeps reconstructing, so relock preparation never pauses ROI.
                await analysis.AccumulateReferenceCandidates(config, state, block).ConfigureAwait(false);
            }
            else
            {
                state.ReferenceCandidateContinuityBreakPending = true;
            }

            EcdCwrWaveformTemplateDisplayPackage? templateDisplayPackage = null;
            string? templateDisplayPayloadJson = null;
            if (block.IsHighQuality)
            {
                state.HighQualityBlocks++;
                state.ConsecutivePairingMismatchBlocks = 0;
                if (state.ReferenceVoltage208 is not null &&
                    !state.ReferenceIsProvisional &&
                    state.StartupDegradedReference is null &&
                    state.ExportableContactCalibration is null)
                {
                    analysis.UpdateContactCalibration(config, state, block, contactResult);
                }
                templateDisplayPackage = analysis.BuildTemplateDisplayPackage(config, state, block);
                templateDisplayPayloadJson = analysis.SerializeTemplateDisplayPackage(templateDisplayPackage);
            }
            else
            {
                state.LowQualityBlocks++;
                analysis.UpdatePairingSelfCheck(config, state, block);
            }

            presentation.PublishQualityAxes(
                config.SetLabel,
                dataQuality: CreateRealtimeDataQualityStatus(block, contactResult),
                referenceMode: presentation.CreateReferenceModeStatus(config, state));

            if (block.IsHighQuality
                && preReferenceContactClean
                && analysis.CommitPreparedReferenceSwitch(config, state, block))
            {
                // The boundary block was diagnosed using the old reference. Do not
                // mix it into the new epoch; the next valid block starts fresh.
                continue;
            }

            if (recoveredFromLowQuality)
            {
                presentation.Diagnostic($"{config.SetLabel} demod recovered block={block.BlockNumber} without restart");
                presentation.QueueLog(
                    $"{DateTime.Now:HH:mm:ss} {config.SetLabel} 解调已自动恢复 block {block.BlockNumber}");
                presentation.PublishStatus($"{config.SetLabel} 解调已自动恢复：采集持续运行，无需重启。");
            }

            var compensatedContactResult = config.EnableOutlierCompensation && !state.ReferenceIsProvisional
                ? contactResult
                : null;
            var activeContactWeights = state.StartupDegradedReference?.MeasurementWeight208 ??
                compensatedContactResult?.MeasurementWeight208;
            var activeWeightPolicy = state.StartupDegradedReference?.WeightPolicyVersion ??
                compensatedContactResult?.WeightPolicyVersion;
            EcdCwrDegradedDemodulationSelection? degradedSelection = null;
            if (!block.IsHighQuality &&
                block.UniformIntegrationStable &&
                state.ReferenceVoltage208 is { Length: RealtimeReconstructionRequest.BoundaryVoltageCount } degradedReference &&
                !state.ReferenceResetRequested)
            {
                degradedSelection = degradedSelector.Select(
                    block,
                    degradedReference,
                    activeContactWeights);
            }

            var baselineIntegrity = analysis.AnalyzeBaseline(state, block);
            if (baselineIntegrity is not null &&
                (block.BlockNumber % 5 == 0 ||
                 !string.Equals(
                     state.LastBaselineClassification,
                     baselineIntegrity.StorageClassification,
                     StringComparison.Ordinal)))
            {
                state.LastBaselineClassification = baselineIntegrity.StorageClassification;
                presentation.PublishBaselineSummary(
                    config.SetLabel,
                    baselineIntegrity.ToChineseSummary(state.ReferenceEpoch));
            }

            if (config.PersistImagingFrames &&
                (block.IsHighQuality || config.PersistAllDemodulatedBlocks))
            {
                var diagnosticPackage = RealtimeFrameDiagnosticFactory.Create(
                    config,
                    state,
                    block,
                    degradedSelection,
                    contactResult,
                    activeContactWeights,
                    activeWeightPolicy,
                    templateDisplayPackage,
                    templateDisplayPayloadJson,
                    baselineIntegrity,
                    analysis.SerializeCandidateDiagnostic(contactResult, state),
                    analysis.CreateReferenceStatus(state),
                    values => analysis.EstimateCommonScale(state, values));
                await derivedPersistence.PersistFrameDiagnosticsAsync(
                    config,
                    state,
                    block,
                    diagnosticPackage.Record,
                    diagnosticPackage.PersistReplayDemodOverride).ConfigureAwait(false);
            }

            if (ShouldUpdateRealtimeDemodPreview(state))
            {
                state.PipelineDroppedBlocks = pipeline.DroppedBlockCount;
                state.PipelineDroppedSampleRows = pipeline.DroppedSampleRows;
                state.PipelineSampleGaps = pipeline.DiscontinuityCount;
                state.PipelineUsbOverflows = pipeline.OverflowCount;
                state.PipelineQueuedSamples = pipeline.QueuedSampleChunkCount;
                state.PipelineQueueHighWater = pipeline.SampleQueueHighWaterMark;
                state.PipelineCadenceRefreshRejected = pipeline.CadenceRefreshRejectedCount;
                UpdateRealtimeDemodulationStability(state, block);
                presentation.PublishSignalPreview(
                    config.SetLabel,
                    CreateRealtimeSignalPreviewSource(state, block, config.FramesPerBlock, config.DifferenceOrientation),
                    presentation.ComposeSummary(config.SetLabel, state));
            }

            if (block.UniformIntegrationStable &&
                state.ReferenceVoltage208 is null &&
                !preReferenceContactClean)
            {
                var startupFaultReference = analysis.TryLockStartupDegradedReference(
                    config,
                    state,
                    block,
                    contactResult);
                if (startupFaultReference is not null)
                {
                    var degradedCapturedBlock = block.BlockNumber;
                    if (config.PersistImagingFrames)
                    {
                        derivedPersistence.PersistReferenceEpoch(
                            config,
                            state,
                            startupFaultReference.RobustReference);
                    }

                    presentation.Diagnostic(
                        $"{config.SetLabel} startup degraded reference locked block={degradedCapturedBlock} faults={string.Join(',', startupFaultReference.FaultElectrodes)} frames={startupFaultReference.RobustReference.FrameCount}");
                    presentation.QueueLog(
                        $"{DateTime.Now:HH:mm:ss} {config.SetLabel} startup degraded reference block {degradedCapturedBlock} faults=[{string.Join(',', startupFaultReference.FaultElectrodes)}] frames={startupFaultReference.RobustReference.FrameCount}");
                    presentation.ReferenceCommandsChanged();
                    continue;
                }
            }

            if (!block.IsHighQuality)
            {
                state.ReferenceCandidateContinuityBreakPending = true;
                analysis.ResetTemporalWindow(state);
                if (ShouldUpdateRealtimeStatus(state))
                {
                    var lowBlock = block.BlockNumber;
                    var accepted = block.AcceptedFrameCount;
                    var rejectSummary = FormatRejectReasonSummary(block);
                    var diagnosticCount = block.DiagnosticAverage?.FiniteMeasurementCount ?? 0;
                    var trustedCount = degradedSelection?.TrustedMeasurementCount
                        ?? block.TrustedPartialAverage?.FiniteMeasurementCount
                        ?? 0;
                    var degradedStatus = degradedSelection?.Status
                        ?? $"等待已锁定参考后评估健康通道：健康 {trustedCount}/208，诊断 {diagnosticCount}/208";
                    presentation.PublishReconstructionActivity(
                        config.SetLabel,
                        $"诊断解调（低置信）block {lowBlock}：strict {accepted}/{config.FramesPerBlock} · {degradedStatus} · rejects={rejectSummary}");
                    presentation.QueueLog(
                        $"{DateTime.Now:HH:mm:ss} {config.SetLabel} diagnostic demod block {lowBlock} strict={accepted}/{config.FramesPerBlock} trusted={trustedCount}/208 diagnostic={diagnosticCount}/208 rejects={rejectSummary}");
                    presentation.PublishStatus(
                        $"{config.SetLabel} 采集运行；诊断解调（低置信）：strict {accepted}/{config.FramesPerBlock}，健康 {trustedCount}/208，诊断 {diagnosticCount}/208。");
                }

                TryScheduleDegradedRealtimeReconstruction(
                    config,
                    state,
                    block,
                    degradedSelection,
                    contactResult,
                    cancellationToken);
                continue;
            }

            if (state.ReconstructionSuspended)
            {
                analysis.ResetTemporalWindow(state);
                state.SkippedReconstructionBlocks++;
                if (ShouldUpdateRealtimeStatus(state))
                {
                    var skippedBlock = block.BlockNumber;
                    presentation.PublishReconstructionActivity(config.SetLabel, $"重构状态：已暂停 · 采集/解调继续 · 跳过 block {skippedBlock}");
                }

                continue;
            }

            var target = block.MeanAmplitude208;
            if (state.ReferenceVoltage208 is null ||
                state.ReferenceIsProvisional ||
                state.ReferenceResetRequested)
            {
                if (state.ReferenceVoltage208 is null || state.ReferenceResetRequested)
                {
                    analysis.ResetTemporalWindow(state);
                }

                if (!preReferenceContactClean && !continueProvisionalTarget)
                {
                    if (ShouldUpdateRealtimeStatus(state))
                    {
                        var candidates = contactResult!.States
                            .Select((contactState, electrode) => (contactState, electrode))
                            .Where(item => item.contactState != ElectrodeContactState.Green)
                            .Select(item => item.electrode + 1)
                            .ToArray();
                        presentation.PublishReferenceSummary(
                            config.SetLabel,
                            $"参考预热：等待稳定故障集合确诊或恢复全绿；当前电极 [{string.Join(',', candidates)}]。");
                    }

                    continue;
                }

                if (continueProvisionalTarget && ShouldUpdateRealtimeStatus(state))
                {
                    presentation.PublishReferenceSummary(
                        config.SetLabel,
                        $"快速预览参考 e{state.ReferenceEpoch} 保持不变；当前非全绿结果仅作诊断，" +
                        "低置信目标重构继续，正式参考升级暂停。");
                }

                EcdCwrRobustReference? lockedReference = null;
                var referenceAction = preReferenceContactClean
                    ? analysis.TryLockReference(
                        config,
                        state,
                        block,
                        out lockedReference)
                    : EcdCwrReferenceLockAction.None;
                if (referenceAction == EcdCwrReferenceLockAction.None &&
                    state.ReferenceVoltage208 is null)
                {
                    continue;
                }

                if (referenceAction != EcdCwrReferenceLockAction.None)
                {
                    var capturedBlock = block.BlockNumber;
                    var referenceFrames = lockedReference?.FrameCount ?? 0;
                    var referenceInputFrames = state.ActiveReferenceWindow?.FrameCount ?? referenceFrames;
                    var referenceRejectedFrames = lockedReference?.RejectedFrameCount ?? 0;
                    if (config.PersistImagingFrames && lockedReference is not null)
                    {
                        derivedPersistence.PersistReferenceEpoch(config, state, lockedReference);
                    }

                    var provisional = referenceAction == EcdCwrReferenceLockAction.LockProvisional;
                    var formal = referenceAction == EcdCwrReferenceLockAction.LockFormal;
                    var userSelected = referenceAction == EcdCwrReferenceLockAction.LockUserSelected;
                    var stage = provisional
                        ? "provisional preview"
                        : userSelected
                            ? "user selected"
                            : "formal";
                    presentation.Diagnostic(
                        $"{config.SetLabel} {stage} robust reference locked epoch={state.ReferenceEpoch} block={capturedBlock} frames={referenceFrames}");
                    presentation.PublishReferenceSummary(
                        config.SetLabel,
                        provisional
                            ? $"快速预览参考 e{state.ReferenceEpoch}：已用 {referenceFrames} 个全绿帧锁定；低置信成像立即启动，正式参考继续后台验证。"
                            : userSelected
                                ? $"用户锁定参考 e{state.ReferenceEpoch}：点击前自动/高级所选区间输入 {referenceInputFrames} 帧，" +
                                  $"稳健保留 {referenceFrames} 帧，剔除 {lockedReference!.RejectedFrameCount} 帧；" +
                                  $"对象慢变与稳定性仅作提示，正常置信度成像与 ROI 已启用。"
                            : formal
                                ? $"正式参考 e{state.ReferenceEpoch}：形状稳定性已通过，已用 {referenceFrames} 个稳定全绿帧升级；公共尺度归一化相对成像已启用，绝对电导率仍未标定。"
                                : throw new InvalidOperationException("Unexpected realtime reference transition."));
                    presentation.PublishQualityAxes(
                        config.SetLabel,
                        referenceMode: presentation.CreateReferenceModeStatus(config, state),
                        reconstructionQuality: state.ReferenceIsProvisional
                            ? "重构质量：快速预览 · 参考模式临时"
                            : "重构质量：等待新参考后的首个目标",
                        roiReadiness: state.ReferenceIsProvisional
                            ? "ROI 就绪：否 · 快速预览参考"
                            : "ROI 就绪：待下一次成功重构");
                    presentation.QueueLog(
                        $"{DateTime.Now:HH:mm:ss} {config.SetLabel} {stage} robust Huber reference epoch {state.ReferenceEpoch} block {capturedBlock} input={referenceInputFrames} retained={referenceFrames} rejected={referenceRejectedFrames}");
                    presentation.ReferenceCommandsChanged();
                    continue;
                }
            }

            var reconstructionTarget = NormalizeRealtimeTargetIfEnabled(state, target);
            var temporalSelection = analysis.CreateTemporalSelection(
                config,
                state,
                block,
                reconstructionTarget,
                contactResult,
                templateDisplayPackage);
            if (temporalSelection is null)
            {
                if (ShouldUpdateRealtimeStatus(state))
                {
                    var continuity = state.SampleContinuity.Snapshot();
                    var waitingStatus = state.SampleContinuityRecoveryPending
                        ? $"采样连续性恢复：等待连续高质量块 {state.TemporalWindow.Count}/5（显示延迟 2 块）" +
                          $" · gap={continuity.TotalDiscontinuities} · 丢行={continuity.TotalMissingSampleRows}"
                        : $"时序去毛刺：等待连续高质量块 {state.TemporalWindow.Count}/5（显示延迟 2 块）";
                    presentation.PublishReconstructionActivity(
                        config.SetLabel,
                        waitingStatus);
                }

                continue;
            }

            if (state.SampleContinuityRecoveryPending)
            {
                state.SampleContinuityRecoveryPending = false;
                var continuity = state.SampleContinuity.Snapshot();
                presentation.Diagnostic(
                    $"{config.SetLabel} sample continuity recovered block={temporalSelection.Block.BlockNumber} " +
                    $"after centered5; totalGaps={continuity.TotalDiscontinuities} " +
                    $"totalMissingRows={continuity.TotalMissingSampleRows} usbOverflows={continuity.TotalUsbOverflows}");
                presentation.QueueLog(
                    $"{DateTime.Now:HH:mm:ss} {config.SetLabel} 采样连续性已恢复，5 个连续高质量块后恢复逆问题重构");
            }

            EcdCwrBoundaryChangeDecision? boundaryChangeDecision = null;
            if (state.BoundaryNoiseModel is { } boundaryNoiseModel &&
                state.BoundaryChangeGate is { } boundaryChangeGate)
            {
                boundaryChangeDecision = boundaryChangeGate.Evaluate(temporalSelection.Target);
                temporalSelection = temporalSelection with
                {
                    MeasurementWeights = CombineMeasurementWeights(
                        temporalSelection.MeasurementWeights,
                        boundaryNoiseModel.PrecisionWeight208),
                    WeightPolicyVersion = $"{temporalSelection.WeightPolicyVersion}+boundary-noise-precision-v1"
                };
            }

            if (temporalSelection.TemporalResult?.IsGlobalIsolatedSpike == true)
            {
                state.SkippedReconstructionBlocks++;
                var isolatedCount = temporalSelection.TemporalResult.IsolatedChannelCount;
                presentation.PublishReconstructionActivity(
                    config.SetLabel,
                    $"时序去毛刺：block {temporalSelection.Block.BlockNumber} 为孤立全局尖峰（{isolatedCount}/208），保持上一幅图。");
                presentation.Diagnostic(
                    $"{config.SetLabel} temporal global spike block={temporalSelection.Block.BlockNumber} isolated={isolatedCount}/208");
                continue;
            }

            var boundaryDisposition = boundaryChangeDecision is null
                ? null
                : EcdCwrBoundaryChangeReconstructionDisposition.FromDecision(boundaryChangeDecision);
            if (boundaryDisposition is { RenderNeutralTrustedImage: true } &&
                boundaryChangeDecision is { } noChangeDecision)
            {
                analysis.HandleNoChange(config, state, temporalSelection, noChangeDecision);
                if (!boundaryDisposition.ScheduleInverseReconstruction)
                {
                    _ = boundaryDisposition.CreateTrustedTarget(
                        state.ReferenceVoltage208!,
                        temporalSelection.Target);
                    state.SkippedReconstructionBlocks++;
                    continue;
                }
            }
            else if (state.BoundaryNoChangeActive)
            {
                state.BoundaryNoChangeActive = false;
                state.DynamicKalmanGeneration++;
                state.DynamicKalmanResetPending = true;
                state.ImageRasterCache.ResetColorScale();
            }
            if (state.ReconstructionTask is { IsCompleted: false })
            {
                state.SkippedReconstructionBlocks++;
                if (ShouldUpdateRealtimeStatus(state))
                {
                    var busyBlock = temporalSelection.Block.BlockNumber;
                    presentation.PublishReconstructionActivity(config.SetLabel, $"重构状态：后端忙 · 跳过 block {busyBlock}");
                }

                continue;
            }

            var reference = state.ReferenceVoltage208!.ToArray();
            var selectedBlock = temporalSelection.Block;
            var scheduledBlock = selectedBlock.BlockNumber;
            var timeout = RealtimeReconstructionController.GetRequestTimeout(Volatile.Read(ref state.ReconstructionFrames));
            var isWarmup = timeout == RealtimeReconstructionController.WarmupTimeout;
            if (ShouldLogRealtimeBlockMilestone(scheduledBlock))
            {
                presentation.Diagnostic(
                    $"{config.SetLabel} schedule reconstruction block={scheduledBlock} timeout={timeout.TotalSeconds:F0}s");
            }

            if (ShouldUpdateRealtimeStatus(state))
            {
                var commonScaleStatus = state.ReferenceUsesCommonScaleNormalization &&
                    state.LatestCommonScaleNormalizationFactor is { } commonScale
                        ? $" · 公共尺度 α={commonScale:F6}"
                        : string.Empty;
                presentation.PublishReconstructionActivity(config.SetLabel, isWarmup
                    ? $"重构状态：首次预热 · 最长 {timeout.TotalSeconds:F0}s{commonScaleStatus}"
                    : $"重构状态：连续运行 · {config.ReconstructionRoute}{commonScaleStatus}");
            }

            var startupDegradedReference = state.StartupDegradedReference;
            var provisionalPreview = state.ReferenceIsProvisional;
            if (!state.TryScheduleReconstruction(
                    () => reconstruction.ExecuteAsync(
                        config,
                        state,
                        selectedBlock,
                        reference,
                        temporalSelection.Target,
                        temporalSelection.MeasurementWeights,
                        temporalSelection.WeightPolicyVersion,
                        boundaryDisposition is not { HoldDynamicState: true } &&
                            temporalSelection.TemporalResult?.IsolatedChannelCount > 0,
                        temporalSelection.ContactResult,
                        temporalSelection.TemplateDisplayPackage,
                        boundaryChangeDecision,
                        degradedDemodulation: startupDegradedReference is not null || provisionalPreview,
                        imageQualityCap: provisionalPreview
                            ? RealtimeProvisionalReferenceImageQualityCap
                            : startupDegradedReference?.ImageQualityCap,
                        degradedStatus: provisionalPreview
                            ? "快速预览参考：正式稳定性仍在后台验证"
                            : startupDegradedReference is null
                                ? null
                                : $"故障启动加权重构：屏蔽电极 [{string.Join(',', startupDegradedReference.FaultElectrodes)}]",
                        publishRoiMeasurement: !provisionalPreview,
                        cancellationToken: cancellationToken),
                    out var scheduledReconstruction))
            {
                state.SkippedReconstructionBlocks++;
                continue;
            }

        }
    }

    private void TryScheduleDegradedRealtimeReconstruction(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        EcdCwrDegradedDemodulationSelection? selection,
        ElectrodeContactDiagnosticResult? contactResult,
        CancellationToken cancellationToken)
    {
        if (selection is null || !selection.CanReconstruct)
        {
            state.SkippedReconstructionBlocks++;
            return;
        }

        if (state.ReconstructionSuspended ||
            state.ReferenceVoltage208 is not { Length: RealtimeReconstructionRequest.BoundaryVoltageCount } reference)
        {
            state.SkippedReconstructionBlocks++;
            return;
        }

        if (state.ReconstructionTask is { IsCompleted: false })
        {
            state.SkippedReconstructionBlocks++;
            return;
        }

        var normalizedTarget = NormalizeRealtimeTargetIfEnabled(state, selection.TargetVoltage208);
        var weightPolicyVersion = state.ReferenceUsesCommonScaleNormalization
            ? $"{selection.WeightPolicyVersion}+{EcdCwrCommonScaleNormalizer.PolicyVersion}"
            : selection.WeightPolicyVersion;

        // V256: diagnostic fallback never enters the persistent Kalman state.
        state.DynamicKalmanResetPending = true;
        presentation.Diagnostic(
            $"{config.SetLabel} schedule degraded reconstruction block={block.BlockNumber} trusted={selection.TrustedMeasurementCount}/208 effective={selection.EffectiveMeasurementCount}/208 rows={selection.TrustedStimulationCount}/16");
        if (!state.TryScheduleReconstruction(
                () => reconstruction.ExecuteAsync(
                    config,
                    state,
                    block,
                    reference.ToArray(),
                    normalizedTarget,
                    selection.MeasurementWeight208,
                    weightPolicyVersion,
                    temporalInnovationCandidate: false,
                    contactResult: contactResult,
                    templateDisplayPackage: null,
                    boundaryChangeDecision: null,
                    degradedDemodulation: true,
                    imageQualityCap: selection.ImageQualityCap,
                    degradedStatus: selection.Status,
                    publishRoiMeasurement: !state.ReferenceIsProvisional,
                    cancellationToken: cancellationToken),
                out var scheduledReconstruction))
        {
            state.SkippedReconstructionBlocks++;
            return;
        }

    }



    internal static bool IsPreReferenceContactCleanForReference(ElectrodeContactDiagnosticResult? contactResult) =>
        contactResult is null || !contactResult.PreReferenceOnly ||
        contactResult.States.All(state => state == ElectrodeContactState.Green);

    internal static bool ShouldContinueProvisionalTargetReconstruction(
        bool referenceIsProvisional,
        bool preReferenceContactClean,
        bool blockIsHighQuality) =>
        referenceIsProvisional && !preReferenceContactClean && blockIsHighQuality;

    private static double[] NormalizeRealtimeTargetIfEnabled(
        RealtimeRunState state,
        IReadOnlyList<double> target)
    {
        if (!state.ReferenceUsesCommonScaleNormalization ||
            state.ReferenceVoltage208 is not { Length: RealtimeReconstructionRequest.BoundaryVoltageCount } reference)
        {
            state.LatestCommonScaleNormalizationFactor = null;
            return target.ToArray();
        }

        var normalized = EcdCwrCommonScaleNormalizer.NormalizeVector(reference, target);
        state.LatestCommonScaleNormalizationFactor = normalized.CommonScale;
        return normalized.Values;
    }

    private static double[] CombineMeasurementWeights(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second)
    {
        if (first.Count != RealtimeReconstructionRequest.BoundaryVoltageCount ||
            second.Count != RealtimeReconstructionRequest.BoundaryVoltageCount)
        {
            throw new ArgumentException("Realtime measurement weights must both contain 208 values.");
        }

        var combined = new double[first.Count];
        for (var index = 0; index < combined.Length; index++)
        {
            combined[index] = Math.Min(first[index], second[index]);
        }

        return combined;
    }

    private static bool ShouldUpdateRealtimeDemodPreview(RealtimeRunState state) =>
        ShouldUpdateUi(ref state.LastDemodPreviewTicks, DemodPreviewInterval);

    private static bool ShouldUpdateRealtimeStatus(RealtimeRunState state) =>
        ShouldUpdateUi(ref state.LastStatusTicks, StatusInterval);

    private static bool ShouldUpdateUi(ref long lastTicks, TimeSpan interval)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref lastTicks);
        var intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        if (previous != 0 && now - previous < intervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref lastTicks, now, previous) == previous;
    }

    private static bool ShouldLogRealtimeBlockMilestone(int blockNumber) =>
        blockNumber <= 5 || blockNumber % 100 == 0;

    private static string CreateRealtimeDataQualityStatus(
        RealtimeDemodulatedBlock block,
        ElectrodeContactDiagnosticResult? contactResult)
    {
        if (!block.UniformIntegrationStable)
        {
            return "数据质量：不可用 · 解调积分不稳定";
        }

        if (!block.IsHighQuality)
        {
            return $"数据质量：受限 · strict {block.AcceptedFrameCount} · rejects {block.RejectedFrameCount}";
        }

        var contact = contactResult is null
            ? "接触未评估"
            : contactResult.States.All(state => state == ElectrodeContactState.Green)
                ? "接触全绿"
                : $"接触告警 {contactResult.RedLikeElectrodeCount}";
        return $"数据质量：可信 · strict {block.AcceptedFrameCount} · {contact}";
    }

    private static string FormatRejectReasonSummary(RealtimeDemodulatedBlock block)
    {
        if (!block.UniformIntegrationStable)
        {
            return FormattableString.Invariant(
                $"UniformIntegrationUnstable={block.UniformIntegrationInstability:G4}");
        }

        var rejectedWindows = block.Frames
            .SelectMany(frame => frame.WindowQualities)
            .Where(quality => quality.Rejected)
            .ToArray();
        if (rejectedWindows.Length == 0)
        {
            return "0";
        }

        var reasonSummary = string.Join(",", rejectedWindows
            .GroupBy(quality => quality.RejectReason)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString())
            .Select(group => $"{group.Key}={group.Count()}"));
        var firstRejected = rejectedWindows[0];
        var top3 = firstRejected.Top3Channels.Length == 0
            ? "-"
            : string.Join("/", firstRejected.Top3Channels.Select(channel =>
                (channel + 1).ToString(CultureInfo.InvariantCulture)));
        return FormattableString.Invariant(
            $"{reasonSummary}; first w{firstRejected.WindowIndex + 1} exp={firstRejected.ExpectedReferenceChannel + 1} top1={firstRejected.DetectedTop1Channel + 1} top3={top3} pbg={firstRejected.PeakToBackgroundRatio:G3}");
    }

    private static RealtimeSignalPreviewSource CreateRealtimeSignalPreviewSource(
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        int framesPerBlock,
        string differenceOrientation)
    {
        var diagnosticAverage = !block.IsHighQuality ? block.DiagnosticAverage : null;
        var useDiagnosticAverage = diagnosticAverage is { FiniteMeasurementCount: > 0 };
        return new RealtimeSignalPreviewSource(
            block.BlockNumber,
            block.AcceptedFrameCount,
            framesPerBlock,
            block.QualityWeight,
            useDiagnosticAverage ? diagnosticAverage!.FlattenAmplitudesRowMajor() : block.MeanAmplitude208.ToArray(),
            useDiagnosticAverage ? diagnosticAverage!.FlattenRealRowMajor() : block.MeanReal208.ToArray(),
            useDiagnosticAverage ? diagnosticAverage!.FlattenImaginaryRowMajor() : block.MeanImaginary208.ToArray(),
            state.ReferenceVoltage208?.ToArray(),
            differenceOrientation,
            DiagnosticMode: useDiagnosticAverage,
            TrustedMeasurementCount: block.TrustedPartialAverage?.FiniteMeasurementCount ?? block.TrustedMeasurementCount,
            DiagnosticMeasurementCount: block.DiagnosticAverage?.FiniteMeasurementCount ?? block.DiagnosticMeasurementCount,
            RejectSummary: useDiagnosticAverage ? FormatRejectReasonSummary(block) : null,
            ReferenceIsProvisional: state.ReferenceIsProvisional,
            StepStability: state.LatestDemodulationStepStability);
    }

    private static void UpdateRealtimeDemodulationStability(
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!block.IsHighQuality)
        {
            state.PreviousDemodulationReal208 = null;
            state.PreviousDemodulationImaginary208 = null;
            state.PreviousDemodulationBlockNumber = 0;
            state.LatestDemodulationStepStability = null;
            return;
        }

        var currentReal = block.MeanReal208;
        var currentImaginary = block.MeanImaginary208;
        state.LatestDemodulationStepStability =
            state.PreviousDemodulationReal208 is { } previousReal &&
            state.PreviousDemodulationImaginary208 is { } previousImaginary &&
            block.BlockNumber == state.PreviousDemodulationBlockNumber + 1
                ? RealtimeDemodulationStabilityAnalyzer.AnalyzeStep(
                    previousReal,
                    previousImaginary,
                    currentReal,
                    currentImaginary)
                : null;
        state.PreviousDemodulationReal208 = currentReal;
        state.PreviousDemodulationImaginary208 = currentImaginary;
        state.PreviousDemodulationBlockNumber = block.BlockNumber;
    }
}
