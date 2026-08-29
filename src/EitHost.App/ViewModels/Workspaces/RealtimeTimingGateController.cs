using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeTimingGateCallbacks(
    Action<string> Diagnostic,
    Action<string> QueueLog,
    Action<string> PublishStatus,
    Action<string, string> PublishSummary,
    Action<string, string> PublishBoundaryFitUnavailable,
    Action<string, string?, string?, string?, string?> PublishQualityAxes,
    Action<RealtimeImagingRunConfig, RealtimeRunState, string> InvalidateProvisionalReference,
    Action<RealtimeRunState> ResetTemporalWindow,
    Func<RealtimeImagingRunConfig, RealtimeRunState, string> CreateReferenceModeStatus);

internal sealed class RealtimeTimingGateController(RealtimeTimingGateCallbacks callbacks)
{
    private readonly RealtimeTimingGateCallbacks callbacks = callbacks ??
        throw new ArgumentNullException(nameof(callbacks));

    internal bool AllowsProcessing(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        var execution = state.ExecutionReceipt ?? throw new InvalidOperationException(
            $"{config.SetLabel} missing DDS firmware v2 execution receipt.");
        var timing = DdsTimingValidator.Validate(
            execution,
            config.AcquisitionSettings.SampleRateHz,
            block.EstimatedWindowSamples);
        state.LatestTimingValidation = timing;
        var consistency = state.TimingConsistency.Evaluate(timing, block.IsHighQuality);
        if (block.IsHighQuality &&
            consistency.State == DdsTimingConsistencyState.PendingMismatch &&
            consistency.ConsecutiveMismatches == 1)
        {
            var pendingMessage = FormattableString.Invariant(
                $"{DdsTimingValidationResult.ExcitationTimingMismatch} pending 1/{DdsTimingConsistencyMonitor.DefaultConfirmationCount} expected={timing.ExpectedWindowSamples:0.###} observed={timing.ObservedWindowSamples:0.###} tolerance={timing.ToleranceSamples:0.###}; processing continues");
            callbacks.Diagnostic($"{config.SetLabel} {pendingMessage}");
            callbacks.QueueLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} {pendingMessage}");
            callbacks.PublishStatus(
                $"{config.SetLabel} 时序估计边缘波动 1/{DdsTimingConsistencyMonitor.DefaultConfirmationCount}，继续诊断与重构。");
        }

        if (consistency.BlocksRealtimeProcessing)
        {
            BlockProcessing(config, state, timing, consistency);
            return false;
        }

        if (consistency.JustRecovered)
        {
            const string recoveredMessage =
                "excitation timing recovered after 3 consecutive matching blocks; diagnostics and reconstruction resumed";
            callbacks.Diagnostic($"{config.SetLabel} {recoveredMessage}");
            callbacks.QueueLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} {recoveredMessage}");
            callbacks.PublishStatus($"{config.SetLabel} 激励时序已连续 3 块恢复，诊断与重构自动继续。");
        }

        state.TimingMismatchWarningRaised = false;
        return true;
    }

    private void BlockProcessing(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        DdsTimingValidationResult timing,
        DdsTimingConsistencyDecision consistency)
    {
        state.LowQualityBlocks++;
        state.ConsecutiveLowQualityBlocks++;
        callbacks.InvalidateProvisionalReference(config, state, "excitation timing mismatch");
        state.ReferenceCandidateFrames.Clear();
        Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
        Interlocked.Exchange(ref state.ManualReferenceLockRequested, 0);
        state.ContactCalibrationFrames.Clear();
        state.ReferenceStationarity.Reset();
        state.LatestReferenceStationarity = null;
        state.RobustReference = null;
        callbacks.ResetTemporalWindow(state);
        if (consistency.JustConfirmed || !state.TimingMismatchWarningRaised)
        {
            state.TimingMismatchWarningRaised = true;
            var message = FormattableString.Invariant(
                $"{DdsTimingValidationResult.ExcitationTimingMismatch} expected={timing.ExpectedWindowSamples:0.###} observed={timing.ObservedWindowSamples:0.###} tolerance={timing.ToleranceSamples:0.###}; reference lock, reconstruction and electrode diagnostics blocked");
            callbacks.Diagnostic($"{config.SetLabel} {message}");
            callbacks.QueueLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} {message}");
            callbacks.PublishSummary(
                config.SetLabel,
                $"{config.SetLabel} 激励时序不一致：期望 {timing.ExpectedWindowSamples:0.###} 点，" +
                $"实测 {timing.ObservedWindowSamples:0.###} 点，容差 {timing.ToleranceSamples:0.###} 点；已阻断诊断与重构。");
            callbacks.PublishStatus(
                $"{config.SetLabel} {DdsTimingValidationResult.ExcitationTimingMismatch}：已阻断 Top3、接触诊断与重构。");
        }

        callbacks.PublishBoundaryFitUnavailable(
            config.SetLabel,
            consistency.State == DdsTimingConsistencyState.Recovering
                ? $"边界电压：激励时序恢复确认 {consistency.ConsecutiveMatches}/{DdsTimingConsistencyMonitor.DefaultConfirmationCount}，暂缓更新。"
                : "边界电压：激励时序不一致，已清除旧拟合并暂停更新。");
        callbacks.PublishQualityAxes(
            config.SetLabel,
            "数据质量：不可用 · 激励时序不一致",
            callbacks.CreateReferenceModeStatus(config, state),
            "重构质量：已阻断 · 时序硬门禁",
            "ROI 就绪：否 · 时序硬门禁");
    }
}
