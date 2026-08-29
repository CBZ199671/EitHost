using System.Globalization;
using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Pairing;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimePairingRecoveryController
{
    private const int ConsecutiveMismatchBlocks = 2;
    private const int MinimumRejectedWindows = 8;
    private const double MinimumMismatchRatio = 0.5;
    private const double PeakToBackgroundThreshold = 20.0;
    private static readonly TimeSpan AutoSwapWait = TimeSpan.FromSeconds(8);

    private readonly HardwareWorkspaceViewModel workspace;
    private readonly HardwareDiscoveryController discovery;
    private readonly ExperimentCatalog catalog;
    private readonly Guid sessionId;
    private readonly RealtimeSessionController sessions;
    private readonly RealtimeRunCommandController runCommands;
    private readonly RealtimePreviewController preview;
    private readonly RealtimePairingRecoveryCallbacks callbacks;
    private int autoSwapInProgress;
    private string? lastAutoSwapTargetMap;

    internal RealtimePairingRecoveryController(
        HardwareWorkspaceViewModel workspace,
        HardwareDiscoveryController discovery,
        ExperimentCatalog catalog,
        Guid sessionId,
        RealtimeSessionController sessions,
        RealtimeRunCommandController runCommands,
        RealtimePreviewController preview,
        RealtimePairingRecoveryCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.sessionId = sessionId;
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.runCommands = runCommands ?? throw new ArgumentNullException(nameof(runCommands));
        this.preview = preview ?? throw new ArgumentNullException(nameof(preview));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void UpdateSelfCheck(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (block.IsHighQuality)
        {
            state.ConsecutivePairingMismatchBlocks = 0;
            return;
        }

        if (!LooksLikeMismatch(block, out var evidence))
        {
            state.ConsecutivePairingMismatchBlocks = 0;
            return;
        }

        state.ConsecutivePairingMismatchBlocks++;
        var evidenceLine = FormatEvidence(evidence);
        callbacks.AddDiagnostic(
            $"{config.SetLabel} pairing self-check mismatch block={block.BlockNumber} dds={config.DdsPortName} usb=#{config.UsbDevice.DeviceNumber} {evidenceLine}");
        if (state.PairingMismatchWarningRaised || state.ConsecutivePairingMismatchBlocks < ConsecutiveMismatchBlocks)
        {
            return;
        }

        state.PairingMismatchWarningRaised = true;
        var mapping = string.IsNullOrWhiteSpace(config.PairingMapSummary)
            ? string.Empty
            : $" 当前绑定：{config.PairingMapSummary}。";
        var message =
            $"{config.SetLabel} 配套自检疑似失败：DDS {config.DdsPortName} + USB2070 #{config.UsbDevice.DeviceNumber} 捕获到强信号，但参考电极拓扑不匹配；请停止后检查/交换 DDS COM 与 USB2070 套号绑定。{mapping}";
        preview.PublishReconstructionActivity(config.SetLabel, "重构状态：配套自检失败 · 请检查设备绑定");
        preview.PublishSummary(config.SetLabel, message);
        preview.QueueLog($"{DateTime.Now:HH:mm:ss} {message} {evidenceLine}");
        callbacks.PostToUi(() =>
        {
            callbacks.SetStatus(message);
            TryStartAutoSwap(config.SetLabel, evidenceLine);
        });
    }

    internal string CreatePairingMapSummary() =>
        string.Join(
            "；",
            workspace.BoundPairings.Select(pairing =>
                $"{pairing.Title}=USB#{pairing.Pairing.Usb2070DeviceNumber}+{pairing.Pairing.DdsSerialCandidate.PortName ?? "COM?"}"));

    private void TryStartAutoSwap(string triggerSetLabel, string evidenceLine)
    {
        if (Interlocked.CompareExchange(ref autoSwapInProgress, 1, 0) != 0)
        {
            callbacks.AddDiagnostic($"{triggerSetLabel} auto pairing swap skipped: already in progress");
            return;
        }

        if (!TryCreatePlan(triggerSetLabel, evidenceLine, out var plan, out var rejectionMessage))
        {
            Interlocked.Exchange(ref autoSwapInProgress, 0);
            callbacks.AddDiagnostic($"{triggerSetLabel} auto pairing swap not eligible: {rejectionMessage}");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {triggerSetLabel} 自动换绑未执行：{rejectionMessage}");
            return;
        }

        callbacks.AddDiagnostic(
            $"{triggerSetLabel} auto pairing swap begin before={plan.BeforeMap} after={plan.AfterMap} evidence={evidenceLine}");
        callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {triggerSetLabel} 配套自检触发自动换绑：停止当前采集，准备交换两套 DDS COM。");
        callbacks.SetStatus($"{triggerSetLabel} 配套自检触发自动换绑：正在停止当前错误采集。");
        runCommands.RequestStop(showIdleMessage: false);
        _ = RunAutoSwapAsync(plan);
    }

    private bool TryCreatePlan(
        string triggerSetLabel,
        string evidenceLine,
        out RealtimeAutoPairingSwapPlan plan,
        out string rejectionMessage)
    {
        plan = default!;
        rejectionMessage = string.Empty;
        if (workspace.BoundPairings.Count != 2)
        {
            rejectionMessage = $"当前绑定 {workspace.BoundPairings.Count} 套；自动换绑仅支持 2 套互换。";
            return false;
        }

        var pairings = workspace.BoundPairings.ToArray();
        if (pairings.Any(pairing => string.IsNullOrWhiteSpace(pairing.Pairing.DdsSerialCandidate.PortName)))
        {
            rejectionMessage = "存在 DDS COM 为空的绑定。";
            return false;
        }

        if (!pairings.All(pairing => sessions.IsSetActive(pairing.Title)))
        {
            rejectionMessage = "并非两套设备都在实时成像中。";
            return false;
        }

        var states = pairings
            .Select(pairing => sessions.TryGetState(pairing.Title, out var state) ? state : null)
            .Where(state => state is { IsActive: true })
            .Cast<RealtimeRunState>()
            .ToArray();
        if (states.Length != 2)
        {
            rejectionMessage = "未找到两套仍在运行的实时任务。";
            return false;
        }

        var beforeMap = CreatePairingMapSummary();
        var afterMap = CreateSwappedPairingMapSummary(pairings[0], pairings[1]);
        if (string.Equals(beforeMap, lastAutoSwapTargetMap, StringComparison.Ordinal))
        {
            rejectionMessage = "当前映射已经是上一轮自动换绑后的目标映射，避免来回交换。";
            return false;
        }

        plan = new RealtimeAutoPairingSwapPlan(
            triggerSetLabel,
            evidenceLine,
            beforeMap,
            afterMap,
            states.Select(state => state.Task ?? Task.CompletedTask).ToArray());
        return true;
    }

    private async Task RunAutoSwapAsync(RealtimeAutoPairingSwapPlan plan)
    {
        try
        {
            var stopTasks = plan.StopTasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(plan.StopTasks);
            var completed = await Task.WhenAny(stopTasks, Task.Delay(AutoSwapWait)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, stopTasks))
            {
                callbacks.AddDiagnostic($"{plan.TriggerSetLabel} auto pairing swap abort: stop timeout before={plan.BeforeMap}");
                callbacks.PostToUi(() =>
                {
                    callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {plan.TriggerSetLabel} 自动换绑中止：等待实时任务停止超时，请手动全部停止后重试。");
                    callbacks.SetStatus($"{plan.TriggerSetLabel} 自动换绑中止：等待实时任务停止超时。");
                    Interlocked.Exchange(ref autoSwapInProgress, 0);
                });
                return;
            }

            await stopTasks.ConfigureAwait(false);
            callbacks.PostToUi(() => CompleteAutoSwap(plan));
        }
        catch (Exception ex)
        {
            callbacks.AddDiagnostic($"{plan.TriggerSetLabel} auto pairing swap failed: {ex}");
            callbacks.PostToUi(() =>
            {
                callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {plan.TriggerSetLabel} 自动换绑失败：{ex.Message}");
                callbacks.SetStatus($"{plan.TriggerSetLabel} 自动换绑失败：{ex.Message}");
                Interlocked.Exchange(ref autoSwapInProgress, 0);
            });
        }
    }

    private void CompleteAutoSwap(RealtimeAutoPairingSwapPlan plan)
    {
        try
        {
            var currentMap = CreatePairingMapSummary();
            if (!string.Equals(currentMap, plan.BeforeMap, StringComparison.Ordinal))
            {
                callbacks.AddDiagnostic($"{plan.TriggerSetLabel} auto pairing swap abort: mapping changed current={currentMap} before={plan.BeforeMap}");
                callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {plan.TriggerSetLabel} 自动换绑中止：绑定映射已变化，请重新启动。");
                callbacks.SetStatus($"{plan.TriggerSetLabel} 自动换绑中止：绑定映射已变化。");
                return;
            }

            if (sessions.ActiveSetCount > 0 || sessions.States.Any(state => state.IsActive))
            {
                callbacks.AddDiagnostic($"{plan.TriggerSetLabel} auto pairing swap abort: realtime still active");
                callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {plan.TriggerSetLabel} 自动换绑中止：实时任务仍未完全停止。");
                callbacks.SetStatus($"{plan.TriggerSetLabel} 自动换绑中止：实时任务仍未完全停止。");
                return;
            }

            ApplyTwoSetDdsSwap();
            lastAutoSwapTargetMap = CreatePairingMapSummary();
            callbacks.AddDiagnostic($"{plan.TriggerSetLabel} auto pairing swap applied before={plan.BeforeMap} after={lastAutoSwapTargetMap}");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} 自动换绑完成：{plan.BeforeMap} → {lastAutoSwapTargetMap}；正在重新启动全部实时成像。");
            callbacks.SetStatus("自动换绑完成：已交换两套 DDS COM，正在重新启动全部实时成像。");
            runCommands.StartAllCore();
        }
        catch (Exception ex)
        {
            callbacks.AddDiagnostic($"{plan.TriggerSetLabel} auto pairing swap failed: {ex}");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {plan.TriggerSetLabel} 自动换绑失败：{ex.Message}");
            callbacks.SetStatus($"{plan.TriggerSetLabel} 自动换绑失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref autoSwapInProgress, 0);
        }
    }

    private void ApplyTwoSetDdsSwap()
    {
        if (workspace.BoundPairings.Count != 2)
        {
            throw new InvalidOperationException("自动换绑仅支持 2 套设备。");
        }

        var selectedBoundLabel = workspace.SelectedBoundPairing?.Title;
        var selectedDisplayLabel = workspace.SelectedRealtimeDisplayPairing?.Title;
        var first = workspace.BoundPairings[0];
        var second = workspace.BoundPairings[1];
        var updatedFirst = CreatePairingSummaryWithDds(first, second.Pairing.DdsSerialCandidate);
        var updatedSecond = CreatePairingSummaryWithDds(second, first.Pairing.DdsSerialCandidate);
        discovery.ReplacePairings([updatedFirst.Pairing, updatedSecond.Pairing]);
        workspace.BoundPairings[0] = updatedFirst;
        workspace.BoundPairings[1] = updatedSecond;
        if (callbacks.IsCatalogReady())
        {
            catalog.UpsertPairing(sessionId, updatedFirst.Pairing);
            catalog.UpsertPairing(sessionId, updatedSecond.Pairing);
        }

        workspace.SelectedBoundPairing = workspace.BoundPairings.FirstOrDefault(item =>
            string.Equals(item.Title, selectedBoundLabel, StringComparison.Ordinal)) ?? workspace.BoundPairings[0];
        workspace.SelectedRealtimeDisplayPairing = workspace.BoundPairings.FirstOrDefault(item =>
            string.Equals(item.Title, selectedDisplayLabel, StringComparison.Ordinal)) ?? workspace.SelectedBoundPairing;
        callbacks.NotifyBindingsChanged();
    }

    private static bool LooksLikeMismatch(
        RealtimeDemodulatedBlock block,
        out RealtimePairingMismatchEvidence evidence)
    {
        var rejectedWindows = block.Frames.SelectMany(frame => frame.WindowQualities).Where(quality => quality.Rejected).ToArray();
        var mismatchWindows = rejectedWindows
            .Where(quality => quality.RejectReason == DemodulatedWindowRejectReason.ExpectedReferenceNotInTop3)
            .ToArray();
        var finitePeakToBackground = mismatchWindows
            .Select(quality => quality.PeakToBackgroundRatio)
            .Where(double.IsFinite)
            .ToArray();
        var maxPeakToBackground = finitePeakToBackground.Length == 0 ? 0.0 : finitePeakToBackground.Max();
        var firstMismatch = mismatchWindows.FirstOrDefault();
        evidence = new RealtimePairingMismatchEvidence(
            rejectedWindows.Length,
            mismatchWindows.Length,
            rejectedWindows.Length == 0 ? 0.0 : (double)mismatchWindows.Length / rejectedWindows.Length,
            maxPeakToBackground,
            firstMismatch?.WindowIndex + 1,
            firstMismatch?.ExpectedReferenceChannel + 1,
            firstMismatch?.DetectedTop1Channel + 1,
            firstMismatch?.Top3Channels.Select(channel => channel + 1).ToArray() ?? []);
        return block.AcceptedFrameCount == 0
            && mismatchWindows.Length >= MinimumRejectedWindows
            && evidence.MismatchRatio >= MinimumMismatchRatio
            && maxPeakToBackground >= PeakToBackgroundThreshold;
    }

    private static string FormatEvidence(RealtimePairingMismatchEvidence evidence)
    {
        var top3 = evidence.FirstTop3Channels.Length == 0
            ? "-"
            : string.Join("/", evidence.FirstTop3Channels.Select(channel => channel.ToString(CultureInfo.InvariantCulture)));
        return FormattableString.Invariant(
            $"mismatch={evidence.ExpectedReferenceNotInTop3Count}/{evidence.RejectedWindowCount} ratio={evidence.MismatchRatio:P0} pbg_max={evidence.MaxPeakToBackgroundRatio:G3} first w{evidence.FirstWindowIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"} exp={evidence.FirstExpectedReferenceChannel?.ToString(CultureInfo.InvariantCulture) ?? "-"} top1={evidence.FirstDetectedTop1Channel?.ToString(CultureInfo.InvariantCulture) ?? "-"} top3={top3}");
    }

    private static PairingSummaryItem CreatePairingSummaryWithDds(
        PairingSummaryItem source,
        PnpDeviceCandidate ddsCandidate) =>
        new(new EitSetPairing(
            source.Pairing.Label,
            source.Pairing.Usb2070DeviceNumber,
            source.Pairing.Usb2070Candidate,
            ddsCandidate,
            DateTimeOffset.UtcNow));

    private static string CreateSwappedPairingMapSummary(PairingSummaryItem first, PairingSummaryItem second) =>
        string.Join(
            "；",
            $"{first.Title}=USB#{first.Pairing.Usb2070DeviceNumber}+{second.Pairing.DdsSerialCandidate.PortName ?? "COM?"}",
            $"{second.Title}=USB#{second.Pairing.Usb2070DeviceNumber}+{first.Pairing.DdsSerialCandidate.PortName ?? "COM?"}");
}

internal sealed record RealtimePairingRecoveryCallbacks(
    Func<bool> IsCatalogReady,
    Action<Action> PostToUi,
    Action<string> AddDiagnostic,
    Action<string> AddLog,
    Action<string> SetStatus,
    Action NotifyBindingsChanged);

internal sealed record RealtimePairingMismatchEvidence(
    int RejectedWindowCount,
    int ExpectedReferenceNotInTop3Count,
    double MismatchRatio,
    double MaxPeakToBackgroundRatio,
    int? FirstWindowIndex,
    int? FirstExpectedReferenceChannel,
    int? FirstDetectedTop1Channel,
    int[] FirstTop3Channels);

internal sealed record RealtimeAutoPairingSwapPlan(
    string TriggerSetLabel,
    string EvidenceLine,
    string BeforeMap,
    string AfterMap,
    Task[] StopTasks);
