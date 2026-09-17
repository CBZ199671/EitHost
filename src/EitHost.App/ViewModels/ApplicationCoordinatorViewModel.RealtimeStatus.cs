using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel
{
    private static string CreateRealtimeReferenceModeStatus(
        RealtimeImagingRunConfig config,
        RealtimeRunState state)
    {
        var scale = config.ReferenceScalePolicy == EcdCwrReferenceScalePolicy.CommonScaleNormalized
            ? "公共尺度归一化"
            : "保留物理尺度";
        if (state.ReferenceInvalidated)
        {
            return state.ReplacementReferenceCollecting
                ? $"参考模式：已失效 · 后台重锁收集中 · {scale}"
                : $"参考模式：已失效 · 等待重锁参考 · {scale}";
        }

        if (state.ReplacementReferenceCollecting)
        {
            var scope = state.ReplacementReferenceSynchronizedSetCount > 1
                ? $"多集合 action {state.ReplacementReferenceActionGroupId?[..8]}"
                : "单集合";
            var readiness = state.ReplacementPreparedReference is null
                ? "后台收集中"
                : Volatile.Read(ref state.ReplacementSwitchRequested) == 0
                    ? "新参考待确认"
                    : "已确认，待有效边界";
            return $"参考模式：当前 e{state.ReferenceEpoch} 保持活动 · {scope} {readiness} · {scale}";
        }

        if (state.StartupDegradedReference is not null)
        {
            return $"参考模式：故障降级 · {scale}";
        }

        if (state.ReferenceIsProvisional)
        {
            return $"参考模式：快速预览（临时） · {scale}";
        }

        if (state.ReferenceVoltage208 is null)
        {
            return $"参考模式：尚未锁定 · 候选窗口收集中 · {scale}";
        }

        return string.Equals(state.ActiveReferenceLockKind, "user_selected", StringComparison.Ordinal)
            ? $"参考模式：用户选定高质量窗口 · 正常置信 · {scale}"
            : $"参考模式：自动稳定锁定 · {scale}";
    }
}
