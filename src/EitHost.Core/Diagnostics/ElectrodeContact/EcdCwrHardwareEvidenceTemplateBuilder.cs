using System.Text;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrHardwareEvidenceTemplateBuilder
{
    public static EcdCwrHardwareValidationEvidence CreateTemplate(string? evidenceSetId = null)
    {
        var id = string.IsNullOrWhiteSpace(evidenceSetId)
            ? $"tank-acceptance-{DateTimeOffset.Now:yyyyMMdd}-001"
            : evidenceSetId.Trim();

        return new EcdCwrHardwareValidationEvidence(
            EvidenceSetId: id,
            CollectedAt: null,
            SingleElectrodeLoosened: null,
            PartialContactMembrane: null,
            AdjacentDual: null,
            CableLoose: null,
            SwitchChannelAbnormal: null,
            CurrentSourceNearCompliance: null,
            ConductiveGelDrying: null,
            ReferenceRecaptureWorkflow: null,
            HardFaultDetectionDelayFrames: null,
            SoftFaultDetectionDelayFrames: null,
            RedRecoveryDelayFrames: null,
            SevereBadFramePassThroughRate: null,
            TraceabilityReplayVerified: null,
            Artifacts: [],
            CurrentSourceNearComplianceDisposition: null,
            CurrentSourceNearComplianceReason: null,
            Notes:
            [
                "所有 null 都会在 ecd-cwr-validate-all 中显示为 INCOMPLETE。",
                "只有真实水槽或硬件实验完成后，才把对应字段改为 true/false 或填入测量值。",
                "artifact 建议填写实验记录、截图、原始帧数据库或回放报告的相对路径。"
            ]);
    }

    public static string ToMarkdown(EcdCwrHardwareValidationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR P4.2 硬件实验证据清单");
        builder.AppendLine();
        builder.AppendLine($"- 证据集：`{evidence.EvidenceSetId ?? "未填写"}`");
        builder.AppendLine($"- 采集时间：{(evidence.CollectedAt is { } collectedAt ? collectedAt.ToString("O") : "未填写")}");
        builder.AppendLine();
        builder.AppendLine("|字段|实验/指标|状态|");
        builder.AppendLine("|---|---|---|");
        AppendBoolRow(builder, "single_electrode_loosened", "松脱单电极", evidence.SingleElectrodeLoosened);
        AppendBoolRow(builder, "partial_contact_membrane", "半接触/垫膜", evidence.PartialContactMembrane);
        AppendBoolRow(builder, "adjacent_dual", "邻双电极异常", evidence.AdjacentDual);
        AppendBoolRow(builder, "cable_loose", "线缆松动", evidence.CableLoose);
        AppendBoolRow(builder, "switch_channel_abnormal", "开关或采集通道异常", evidence.SwitchChannelAbnormal);
        AppendNearComplianceRow(builder, evidence);
        AppendBoolRow(builder, "conductive_gel_drying", "导电膏渐干长时程", evidence.ConductiveGelDrying);
        AppendBoolRow(builder, "reference_recapture_workflow", "红转绿后重采 v_ref/qc_ref 工作流", evidence.ReferenceRecaptureWorkflow);
        AppendNumberRow(builder, "hard_fault_detection_delay_frames", "硬故障检出延迟，目标 <= 1 帧", evidence.HardFaultDetectionDelayFrames);
        AppendNumberRow(builder, "soft_fault_detection_delay_frames", "软退化检出延迟，目标 <= 5 帧", evidence.SoftFaultDetectionDelayFrames);
        AppendNumberRow(builder, "red_recovery_delay_frames", "故障解除后红恢复延迟，目标 <= 3 帧", evidence.RedRecoveryDelayFrames);
        AppendNumberRow(builder, "severe_bad_frame_pass_through_rate", "严重坏帧误放行率，目标接近 0", evidence.SevereBadFramePassThroughRate);
        AppendBoolRow(builder, "traceability_replay_verified", "任意帧可回放判决依据", evidence.TraceabilityReplayVerified);
        builder.AppendLine();
        builder.AppendLine("## 证据附件");
        if (evidence.Artifacts is { Count: > 0 })
        {
            foreach (var artifact in evidence.Artifacts)
            {
                builder.AppendLine($"- `{artifact}`");
            }
        }
        else
        {
            builder.AppendLine("- 未填写");
        }

        if (evidence.Notes is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("## 备注");
            foreach (var note in evidence.Notes)
            {
                builder.AppendLine($"- {note}");
            }
        }

        return builder.ToString();
    }

    private static void AppendBoolRow(
        StringBuilder builder,
        string field,
        string name,
        bool? value)
    {
        builder.AppendLine($"|`{field}`|{name}|{FormatBool(value)}|");
    }

    private static void AppendNumberRow(
        StringBuilder builder,
        string field,
        string name,
        double? value)
    {
        var text = value is { } finite && double.IsFinite(finite)
            ? finite.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)
            : "未填写";
        builder.AppendLine($"|`{field}`|{name}|{text}|");
    }

    private static void AppendNearComplianceRow(
        StringBuilder builder,
        EcdCwrHardwareValidationEvidence evidence)
    {
        var status = evidence.CurrentSourceNearComplianceDisposition switch
        {
            EcdCwrEvidenceDisposition.Passed => "完成/通过",
            EcdCwrEvidenceDisposition.Failed => "完成/未通过",
            EcdCwrEvidenceDisposition.Unavailable when !string.IsNullOrWhiteSpace(
                evidence.CurrentSourceNearComplianceReason) =>
                $"明确不可测：{evidence.CurrentSourceNearComplianceReason.Trim()}",
            EcdCwrEvidenceDisposition.Unavailable => "不可测但未填写原因",
            _ => FormatBool(evidence.CurrentSourceNearCompliance)
        };
        builder.AppendLine($"|`current_source_near_compliance_disposition`|电流源接近 compliance|{status}|");
    }

    private static string FormatBool(bool? value)
    {
        return value switch
        {
            true => "完成/通过",
            false => "完成/未通过",
            _ => "未填写"
        };
    }
}
