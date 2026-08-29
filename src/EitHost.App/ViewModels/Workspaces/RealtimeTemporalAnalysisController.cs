using System.Diagnostics;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeTemporalAnalysisCallbacks(
    Action<string> Diagnostic,
    Action<RealtimeImagingRunConfig, RealtimeRunState, string> InvalidateProvisionalReference,
    Action<string, RealtimeDemodulatedBlock, RealtimeRunState> PublishNeutralRoiMeasurement,
    Action<string, RealtimeRunState, ElectrodeContactDiagnosticResult?, string, string> QueueNeutralImage,
    Action<string, string> PublishReconstructionActivity,
    Action<string> QueueLog,
    Func<RealtimeRunState, bool> ShouldUpdateStatus);

internal sealed class RealtimeTemporalAnalysisController
{
    private const long ReconstructionStatsQuietMilliseconds = 3000;
    private static readonly TimeSpan SampleDiscontinuityLogInterval = TimeSpan.FromSeconds(1);
    private readonly RealtimeTemporalAnalysisCallbacks callbacks;
    private readonly EcdCwrCenteredTemporalDespiker temporalDespiker = new();

    internal RealtimeTemporalAnalysisController(RealtimeTemporalAnalysisCallbacks callbacks)
    {
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal RealtimeTemporalSelection? CreateSelection(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        IReadOnlyList<double> target,
        ElectrodeContactDiagnosticResult? contactResult,
        EcdCwrWaveformTemplateDisplayPackage? templateDisplayPackage)
    {
        var compensatedContactResult = config.EnableOutlierCompensation && !state.ReferenceIsProvisional
            ? contactResult
            : null;
        var baseWeights = state.StartupDegradedReference?.MeasurementWeight208.ToArray() ??
            compensatedContactResult?.MeasurementWeight208?.ToArray()
            ?? Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray();
        var basePolicy = state.StartupDegradedReference?.WeightPolicyVersion ??
            compensatedContactResult?.WeightPolicyVersion ??
            "all-one-v1";
        if (state.ReferenceUsesCommonScaleNormalization)
        {
            basePolicy = $"{basePolicy}+{EcdCwrCommonScaleNormalizer.PolicyVersion}";
        }

        if (!config.EnableTemporalDespiking)
        {
            ResetWindow(state);
            return new RealtimeTemporalSelection(
                block,
                target.ToArray(),
                baseWeights,
                basePolicy,
                contactResult,
                templateDisplayPackage,
                null);
        }

        var window = state.TemporalWindow.Push(block.BlockNumber, new RealtimeTemporalCandidate(
            block,
            target.ToArray(),
            baseWeights,
            basePolicy,
            contactResult,
            templateDisplayPackage));
        if (window is null)
        {
            return null;
        }

        var center = window[EcdCwrCenteredTemporalDespiker.CenterIndex];
        var temporalResult = temporalDespiker.Analyze(
            window.Select(candidate => (IReadOnlyList<double>)candidate.Target).ToArray(),
            center.BaseWeights);
        if (temporalResult.IsolatedChannelCount > 0)
        {
            callbacks.Diagnostic(
                $"{config.SetLabel} temporal gate block={center.Block.BlockNumber} isolated={temporalResult.IsolatedChannelCount}/208 global={temporalResult.IsGlobalIsolatedSpike} max={temporalResult.MaximumExcursionScore:F2}");
        }

        return new RealtimeTemporalSelection(
            center.Block,
            temporalResult.RepairedCenter208,
            temporalResult.CombinedMeasurementWeight208,
            CreateRepairPolicyVersion(center.BaseWeightPolicyVersion, temporalResult),
            center.ContactResult,
            center.TemplateDisplayPackage,
            temporalResult);
    }

    internal static void ResetWindow(RealtimeRunState state)
    {
        state.TemporalWindow.Reset();
    }

    internal void ApplyPendingDiscontinuities(
        RealtimeImagingRunConfig config,
        RealtimeRunState state)
    {
        if (!state.SampleContinuity.TryDrain(out var batch))
        {
            return;
        }

        ResetWindow(state);
        state.BoundaryChangeGate?.Reset();
        state.DynamicKalmanGeneration++;
        state.DynamicKalmanResetPending = true;
        state.ConsecutiveLowQualityBlocks = 0;
        state.TimingConsistency.Reset();
        state.TimingMismatchWarningRaised = false;
        state.SampleContinuityRecoveryPending = true;
        callbacks.InvalidateProvisionalReference(config, state, "sample discontinuity");
        if (state.ReferenceVoltage208 is null)
        {
            state.ReferenceCandidateFrames.Clear();
            Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
            Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
            state.ContactCalibrationFrames.Clear();
            state.ReferenceStationarity.Reset();
            state.LatestReferenceStationarity = null;
            state.RobustReference = null;
        }

        state.UnloggedSampleDiscontinuityCount += batch.DiscontinuityCount;
        state.UnloggedMissingSampleRows += batch.MissingSampleRows;
        state.UnloggedUsbOverflowCount += batch.UsbOverflowCount;
        state.LatestSampleDiscontinuity = batch.Latest;
        if (!ShouldLogDiscontinuity(state))
        {
            return;
        }

        var latest = state.LatestSampleDiscontinuity;
        var continuity = state.SampleContinuity.Snapshot();
        callbacks.Diagnostic(
            $"{config.SetLabel} sample discontinuity burst count={state.UnloggedSampleDiscontinuityCount} " +
            $"missingRows={state.UnloggedMissingSampleRows} usbOverflows={state.UnloggedUsbOverflowCount} " +
            $"latestReason={latest?.Reason ?? "unknown"} expected={latest?.ExpectedStartSampleIndex ?? 0} " +
            $"actual={latest?.ActualStartSampleIndex ?? 0}; totals gaps={continuity.TotalDiscontinuities} " +
            $"missingRows={continuity.TotalMissingSampleRows}; demod/temporal/Kalman reset");
        state.UnloggedSampleDiscontinuityCount = 0;
        state.UnloggedMissingSampleRows = 0;
        state.UnloggedUsbOverflowCount = 0;
    }

    internal void HandleNoChange(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeTemporalSelection selection,
        EcdCwrBoundaryChangeDecision decision)
    {
        var enteringNoChange = !state.BoundaryNoChangeActive;
        if (enteringNoChange)
        {
            state.BoundaryNoChangeActive = true;
            state.DynamicKalmanGeneration++;
            state.ImageRasterCache.ResetColorScale();
        }

        state.DynamicKalmanResetPending = true;
        callbacks.PublishNeutralRoiMeasurement(config.SetLabel, selection.Block, state);
        if (enteringNoChange)
        {
            var stats =
                $"block {selection.Block.BlockNumber} · 均匀场噪声底 · ΔV score {decision.GlobalScore:F2}/{decision.Threshold:F2} · " +
                $"3σ通道 {decision.ExcursionCount}/208 · 可信ΔV=0 · 未启动逆问题 · Kalman已复位";
            callbacks.QueueNeutralImage(
                config.SetLabel,
                state,
                selection.ContactResult,
                stats,
                "重构状态：可信无变化 · 显示中性基线");
            callbacks.QueueLog(
                $"{DateTime.Now:HH:mm:ss} {config.SetLabel} 未检出可信电导率变化；可信ΔV已归零且未启动逆问题，原始浮点电压完整保留");
        }

        if (callbacks.ShouldUpdateStatus(state) &&
            Environment.TickCount64 - Volatile.Read(ref state.LastReconImageStatsTicks) > ReconstructionStatsQuietMilliseconds)
        {
            var stateText = decision.Action == EcdCwrBoundaryChangeAction.PendingChange
                ? $"候选变化 {decision.ConsecutiveChangeCount}/3"
                : "无变化";
            callbacks.PublishReconstructionActivity(
                config.SetLabel,
                $"ΔV可信度：{stateText} · score={decision.GlobalScore:F2}/{decision.Threshold:F2} · 3σ通道={decision.ExcursionCount}/208");
        }
    }

    private static string CreateRepairPolicyVersion(
        string basePolicyVersion,
        EcdCwrTemporalDespikingResult temporalResult)
    {
        var repaired = temporalResult.RepairedChannelIndices.Length == 0
            ? "none"
            : string.Join(',', temporalResult.RepairedChannelIndices.Select(index => index + 1));
        return $"{basePolicyVersion}+{temporalResult.WeightPolicyVersion}:repaired1={repaired}";
    }

    private static bool ShouldLogDiscontinuity(RealtimeRunState state)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref state.LastSampleDiscontinuityLogTicks);
        var intervalTicks = (long)(SampleDiscontinuityLogInterval.TotalSeconds * Stopwatch.Frequency);
        if (previous != 0 && now - previous < intervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref state.LastSampleDiscontinuityLogTicks, now, previous) == previous;
    }
}
