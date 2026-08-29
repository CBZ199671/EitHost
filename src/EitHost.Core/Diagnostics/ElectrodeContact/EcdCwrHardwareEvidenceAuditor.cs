using System.Text;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrHardwareEvidenceAuditor
{
    private const double HardDetectionDelayFrameTarget = 1.0;
    private const double SoftDetectionDelayFrameTarget = 5.0;
    private const double RedRecoveryDelayFrameTarget = 3.0;
    private const double SevereBadFramePassThroughTarget = 0.0;

    public EcdCwrHardwareEvidenceAuditReport Audit(
        EcdCwrHardwareValidationEvidence evidence,
        string? evidencePath = null,
        string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var resolvedBaseDirectory = ResolveBaseDirectory(evidencePath, baseDirectory);
        var items = new List<EcdCwrHardwareEvidenceAuditItem>
        {
            CheckText("evidence_set_id", "证据集 ID", evidence.EvidenceSetId),
            CheckTimestamp("collected_at", "采集时间", evidence.CollectedAt),
            CheckBoolean("single_electrode_loosened", "松脱单电极实验", evidence.SingleElectrodeLoosened),
            CheckBoolean("partial_contact_membrane", "半接触/垫膜实验", evidence.PartialContactMembrane),
            CheckBoolean("adjacent_dual", "邻双电极异常实验", evidence.AdjacentDual),
            CheckBoolean("cable_loose", "线缆松动实验", evidence.CableLoose),
            CheckBoolean("switch_channel_abnormal", "开关/采集通道异常实验", evidence.SwitchChannelAbnormal),
            CheckNearCompliance(evidence),
            CheckBoolean("conductive_gel_drying", "导电膏渐干长时程实验", evidence.ConductiveGelDrying),
            CheckBoolean("reference_recapture_workflow", "红转绿后强制重采参考流程", evidence.ReferenceRecaptureWorkflow),
            CheckLessOrEqual("hard_fault_detection_delay_frames", "硬故障检测延迟", evidence.HardFaultDetectionDelayFrames, HardDetectionDelayFrameTarget),
            CheckLessOrEqual("soft_fault_detection_delay_frames", "软故障检测延迟", evidence.SoftFaultDetectionDelayFrames, SoftDetectionDelayFrameTarget),
            CheckLessOrEqual("red_recovery_delay_frames", "红恢复延迟", evidence.RedRecoveryDelayFrames, RedRecoveryDelayFrameTarget),
            CheckLessOrEqual("severe_bad_frame_pass_through_rate", "严重坏帧误放行率", evidence.SevereBadFramePassThroughRate, SevereBadFramePassThroughTarget),
            CheckBoolean("traceability_replay_verified", "任意帧可追溯回放", evidence.TraceabilityReplayVerified)
        };
        items.AddRange(CheckArtifacts(evidence.Artifacts, resolvedBaseDirectory));
        var status = Aggregate(items);
        return new EcdCwrHardwareEvidenceAuditReport(
            DateTimeOffset.Now,
            evidencePath is null ? null : Path.GetFullPath(evidencePath),
            resolvedBaseDirectory,
            status,
            items);
    }

    public static string ToMarkdown(EcdCwrHardwareEvidenceAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR P4.2 硬件证据审核");
        builder.AppendLine();
        builder.AppendLine($"- 审核时间：{report.AuditedAt:O}");
        builder.AppendLine($"- 结论：{FormatStatus(report.Status)}");
        builder.AppendLine($"- 证据文件：`{report.EvidencePath ?? "未提供"}`");
        builder.AppendLine($"- 附件基准目录：`{report.BaseDirectory}`");
        builder.AppendLine();
        builder.AppendLine("|id|项目|状态|说明|");
        builder.AppendLine("|---|---|---|---|");
        foreach (var item in report.Items)
        {
            builder.AppendLine($"|{item.Id}|{item.Name}|{FormatStatus(item.Status)}|{item.Detail}|");
        }

        return builder.ToString();
    }

    private static string ResolveBaseDirectory(string? evidencePath, string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.GetFullPath(baseDirectory);
        }

        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(evidencePath)) ?? Environment.CurrentDirectory;
        }

        return Environment.CurrentDirectory;
    }

    private static EcdCwrHardwareEvidenceAuditItem CheckText(
        string id,
        string name,
        string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Incomplete(id, name, "未填写")
            : Passed(id, name, value.Trim());
    }

    private static EcdCwrHardwareEvidenceAuditItem CheckTimestamp(
        string id,
        string name,
        DateTimeOffset? value)
    {
        return value is null
            ? Incomplete(id, name, "未填写")
            : Passed(id, name, value.Value.ToString("O"));
    }

    private static EcdCwrHardwareEvidenceAuditItem CheckBoolean(
        string id,
        string name,
        bool? value)
    {
        return value switch
        {
            true => Passed(id, name, "true"),
            false => Failed(id, name, "false"),
            _ => Incomplete(id, name, "未填写")
        };
    }

    private static EcdCwrHardwareEvidenceAuditItem CheckNearCompliance(
        EcdCwrHardwareValidationEvidence evidence)
    {
        const string id = "current_source_near_compliance";
        const string name = "电流源 near-compliance 实验";
        if (evidence.CurrentSourceNearComplianceDisposition is not { } disposition)
        {
            return CheckBoolean(id, name, evidence.CurrentSourceNearCompliance);
        }

        return disposition switch
        {
            EcdCwrEvidenceDisposition.Passed => Passed(id, name, "passed"),
            EcdCwrEvidenceDisposition.Failed => Failed(id, name, "failed"),
            EcdCwrEvidenceDisposition.Unavailable when !string.IsNullOrWhiteSpace(
                evidence.CurrentSourceNearComplianceReason) => Passed(
                    id,
                    name,
                    $"unavailable: {evidence.CurrentSourceNearComplianceReason.Trim()}"),
            EcdCwrEvidenceDisposition.Unavailable => Failed(id, name, "unavailable 缺少原因"),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
        };
    }

    private static EcdCwrHardwareEvidenceAuditItem CheckLessOrEqual(
        string id,
        string name,
        double? value,
        double target)
    {
        if (value is not { } finite || !double.IsFinite(finite))
        {
            return Incomplete(id, name, "未填写");
        }

        return finite <= target
            ? Passed(id, name, $"{finite:G4} <= {target:G4}")
            : Failed(id, name, $"{finite:G4} > {target:G4}");
    }

    private static IEnumerable<EcdCwrHardwareEvidenceAuditItem> CheckArtifacts(
        IReadOnlyList<string>? artifacts,
        string baseDirectory)
    {
        if (artifacts is not { Count: > 0 })
        {
            yield return Incomplete("artifacts", "证据附件列表", "未填写");
            yield break;
        }

        yield return Passed("artifacts", "证据附件列表", $"{artifacts.Count} 个附件");
        var experimentArtifacts = EcdCwrHardwareArtifactClassifier.GetExperimentArtifacts(artifacts);
        yield return experimentArtifacts.Count > 0
            ? Passed("experiment_artifacts", "真实实验附件", $"{experimentArtifacts.Count} 个真实实验附件")
            : Failed("experiment_artifacts", "真实实验附件", "仅包含协议、模板或审计辅助文件，缺少原始帧/截图/数据库/回放报告");
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            if (string.IsNullOrWhiteSpace(artifact))
            {
                yield return Incomplete($"artifact_{index}", $"附件 {index + 1}", "路径为空");
                continue;
            }

            var fullPath = Path.IsPathRooted(artifact)
                ? Path.GetFullPath(artifact)
                : Path.GetFullPath(Path.Combine(baseDirectory, artifact));
            var exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            if (!exists)
            {
                yield return Failed($"artifact_{index}", $"附件 {index + 1}", $"不存在：{fullPath}");
                continue;
            }

            var detail = EcdCwrHardwareArtifactClassifier.IsExperimentArtifact(artifact)
                ? fullPath
                : $"{fullPath}（辅助文件，不计入真实实验附件）";
            yield return Passed($"artifact_{index}", $"附件 {index + 1}", detail);
        }
    }

    private static EcdCwrHardwareEvidenceAuditItem Passed(string id, string name, string detail)
    {
        return new EcdCwrHardwareEvidenceAuditItem(id, name, EcdCwrValidationStatus.Passed, detail);
    }

    private static EcdCwrHardwareEvidenceAuditItem Failed(string id, string name, string detail)
    {
        return new EcdCwrHardwareEvidenceAuditItem(id, name, EcdCwrValidationStatus.Failed, detail);
    }

    private static EcdCwrHardwareEvidenceAuditItem Incomplete(string id, string name, string detail)
    {
        return new EcdCwrHardwareEvidenceAuditItem(id, name, EcdCwrValidationStatus.Incomplete, detail);
    }

    private static EcdCwrValidationStatus Aggregate(IEnumerable<EcdCwrHardwareEvidenceAuditItem> items)
    {
        var list = items.ToArray();
        if (list.Any(item => item.Status == EcdCwrValidationStatus.Failed))
        {
            return EcdCwrValidationStatus.Failed;
        }

        return list.Any(item => item.Status == EcdCwrValidationStatus.Incomplete)
            ? EcdCwrValidationStatus.Incomplete
            : EcdCwrValidationStatus.Passed;
    }

    private static string FormatStatus(EcdCwrValidationStatus status)
    {
        return status switch
        {
            EcdCwrValidationStatus.Passed => "PASS",
            EcdCwrValidationStatus.Failed => "FAIL",
            EcdCwrValidationStatus.Incomplete => "INCOMPLETE",
            _ => status.ToString()
        };
    }
}

public sealed record EcdCwrHardwareEvidenceAuditReport(
    DateTimeOffset AuditedAt,
    string? EvidencePath,
    string BaseDirectory,
    EcdCwrValidationStatus Status,
    IReadOnlyList<EcdCwrHardwareEvidenceAuditItem> Items)
{
    public bool Passed => Status == EcdCwrValidationStatus.Passed;
}

public sealed record EcdCwrHardwareEvidenceAuditItem(
    string Id,
    string Name,
    EcdCwrValidationStatus Status,
    string Detail);
