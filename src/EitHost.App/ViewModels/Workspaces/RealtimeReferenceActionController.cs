using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeReferenceActionCallbacks(
    Func<string?> SelectedSetLabel,
    Action<string> PublishStatus,
    Action<string, string> PublishReferenceSummary,
    Action<string> QueueLog,
    Action<string> Diagnostic,
    Action RefreshSelectionPresentation,
    Action RefreshWindowPresentation,
    Action RefreshPresentation);

internal sealed class RealtimeReferenceActionController
{
    private const int MinimumReferenceFrames = 100;
    private readonly RealtimeWorkspaceViewModel workspace;
    private readonly RealtimeSessionController sessions;
    private readonly object synchronizedActionGate;
    private readonly RealtimeReferenceActionCallbacks callbacks;

    internal RealtimeReferenceActionController(
        RealtimeWorkspaceViewModel workspace,
        RealtimeSessionController sessions,
        object synchronizedActionGate,
        RealtimeReferenceActionCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.synchronizedActionGate = synchronizedActionGate ?? throw new ArgumentNullException(nameof(synchronizedActionGate));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal string ManualActionText
    {
        get
        {
            var label = callbacks.SelectedSetLabel();
            if (label is null || !sessions.TryGetState(label, out var state) || !state.IsActive)
            {
                return "开始采集后可手动建立参考";
            }

            if (Volatile.Read(ref state.ManualReferenceLockRequested) != 0)
            {
                return "正在建立正式参考…";
            }

            if (state.ReferenceVoltage208 is not null &&
                !state.ReferenceIsProvisional &&
                !state.ReplacementReferenceCollecting)
            {
                return "正式参考已启用 · ROI 运行中";
            }

            if (state.AutomaticReferenceWindow is { } automaticWindow)
            {
                return state.ReplacementReferenceCollecting
                    ? $"用点击前 {automaticWindow.FrameCount} 帧准备新参考"
                    : $"用点击前 {automaticWindow.FrameCount} 帧建立正式参考并开始 ROI";
            }

            var count = Volatile.Read(ref state.ReferenceCandidateContinuousCount);
            return $"准备参考 {Math.Min(count, MinimumReferenceFrames)}/{MinimumReferenceFrames}";
        }
    }

    internal bool ShouldShowSwitchControls
    {
        get
        {
            var label = callbacks.SelectedSetLabel();
            return label is not null &&
                sessions.TryGetState(label, out var state) &&
                state.ReplacementPreparedReference is not null &&
                state.ReplacementReferenceSynchronizedSetCount <= 1;
        }
    }

    internal bool ShouldShowSynchronizedControls => sessions.States.Count(state => state.IsActive) >= 2;

    internal void OnSelectedWindowChanged(RealtimeReferenceWindowOption? option)
    {
        var label = callbacks.SelectedSetLabel();
        if (label is not null && sessions.TryGetState(label, out var state))
        {
            state.SelectedReferenceWindow = option?.Window;
        }

        callbacks.RefreshSelectionPresentation();
    }

    internal void RefreshWindowOptions(string? requestedLabel = null)
    {
        var label = requestedLabel ?? callbacks.SelectedSetLabel();
        if (label is null ||
            !sessions.TryGetState(label, out var state) ||
            state.Config is null)
        {
            workspace.ReferenceWindowOptions.Clear();
            workspace.SelectedReferenceWindowOption = null;
            workspace.ReferenceRelockStateText = "重锁：未启动；当前参考持续用于成像与 ROI。";
            callbacks.RefreshWindowPresentation();
            return;
        }

        workspace.ReferenceRelockStateText = CreateRelockStateText(state);
        EcdCwrReferenceWindow? automaticWindow;
        IReadOnlyList<EcdCwrReferenceWindow> windows;
        lock (state.ReferenceCandidateGate)
        {
            automaticWindow = state.ReferenceCandidateHistory.BuildAutomaticWindow(
                DateTimeOffset.Now,
                MinimumReferenceFrames);
            windows = state.ReferenceCandidateHistory.BuildRepresentativeWindows(
                MinimumReferenceFrames);
            state.AutomaticReferenceWindow = automaticWindow;
        }

        var selectedId = state.SelectedReferenceWindow?.WindowId;
        workspace.ReferenceWindowOptions.Clear();
        foreach (var window in windows)
        {
            workspace.ReferenceWindowOptions.Add(new RealtimeReferenceWindowOption(window));
        }

        workspace.SelectedReferenceWindowOption = workspace.ReferenceWindowOptions.FirstOrDefault(option =>
            string.Equals(option.Window.WindowId, selectedId, StringComparison.Ordinal))
            ?? workspace.ReferenceWindowOptions.LastOrDefault();
        if (automaticWindow is null)
        {
            var count = Volatile.Read(ref state.ReferenceCandidateContinuousCount);
            workspace.ReferenceWindowPreview =
                $"自动参考准备中：最近同工况连续质量合格帧 {Math.Min(count, MinimumReferenceFrames)}/{MinimumReferenceFrames}；满 100 帧即可点击，无需等待稳定阈值。";
        }
        else
        {
            workspace.ReferenceWindowPreview =
                $"自动参考就绪：点击时将冻结截止时刻，综合 {automaticWindow.StartedAt.ToLocalTime():HH:mm:ss}–{automaticWindow.EndedAt.ToLocalTime():HH:mm:ss} 的全部 {automaticWindow.FrameCount} 个同工况连续高质量帧；稳健统计自动剔除离群帧。";
        }

        callbacks.RefreshWindowPresentation();
    }

    internal static string CreateRelockStateText(RealtimeRunState state)
    {
        if (Volatile.Read(ref state.ReplacementSwitchRequested) != 0)
        {
            return $"重锁：已确认；当前 e{state.ReferenceEpoch} 继续运行，下一有效目标边界原子切换。";
        }

        if (state.ReplacementPreparedReference is { } prepared)
        {
            return $"重锁：新参考已准备（{prepared.FrameCount} 帧），旧 e{state.ReferenceEpoch} 仍在运行；请确认切换或取消。";
        }

        if (state.ReplacementReferenceCollecting)
        {
            return $"重锁：后台准备中；当前 e{state.ReferenceEpoch} 持续正常成像与 ROI。";
        }

        if (state.ReferenceIsProvisional)
        {
            return $"手动开始：快速预览参考 e{state.ReferenceEpoch} 正在运行；选择合格数据后可立即切换为正式参考并启用正常置信度 ROI。";
        }

        return state.ReferenceEpoch > 0
            ? $"正式参考 e{state.ReferenceEpoch} 已启用；正常成像与 ROI 正在运行。"
            : "手动开始：正在准备质量合格数据；不要求对象静止或通过稳定阈值。";
    }

    internal void UseCurrentReference() => UseReference(useSelectedWindow: false);

    internal void UseSelectedReference() => UseReference(useSelectedWindow: true);

    internal bool CanUseCurrentReference()
    {
        var label = callbacks.SelectedSetLabel();
        return label is not null &&
            sessions.TryGetState(label, out var state) &&
            state.IsActive &&
            (state.ReferenceVoltage208 is null || state.ReferenceIsProvisional || state.ReplacementReferenceCollecting) &&
            state.ReplacementReferenceSynchronizedSetCount <= 1 &&
            state.AutomaticReferenceWindow is not null &&
            Volatile.Read(ref state.ManualReferenceLockRequested) == 0 &&
            Volatile.Read(ref state.ReplacementSwitchRequested) == 0;
    }

    internal bool CanUseSelectedReference()
    {
        var label = callbacks.SelectedSetLabel();
        return label is not null &&
            sessions.TryGetState(label, out var state) &&
            state.IsActive &&
            (state.ReferenceVoltage208 is null || state.ReferenceIsProvisional || state.ReplacementReferenceCollecting) &&
            state.ReplacementReferenceSynchronizedSetCount <= 1 &&
            state.SelectedReferenceWindow is not null &&
            Volatile.Read(ref state.ManualReferenceLockRequested) == 0 &&
            Volatile.Read(ref state.ReplacementSwitchRequested) == 0;
    }

    internal void ResetReference()
    {
        var label = callbacks.SelectedSetLabel();
        if (label is null ||
            !sessions.TryGetState(label, out var state) ||
            !state.IsActive ||
            state.ReferenceVoltage208 is null)
        {
            callbacks.PublishStatus("当前没有可在后台重锁的有效实时参考。");
            return;
        }

        state.BeginReplacementPreparation(DateTimeOffset.Now);
        RefreshWindowOptions(label);
        workspace.ReferenceRelockStateText =
            $"重锁：后台准备中；当前 e{state.ReferenceEpoch} 持续正常成像与 ROI。主按钮自动综合点击前全部合格数据；高级区间可选。";
        callbacks.PublishReferenceSummary(
            label,
            $"重锁准备中：当前参考 e{state.ReferenceEpoch} 保持激活，成像、接触诊断与 ROI 不停；点击主按钮将自动综合操作前最近同工况连续段的全部高质量帧，再确认切换或取消。");
        callbacks.PublishStatus($"{label} 已开始后台重锁准备；旧参考 e{state.ReferenceEpoch} 未清除。");
        QueueLog($"{DateTime.Now:HH:mm:ss} {label} replacement reference collection started activeEpoch={state.ReferenceEpoch}");
        callbacks.RefreshPresentation();
    }

    internal bool CanResetReference()
    {
        var label = callbacks.SelectedSetLabel();
        return label is not null &&
            sessions.TryGetState(label, out var state) &&
            state.IsActive &&
            state.ReferenceVoltage208 is not null &&
            !state.ReplacementReferenceCollecting;
    }

    internal void ConfirmReferenceSwitch()
    {
        var label = callbacks.SelectedSetLabel();
        if (label is null || !sessions.TryGetState(label, out var state))
        {
            return;
        }

        lock (synchronizedActionGate)
        {
            lock (state.ReplacementReferenceGate)
            {
                if (state.ReplacementReferenceSynchronizedSetCount > 1)
                {
                    callbacks.PublishStatus($"{label} 属于多集合同步参考动作；请使用统一确认。");
                    return;
                }

                if (!state.ReplacementReferenceCollecting ||
                    state.ReplacementPreparedReference is null ||
                    !state.RequestReplacementSwitch())
                {
                    callbacks.PublishStatus($"{label} 尚无可确认的新参考。");
                    return;
                }
            }
        }

        workspace.ReferenceRelockStateText =
            $"重锁：已确认；当前 e{state.ReferenceEpoch} 继续运行，下一有效目标边界原子切换。";
        callbacks.PublishStatus($"{label} 已确认新参考；等待下一有效目标边界切换。");
        callbacks.RefreshPresentation();
    }

    internal bool CanConfirmReferenceSwitch()
    {
        var label = callbacks.SelectedSetLabel();
        return label is not null &&
            sessions.TryGetState(label, out var state) &&
            state.IsActive &&
            state.ReplacementReferenceCollecting &&
            state.ReplacementPreparedReference is not null &&
            state.ReplacementReferenceSynchronizedSetCount <= 1 &&
            Volatile.Read(ref state.ReplacementSwitchRequested) == 0;
    }

    internal void CancelReferenceRelock()
    {
        var label = callbacks.SelectedSetLabel();
        if (label is null || !sessions.TryGetState(label, out var state))
        {
            return;
        }

        lock (synchronizedActionGate)
        {
            lock (state.ReplacementReferenceGate)
            {
                if (state.ReplacementReferenceSynchronizedSetCount > 1)
                {
                    callbacks.PublishStatus($"{label} 属于多集合同步参考动作；请使用全部取消。");
                    return;
                }

                if (!state.ReplacementReferenceCollecting)
                {
                    return;
                }

                state.ClearReplacementPreparation();
            }
        }

        workspace.ReferenceRelockStateText = $"重锁：已取消；当前参考 e{state.ReferenceEpoch} 从未中断。";
        callbacks.PublishReferenceSummary(
            label,
            $"重锁已取消：继续使用原参考 e{state.ReferenceEpoch}，成像与 ROI 未发生切换或分段。");
        callbacks.PublishStatus($"{label} 重锁已取消；原参考 e{state.ReferenceEpoch} 保持有效。");
        QueueLog($"{DateTime.Now:HH:mm:ss} {label} replacement reference cancelled activeEpoch={state.ReferenceEpoch}");
        callbacks.RefreshPresentation();
    }

    internal bool CanCancelReferenceRelock()
    {
        var label = callbacks.SelectedSetLabel();
        return label is not null &&
            sessions.TryGetState(label, out var state) &&
            state.ReplacementReferenceCollecting &&
            state.ReplacementReferenceSynchronizedSetCount <= 1;
    }

    internal void PrepareSynchronizedReferences()
    {
        var activeStates = sessions.States
            .Where(state => state.IsActive)
            .OrderBy(state => state.SetLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activeStates.Length < 2)
        {
            callbacks.PublishStatus("多集合同步参考至少需要两个已有活动参考的实时集合。");
            return;
        }

        var blocked = activeStates
            .Where(state => state.Config is null || state.ReferenceVoltage208 is null || state.ReplacementReferenceCollecting)
            .Select(state => state.SetLabel)
            .ToArray();
        if (blocked.Length > 0)
        {
            workspace.SynchronizedReferenceSummary =
                $"多集合同步：以下运行集合尚未就绪或正在单独重锁：{string.Join(", ", blocked)}。";
            callbacks.PublishStatus("多集合同步不会静默排除任何运行集合。");
            return;
        }

        var actionAt = DateTimeOffset.Now;
        var actionGroupId = Guid.NewGuid().ToString("N");
        var windowsBySet = new Dictionary<string, IReadOnlyList<EcdCwrReferenceWindow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in activeStates)
        {
            lock (state.ReferenceCandidateGate)
            {
                var automaticWindow = state.ReferenceCandidateHistory.BuildAutomaticWindow(
                    actionAt,
                    MinimumReferenceFrames);
                windowsBySet[state.SetLabel] = automaticWindow is null ? [] : [automaticWindow];
            }
        }

        EcdCwrSynchronizedReferencePlan plan;
        try
        {
            plan = EcdCwrSynchronizedReferencePlanner.Create(actionGroupId, actionAt, windowsBySet);
        }
        catch (InvalidOperationException ex)
        {
            workspace.SynchronizedReferenceSummary = $"多集合同步：准备失败；{ex.Message}";
            callbacks.PublishStatus("多集合同步参考未启动：至少一个集合缺少共同操作前的完整高质量窗口。");
            return;
        }

        var byLabel = activeStates.ToDictionary(state => state.SetLabel, StringComparer.OrdinalIgnoreCase);
        var prepared = new List<SynchronizedPreparedReference>(activeStates.Length);
        try
        {
            foreach (var selection in plan.Selections)
            {
                var state = byLabel[selection.SetLabel];
                IReadOnlyList<EcdCwrRobustReferenceObservation> observations;
                DemodulatedFrame[] frames;
                lock (state.ReferenceCandidateGate)
                {
                    observations = state.ReferenceCandidateHistory.ResolveObservations(selection.Window);
                    frames = selection.Window.SourceCandidateIds
                        .Where(state.ReferenceCandidateFrameBySourceId.ContainsKey)
                        .Select(sourceId => state.ReferenceCandidateFrameBySourceId[sourceId])
                        .ToArray();
                }

                prepared.Add(new SynchronizedPreparedReference(
                    state,
                    selection,
                    new EcdCwrRobustReferenceBuilder().CreateFromObservations(
                        observations,
                        RealtimeReferenceLifecycleController.CreateRobustReferenceOptions(state.Config!)),
                    frames));
            }
        }
        catch (InvalidOperationException ex)
        {
            workspace.SynchronizedReferenceSummary = $"多集合同步：参考计算失败；{ex.Message}";
            callbacks.PublishStatus("多集合同步参考未启动；所有集合继续使用原参考。");
            return;
        }

        lock (synchronizedActionGate)
        {
            if (prepared.Any(item => !item.State.IsActive ||
                item.State.ReferenceVoltage208 is null ||
                item.State.ReplacementReferenceCollecting))
            {
                callbacks.PublishStatus("多集合同步准备期间运行状态已变化；未修改任何活动参考。");
                return;
            }

            foreach (var item in prepared)
            {
                lock (item.State.ReplacementReferenceGate)
                {
                    item.State.BeginReplacementPreparation(plan.CommonActionAt);
                    if (!item.State.SetPreparedReplacement(
                            item.Reference,
                            item.Selection.Window,
                            item.Frames,
                            "user_selected",
                            plan.ActionGroupId,
                            plan.CommonActionAt,
                            item.Selection.WindowSkewMilliseconds,
                            prepared.Count))
                    {
                        throw new InvalidOperationException(
                            $"{item.State.SetLabel} synchronized reference preparation was canceled.");
                    }
                }
            }
        }

        workspace.SynchronizedReferenceSummary =
            $"多集合同步待确认 · action {plan.ActionGroupId[..8]} · {plan.CommonActionAt.ToLocalTime():HH:mm:ss.fff}\n" +
            string.Join(
                "\n",
                prepared.Select(item =>
                    $"{item.State.SetLabel}: {item.Selection.Window.StartedAt.ToLocalTime():HH:mm:ss.fff}–" +
                    $"{item.Selection.Window.EndedAt.ToLocalTime():HH:mm:ss.fff} · " +
                    $"输入 {item.Selection.Window.FrameCount} / 保留 {item.Reference.FrameCount} / 剔除 {item.Reference.RejectedFrameCount} 帧 · " +
                    $"窗口 skew {item.Selection.WindowSkewMilliseconds / 1000.0:+0.000;-0.000;0.000}s"));
        workspace.ReferenceRelockStateText =
            $"多集合同步：{prepared.Count} 个集合均已准备；旧参考继续运行，请统一确认或取消。";
        callbacks.PublishStatus($"多集合同步参考已准备：{prepared.Count} 个集合，尚未切换。");
        QueueLog($"{DateTime.Now:HH:mm:ss} synchronized reference prepared action={plan.ActionGroupId} sets={prepared.Count}");
        callbacks.RefreshPresentation();
    }

    internal bool CanPrepareSynchronizedReferences()
    {
        var activeStates = sessions.States.Where(state => state.IsActive).ToArray();
        return activeStates.Length >= 2 &&
            activeStates.All(state => state.Config is not null &&
                state.ReferenceVoltage208 is not null &&
                !state.ReplacementReferenceCollecting) &&
            !sessions.States.Any(state => state.ReplacementReferenceActionGroupId is not null);
    }

    internal void ConfirmSynchronizedReferenceSwitch()
    {
        RealtimeRunState[] states;
        string groupId;
        lock (synchronizedActionGate)
        {
            states = GetPreparedSynchronizedReferenceStates();
            if (!IsCompleteSynchronizedReferenceGroup(states))
            {
                callbacks.PublishStatus("多集合同步确认失败：准备组不完整，未切换任何集合。");
                return;
            }

            foreach (var state in states)
            {
                if (!state.RequestReplacementSwitch())
                {
                    callbacks.PublishStatus($"{state.SetLabel} 同步参考尚未准备完成。");
                    return;
                }
            }

            groupId = states[0].ReplacementReferenceActionGroupId!;
        }

        workspace.SynchronizedReferenceSummary =
            $"多集合同步已确认 · action {groupId[..8]} · 各集合等待自己的下一有效目标边界；实际 switch skew 将写入 epoch。";
        callbacks.PublishStatus($"已统一确认 {states.Length} 个集合的新参考。");
        callbacks.RefreshPresentation();
    }

    internal bool CanConfirmSynchronizedReferenceSwitch()
    {
        lock (synchronizedActionGate)
        {
            var states = GetPreparedSynchronizedReferenceStates();
            return IsCompleteSynchronizedReferenceGroup(states) &&
                states.All(state => Volatile.Read(ref state.ReplacementSwitchRequested) == 0);
        }
    }

    internal void CancelSynchronizedReferenceRelock()
    {
        RealtimeRunState[] states;
        string groupId;
        lock (synchronizedActionGate)
        {
            states = GetPreparedSynchronizedReferenceStates();
            if (!IsCompleteSynchronizedReferenceGroup(states))
            {
                callbacks.PublishStatus("多集合同步取消不可用：部分集合已完成切换，请按 epoch 审计结果处理。");
                return;
            }

            groupId = states[0].ReplacementReferenceActionGroupId!;
            foreach (var state in states)
            {
                lock (state.ReplacementReferenceGate)
                {
                    state.ClearReplacementPreparation();
                }
            }
        }

        workspace.SynchronizedReferenceSummary =
            $"多集合同步已取消 · action {groupId[..8]} · 所有原参考从未中断。";
        workspace.ReferenceRelockStateText = "多集合同步：已取消；各集合保留原参考。";
        callbacks.PublishStatus($"已取消 {states.Length} 个集合的同步重锁。");
        callbacks.RefreshPresentation();
    }

    internal bool CanCancelSynchronizedReferenceRelock()
    {
        lock (synchronizedActionGate)
        {
            return IsCompleteSynchronizedReferenceGroup(GetPreparedSynchronizedReferenceStates());
        }
    }

    internal void SetReferenceRelockStateText(string text) => workspace.ReferenceRelockStateText = text;

    internal void SetSynchronizedReferenceSummary(string text) => workspace.SynchronizedReferenceSummary = text;

    private void UseReference(bool useSelectedWindow)
    {
        var label = callbacks.SelectedSetLabel();
        if (label is null || !sessions.TryGetState(label, out var state) || !state.IsActive)
        {
            callbacks.PublishStatus("当前没有可锁定参考的实时采集。");
            return;
        }

        if (state.Config is not { } config)
        {
            callbacks.PublishStatus($"{label} 参考数据暂不可用：实时运行配置尚未就绪。");
            return;
        }

        var preparingReplacement = state.ReplacementReferenceCollecting && state.ReferenceVoltage208 is not null;
        if (preparingReplacement && state.ReplacementReferenceSynchronizedSetCount > 1)
        {
            callbacks.PublishStatus($"{label} 属于多集合同步参考动作；请使用统一确认或全部取消。");
            return;
        }

        if (!preparingReplacement && state.ReferenceVoltage208 is not null && !state.ReferenceIsProvisional)
        {
            callbacks.PublishStatus($"{label} 当前不是可升级的快速预览参考；如需换参考，请先使用“重锁参考”。");
            return;
        }

        var actionAt = DateTimeOffset.Now;
        EcdCwrReferenceWindow? selectedWindow;
        if (useSelectedWindow)
        {
            if (state.SelectedReferenceWindow is null)
            {
                RefreshWindowOptions(label);
            }

            selectedWindow = state.SelectedReferenceWindow;
        }
        else
        {
            lock (state.ReferenceCandidateGate)
            {
                selectedWindow = state.ReferenceCandidateHistory.BuildAutomaticWindow(
                    actionAt,
                    MinimumReferenceFrames);
                state.AutomaticReferenceWindow = selectedWindow;
            }
        }

        if (selectedWindow is null)
        {
            var count = Volatile.Read(ref state.ReferenceCandidateContinuousCount);
            callbacks.PublishStatus(useSelectedWindow
                ? $"{label} 尚未选择完整的历史参考区间。"
                : $"{label} 最近同工况连续高质量参考候选不足：{count}/{MinimumReferenceFrames}。");
            return;
        }

        IReadOnlyList<EcdCwrRobustReferenceObservation> observations;
        try
        {
            lock (state.ReferenceCandidateGate)
            {
                observations = state.ReferenceCandidateHistory.ResolveObservations(selectedWindow);
                state.PendingSelectedReferenceFrames = selectedWindow.SourceCandidateIds
                    .Where(state.ReferenceCandidateFrameBySourceId.ContainsKey)
                    .Select(sourceId => state.ReferenceCandidateFrameBySourceId[sourceId])
                    .ToArray();
            }
        }
        catch (InvalidOperationException ex)
        {
            RefreshWindowOptions(label);
            callbacks.PublishStatus($"{label} 所选参考窗口已不可用：{ex.Message} 请重新选择。");
            return;
        }

        if (preparingReplacement)
        {
            EcdCwrRobustReference preparedReference;
            try
            {
                preparedReference = new EcdCwrRobustReferenceBuilder().CreateFromObservations(
                    observations,
                    RealtimeReferenceLifecycleController.CreateRobustReferenceOptions(config));
            }
            catch (InvalidOperationException ex)
            {
                callbacks.PublishStatus($"{label} 新参考准备失败：{ex.Message}");
                return;
            }

            lock (synchronizedActionGate)
            {
                lock (state.ReplacementReferenceGate)
                {
                    if (!state.ReplacementReferenceCollecting)
                    {
                        callbacks.PublishStatus($"{label} 重锁已取消；当前参考 e{state.ReferenceEpoch} 保持不变。");
                        return;
                    }

                    if (state.ReplacementReferenceSynchronizedSetCount > 1)
                    {
                        callbacks.PublishStatus($"{label} 已进入多集合同步参考动作；未改动该同步组。");
                        return;
                    }

                    if (!state.SetPreparedReplacement(
                            preparedReference,
                            selectedWindow,
                            state.PendingSelectedReferenceFrames,
                            "user_selected",
                            Guid.NewGuid().ToString("N"),
                            actionAt,
                            (selectedWindow.EffectiveReferenceAt - actionAt).TotalMilliseconds,
                            synchronizedSetCount: 1))
                    {
                        callbacks.PublishStatus($"{label} 重锁已取消；当前参考 e{state.ReferenceEpoch} 保持不变。");
                        return;
                    }
                }
            }

            workspace.ReferenceRelockStateText =
                $"重锁：新参考已准备（输入 {selectedWindow.FrameCount}，稳健保留 {preparedReference.FrameCount}，剔除 {preparedReference.RejectedFrameCount} 帧），旧 e{state.ReferenceEpoch} 仍在运行；请确认切换或取消。";
            callbacks.PublishReferenceSummary(
                label,
                $"新参考待切换：{(useSelectedWindow ? "高级所选历史区间" : "点击前自动汇总区间")} " +
                $"{selectedWindow.StartedAt.ToLocalTime():HH:mm:ss}–{selectedWindow.EndedAt.ToLocalTime():HH:mm:ss}，" +
                $"输入 {selectedWindow.FrameCount} 帧、稳健保留 {preparedReference.FrameCount} 帧、剔除 {preparedReference.RejectedFrameCount} 帧；" +
                $"当前 e{state.ReferenceEpoch} 继续正常成像与 ROI。请明确确认或取消。");
            callbacks.PublishStatus($"{label} 新参考已准备，尚未切换；旧参考 e{state.ReferenceEpoch} 保持有效。");
            QueueLog($"{DateTime.Now:HH:mm:ss} {label} replacement reference prepared mode={(useSelectedWindow ? "expert-history" : "preclick-auto")} window={selectedWindow.WindowId} input={selectedWindow.FrameCount} retained={preparedReference.FrameCount} rejected={preparedReference.RejectedFrameCount} activeEpoch={state.ReferenceEpoch}");
            callbacks.RefreshPresentation();
            return;
        }

        state.PendingSelectedReferenceWindow = selectedWindow;
        state.PendingSelectedReferenceObservations = observations.ToArray();
        Interlocked.Exchange(ref state.ManualReferenceLockRequested, 1);
        callbacks.PublishReferenceSummary(
            label,
            $"用户锁定参考已请求：{(useSelectedWindow ? "使用高级所选历史区间" : "已冻结点击时刻并自动汇总此前最近同工况连续段")}，" +
            $"将综合 {selectedWindow.StartedAt.ToLocalTime():HH:mm:ss}–{selectedWindow.EndedAt.ToLocalTime():HH:mm:ss} 的全部 {selectedWindow.FrameCount} 个质量合格帧建立正式参考；" +
            $"对象是否静止与稳定性指标仅作提示，不降低成像置信度。");
        callbacks.PublishStatus($"{label} 正在用已有高质量数据建立用户参考；下一有效目标开始正常成像与 ROI。");
        QueueLog($"{DateTime.Now:HH:mm:ss} {label} user-selected reference requested mode={(useSelectedWindow ? "expert-history" : "preclick-auto")} cutoff={actionAt:O} window={selectedWindow.WindowId} frames={selectedWindow.FrameCount}");
        callbacks.RefreshPresentation();
    }

    private void QueueLog(string message) => callbacks.QueueLog(message);

    private RealtimeRunState[] GetPreparedSynchronizedReferenceStates()
    {
        var groups = sessions.States
            .Where(state => state.ReplacementReferenceCollecting && state.ReplacementReferenceActionGroupId is not null)
            .GroupBy(state => state.ReplacementReferenceActionGroupId!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToArray();
        return groups.Length == 1
            ? groups[0].OrderBy(state => state.SetLabel, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    private static bool IsCompleteSynchronizedReferenceGroup(IReadOnlyList<RealtimeRunState> states)
    {
        return states.Count >= 2 && states.All(state =>
            state.IsActive &&
            state.ReplacementPreparedReference is not null &&
            state.ReplacementReferenceSynchronizedSetCount == states.Count);
    }

    private sealed record SynchronizedPreparedReference(
        RealtimeRunState State,
        EcdCwrSynchronizedReferenceSelection Selection,
        EcdCwrRobustReference Reference,
        DemodulatedFrame[] Frames);
}

public sealed record RealtimeReferenceWindowOption(EcdCwrReferenceWindow Window)
{
    public string Preview =>
        $"{Window.StartedAt.ToLocalTime():HH:mm:ss}–{Window.EndedAt.ToLocalTime():HH:mm:ss} · " +
        $"{Window.FrameCount} 帧 · 漂移 {Window.DriftPerMinute * 100.0:F4}%/min · " +
        $"间隙 {Window.GapCount} · 饱和 {Window.SaturationCount} · 接触 {Window.ContactEvidence} · " +
        $"来源 {(Window.UsesPersistedCandidates ? "持久化+内存" : "内存")}";

    public override string ToString() => Preview;
}
