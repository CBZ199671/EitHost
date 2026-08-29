using System.Diagnostics;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeContactDiagnosticCallbacks(
    Action<string> Diagnostic,
    Func<RealtimeImagingRunConfig, RealtimeRunState, string, bool> CaptureRawRing,
    Action<string, string> PublishContactSummary,
    Action<string, string> PublishMultiFrequencySummary,
    Action<string, bool> PublishReferenceInvalidated,
    Action<string, string> PublishBoundaryFitUnavailable,
    Action<string, string> PublishReferenceSummary,
    Action<string, string> PublishReconstructionActivity,
    Action<string, int, ElectrodeContactDiagnosticResult> PublishPreReferencePreview,
    Action<string> QueueLog,
    Action<string> InvalidateCalibrationState);

internal sealed class RealtimeContactDiagnosticController
{
    private static readonly TimeSpan RealtimeContactDiagnosticLogInterval = TimeSpan.FromSeconds(1);
    private readonly RealtimeContactDiagnosticCallbacks callbacks;

    internal RealtimeContactDiagnosticController(RealtimeContactDiagnosticCallbacks callbacks)
    {
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal static string CreateMultiFrequencySummary(RealtimeImagingRunConfig config)
    {
        return CreateRealtimeMultiFrequencySummary(
            config.DacSettings.ActualFrequencyHz,
            config.UseFrequencyDivisionLockIn,
            config.InterferenceFrequencyHz);
    }

    internal static string CreateRealtimeMultiFrequencySummary(
        double primaryFrequencyHz,
        bool useFrequencyDivisionLockIn,
        IReadOnlyList<double> interferenceFrequencyHz)
    {
        if (!useFrequencyDivisionLockIn)
        {
            return $"多频证据：单频 {primaryFrequencyHz:g} Hz，证据 E 未启用。";
        }

        if (interferenceFrequencyHz.Count == 0)
        {
            return $"多频证据：频分锁相已启用，主频 {primaryFrequencyHz:g} Hz；暂无其他频点可融合。";
        }

        var frequencyList = string.Join(
            "/",
            interferenceFrequencyHz.Select(frequency => FormattableString.Invariant($"{frequency:g}")));
        return $"多频证据：频分锁相已启用，主频 {primaryFrequencyHz:g} Hz，旁路频点 {frequencyList} Hz；证据 E 等待实时融合评分。";
    }

    private static string CreateRealtimeMultiFrequencyFusionSummary(
        RealtimeImagingRunConfig config,
        EcdCwrMultiFrequencyScoreFusionResult? fusion,
        int peerFrameCount)
    {
        if (!config.UseFrequencyDivisionLockIn)
        {
            return CreateMultiFrequencySummary(config);
        }

        if (config.InterferenceFrequencyHz.Count == 0)
        {
            return CreateMultiFrequencySummary(config);
        }

        if (fusion is null)
        {
            return peerFrameCount == 0
                ? $"多频证据：频分锁相已启用，主频 {config.DacSettings.ActualFrequencyHz:g} Hz；等待旁路频点投影。"
                : $"多频证据：已读取 {peerFrameCount} 个旁路频点，等待有效证据 E 融合评分。";
        }

        var bestElectrode = Enumerable.Range(0, fusion.FusedScores.Length)
            .OrderByDescending(index => fusion.FusedScores[index])
            .ThenBy(index => index)
            .FirstOrDefault();
        var baseScore = fusion.BaseScores[bestElectrode];
        var fusedScore = fusion.FusedScores[bestElectrode];
        var boost = baseScore <= 1.0e-12 ? 1.0 : fusedScore / baseScore;
        var frequencies = string.Join(
            "/",
            fusion.Consistency.FrequenciesHz.Select(frequency => FormattableString.Invariant($"{frequency:g}")));
        var activeElectrodes = fusion.Consistency.ActiveFrequencyFraction.Count(value => value >= 0.5);
        return $"多频证据：证据 E 已融合 {fusion.Consistency.FrequenciesHz.Length} 频点({frequencies} Hz)，活跃电极 {activeElectrodes}/16，E{bestElectrode + 1} {baseScore:G3}->{fusedScore:G3}，boost×{boost:G3}。";
    }

    internal static string CreateAdaptiveContactThresholdMode(EcdCwrAdaptiveContactProfileMatch? match)
    {
        return match?.Mode switch
        {
            EcdCwrAdaptiveContactProfileMatchMode.Exact => "adaptive-guarded-exact",
            EcdCwrAdaptiveContactProfileMatchMode.Mismatch => "adaptive-shadow-mismatch-legacy",
            _ => "uncalibrated-legacy"
        };
    }

    internal static string FormatAdaptiveContactThresholdStatus(RealtimeRunState state)
    {
        var match = state.AdaptiveContactProfileMatch;
        if (match?.Profile is { } profile)
        {
            return $"自适应阈值已条件启用 profile={profile.ProfileId} Y={profile.Thresholds.YellowEntry:F2} R={profile.Thresholds.RedEntry:F2}/{profile.Thresholds.RedRelease:F2}；仅持久红证据接管，在线更新冻结";
        }

        return match?.Mode == EcdCwrAdaptiveContactProfileMatchMode.Mismatch
            ? $"自适应阈值配置不匹配，继续旧阈值（{match.Reason}）"
            : $"自适应阈值未标定，继续旧阈值（{match?.Reason ?? "尚未建立工况指纹"}）";
    }

    private ElectrodeContactDiagnosticResult? UpdateAdaptiveShadowContactDiagnostics(
        RealtimeRunState state,
        double[,]? fullAmplitudes256,
        IReadOnlyList<DemodulatedWindowQuality> windowQualities,
        int strictAcceptedFrameCount)
    {
        if (state.AdaptiveShadowContactMonitor is null)
        {
            return null;
        }

        try
        {
            return state.AdaptiveShadowContactMonitor.Update(
                fullAmplitudes256,
                windowQualities,
                strictAcceptedFrameCount);
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{state.SetLabel} adaptive contact shadow failed: {ex.Message}");
            return null;
        }
    }

    internal static ElectrodeContactDiagnosticResult SelectGuardedAdaptivePreReferenceResult(
        ElectrodeContactDiagnosticResult legacyResult,
        ElectrodeContactDiagnosticResult? adaptiveResult,
        EcdCwrAdaptiveContactProfileMatch? profileMatch)
    {
        if (profileMatch?.Calibrated != true ||
            adaptiveResult is null ||
            !legacyResult.PreReferenceOnly ||
            !adaptiveResult.PreReferenceOnly ||
            legacyResult.SystemLevel ||
            adaptiveResult.SystemLevel ||
            legacyResult.PreReferenceConsensus is not { } legacyConsensus ||
            adaptiveResult.PreReferenceConsensus is not { } adaptiveConsensus)
        {
            return legacyResult;
        }

        var adaptiveConfirmed = adaptiveConsensus.Confirmed;
        var legacySafetyMask = legacyConsensus.SafetyMask ?? legacyConsensus.Confirmed;
        var adaptiveSafetyMask = adaptiveConsensus.SafetyMask ?? adaptiveConsensus.Confirmed;
        if (adaptiveConfirmed.Length != ElectrodeContactBaseline.ElectrodeCount ||
            legacySafetyMask.Length != ElectrodeContactBaseline.ElectrodeCount ||
            adaptiveSafetyMask.Length != ElectrodeContactBaseline.ElectrodeCount ||
            !adaptiveConfirmed.Any(selected => selected))
        {
            return legacyResult;
        }

        for (var electrode = 0; electrode < ElectrodeContactBaseline.ElectrodeCount; electrode++)
        {
            if (legacySafetyMask[electrode] && !adaptiveSafetyMask[electrode])
            {
                return legacyResult;
            }

            if (adaptiveConfirmed[electrode] &&
                (legacyResult.States[electrode] == ElectrodeContactState.Green ||
                    adaptiveResult.States[electrode] != ElectrodeContactState.Red))
            {
                return legacyResult;
            }
        }

        return adaptiveResult;
    }

    internal ElectrodeContactDiagnosticResult? UpdateDiagnostics(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!block.UniformIntegrationStable)
        {
            state.ContactDiagnosticsSkippedCount++;
            return null;
        }

        if (!config.EnableOutlierDetection)
        {
            state.LatestContactResult = null;
            return null;
        }

        try
        {
            var useDiagnosticAverage = !block.IsHighQuality &&
                block.DiagnosticAverage is { FiniteFullMeasurementCount: > 0 };
            var qualities = block.Frames
                .SelectMany(frame => frame.WindowQualities)
                .ToArray();
            var diagnosticModeChanged = state.LastContactDiagnosticWasDegraded != useDiagnosticAverage;
            var preReferenceMode = state.ContactMonitor is null;
            var shouldRunDiagnostics = RealtimeContactDiagnosticAlgorithms.ShouldRun(
                state,
                qualities,
                diagnosticModeChanged);
            if (!preReferenceMode && !shouldRunDiagnostics)
            {
                state.ContactDiagnosticsSkippedCount++;
                return state.LatestContactResult;
            }

            if (preReferenceMode)
            {
                var startupStarted = Stopwatch.GetTimestamp();
                var startupAmplitudes = useDiagnosticAverage
                    ? block.DiagnosticAverage!.FullAmplitudes
                    : block.Average.FullAmplitudes;
                var startupPreviousSevere = IsSevereContactResult(state.LatestContactResult);
                var legacyStartupResult = state.PreReferenceContactMonitor.Update(
                    startupAmplitudes,
                    qualities,
                    block.AcceptedFrameCount);
                state.LatestAdaptiveShadowContactResult = UpdateAdaptiveShadowContactDiagnostics(
                    state,
                    startupAmplitudes,
                    qualities,
                    block.AcceptedFrameCount);
                var startupResult = SelectGuardedAdaptivePreReferenceResult(
                    legacyStartupResult,
                    state.LatestAdaptiveShadowContactResult,
                    state.AdaptiveContactProfileMatch);
                state.LastContactDiagnosticWasDegraded = useDiagnosticAverage;
                state.LastContactDiagnosticElapsedMs = Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds;
                state.MaxContactDiagnosticElapsedMs = Math.Max(
                    state.MaxContactDiagnosticElapsedMs,
                    state.LastContactDiagnosticElapsedMs);
                state.ContactDiagnosticsRunCount++;
                state.LatestContactResult = startupResult;
                var startupSevere = IsSevereContactResult(startupResult);
                if (config.StoragePolicy.KeepRawRingBuffer && startupSevere && !startupPreviousSevere)
                {
                    callbacks.CaptureRawRing(
                        config,
                        state,
                        $"pre-reference-contact-event-block-{block.BlockNumber}");
                }

                if (shouldRunDiagnostics)
                {
                    var coverageSuffix = useDiagnosticAverage
                        ? $" · 诊断解调 {block.DiagnosticAverage!.FiniteMeasurementCount}/208"
                        : string.Empty;
                    var startupSummary = state.ReferenceIsProvisional
                        ? startupResult.Summary.Replace(
                            "仅诊断、未重构",
                            "快速预览低置信重构",
                            StringComparison.Ordinal)
                        : state.StartupDegradedReference is null
                            ? startupResult.Summary
                            : startupResult.Summary.Replace(
                                "仅诊断、未重构",
                                "降级加权重构",
                                StringComparison.Ordinal);
                    startupSummary = $"{startupSummary} · {FormatAdaptiveContactThresholdStatus(state)}";
                    callbacks.PublishContactSummary(
                        config.SetLabel,
                        $"{startupSummary}{coverageSuffix}");
                    if (state.StartupDegradedReference is null && !state.ReferenceIsProvisional)
                    {
                        callbacks.PublishPreReferencePreview(
                            config.SetLabel,
                            block.BlockNumber,
                            startupResult);
                    }
                    if (ShouldLogRealtimeContactState(state, startupResult, block.BlockNumber))
                    {
                        callbacks.Diagnostic($"{config.SetLabel} {startupResult.Summary}");
                    }
                }

                return startupResult;
            }

            var contactMonitor = state.ContactMonitor ??
                throw new InvalidOperationException("Contact monitor became unavailable during diagnostics.");
            var evidenceReal256 = useDiagnosticAverage
                ? block.DiagnosticAverage!.FullRealComponents
                : block.Average.FullRealComponents;
            var evidenceImaginary256 = useDiagnosticAverage
                ? block.DiagnosticAverage!.FullImaginaryComponents
                : block.Average.FullImaginaryComponents;
            if (evidenceReal256 is null || evidenceImaginary256 is null)
            {
                return null;
            }

            var previousSevere = IsSevereContactResult(state.LatestContactResult);
            var diagnosticStarted = Stopwatch.GetTimestamp();
            var peerFrequencyEvidence = RealtimeContactDiagnosticAlgorithms.BuildPeerFrequencyEvidence(config, block);
            var result = contactMonitor.Update(
                evidenceReal256,
                evidenceImaginary256,
                qualities,
                config.DacSettings.ActualFrequencyHz,
                peerFrequencyEvidence,
                Volatile.Read(ref state.ContactSubspaceEvidence));
            state.LastContactDiagnosticWasDegraded = useDiagnosticAverage;
            state.LastContactDiagnosticElapsedMs = Stopwatch.GetElapsedTime(diagnosticStarted).TotalMilliseconds;
            state.MaxContactDiagnosticElapsedMs = Math.Max(
                state.MaxContactDiagnosticElapsedMs,
                state.LastContactDiagnosticElapsedMs);
            state.ContactDiagnosticsRunCount++;
            state.LatestContactResult = result;
            var severe = IsSevereContactResult(result);
            if (config.StoragePolicy.KeepRawRingBuffer && severe && !previousSevere)
            {
                callbacks.CaptureRawRing(
                    config,
                    state,
                    $"contact-event-block-{block.BlockNumber}");
            }

            callbacks.PublishContactSummary(
                config.SetLabel,
                useDiagnosticAverage
                    ? $"{result.Summary} · 诊断解调 {block.DiagnosticAverage!.FiniteMeasurementCount}/208"
                    : result.Summary);
            callbacks.PublishMultiFrequencySummary(
                config.SetLabel,
                CreateRealtimeMultiFrequencyFusionSummary(
                    config,
                    result.MultiFrequencyFusion,
                    peerFrequencyEvidence.Count));
            if (result.ReferenceInvalidated)
            {
                state.InvalidateReference("contact_recovery_invalidated_reference");
                state.ExportableContactCalibration = null;
                state.ExportableSessionCalibration = null;
                callbacks.PublishReferenceInvalidated(config.SetLabel, true);
                callbacks.PublishBoundaryFitUnavailable(
                    config.SetLabel,
                    "边界电压：参考已失效，已清除旧拟合；等待重锁参考。");
                var message = $"{config.SetLabel} 参考帧失效：电极状态已从红/深红恢复为绿，请点击“重锁参考”重新采集 v_ref + qc_ref。";
                callbacks.PublishReferenceSummary(config.SetLabel, message);
                callbacks.PublishReconstructionActivity(config.SetLabel, "重构状态：参考失效 · 已暂停并等待重锁");
                callbacks.Diagnostic($"{config.SetLabel} reference invalidated by contact recovery");
                callbacks.QueueLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} reference invalidated after contact recovery");
                callbacks.InvalidateCalibrationState(config.SetLabel);
            }

            if (ShouldLogRealtimeContactState(state, result, block.BlockNumber))
            {
                callbacks.Diagnostic($"{config.SetLabel} {result.Summary}");
            }

            return result;
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{config.SetLabel} contact diagnostics failed block={block.BlockNumber}: {ex.Message}");
            return null;
        }
    }

    private static bool ShouldLogRealtimeBlockMilestone(int blockNumber)
    {
        return blockNumber <= 5 || blockNumber % 100 == 0;
    }

    internal static bool ShouldLogRealtimeContactState(
        RealtimeRunState state,
        ElectrodeContactDiagnosticResult result,
        int blockNumber)
    {
        var severity = result.SystemLevel
            ? 2
            : result.RedLikeElectrodeCount > 0 ||
              result.States.Any(value => value == ElectrodeContactState.Yellow)
                ? 1
                : 0;
        var previousSeverity = state.LastContactDiagnosticSeverity;
        var episodeBoundary = previousSeverity < 0 ||
                              (previousSeverity == 0) != (severity == 0);
        var systemEscalation = severity == 2 &&
                               !state.ContactDiagnosticSystemEscalationLogged;
        state.LastContactDiagnosticSeverity = severity;
        if (severity == 0)
        {
            state.ContactDiagnosticSystemEscalationLogged = false;
        }
        else if (systemEscalation)
        {
            state.ContactDiagnosticSystemEscalationLogged = true;
        }

        if (episodeBoundary || systemEscalation || ShouldLogRealtimeBlockMilestone(blockNumber))
        {
            Interlocked.Exchange(ref state.LastContactDiagnosticLogTicks, Stopwatch.GetTimestamp());
            return true;
        }

        return severity > 0 && ShouldUpdateRealtimeUi(
            ref state.LastContactDiagnosticLogTicks,
            RealtimeContactDiagnosticLogInterval);
    }

    private static bool ShouldUpdateRealtimeUi(ref long lastTicks, TimeSpan interval)
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


    private static bool IsSevereContactResult(ElectrodeContactDiagnosticResult? result)
    {
        return result is { } value && (value.SystemLevel || value.RedLikeElectrodeCount > 0);
    }
}
