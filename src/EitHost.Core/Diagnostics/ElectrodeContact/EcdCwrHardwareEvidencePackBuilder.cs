using System.Text;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrHardwareEvidencePackBuilder
{
    public const string EvidenceJsonRelativePath = "ecd-cwr-hardware-evidence.json";
    public const string EvidenceMarkdownRelativePath = "ecd-cwr-hardware-evidence.md";

    public static EcdCwrHardwareEvidencePackPlan CreatePlan(string? evidenceSetId = null)
    {
        var evidence = EcdCwrHardwareEvidenceTemplateBuilder.CreateTemplate(evidenceSetId);
        var files = new List<EcdCwrHardwareEvidencePackFile>
        {
            new("README.md", CreateReadme(evidence)),
            new("expected-artifacts.md", CreateExpectedArtifacts()),
            new("protocols/01-single-electrode-loosened.md", CreateProtocol(
                "01 松脱单电极",
                "验证单个电极接触劣化时，ECD-CWR 能在 48 点主动证据中快速定位该电极，并把相关测量降权。",
                [
                    "全绿状态下采集 qc_ref 与 v_ref，并记录基线版本。",
                    "选择一个非边界特殊位置的电极，逐步松脱或垫高，保持采集至少 20 帧。",
                    "记录电极环颜色、fault_type、硬故障/软故障检出帧号、红恢复帧号。",
                    "恢复电极接触，确认 UI 要求重采参考帧后重新采集 qc_ref 与 v_ref。"
                ],
                [
                    "`single_electrode_loosened = true`",
                    "`hard_fault_detection_delay_frames` 或 `soft_fault_detection_delay_frames`",
                    "`red_recovery_delay_frames`",
                    "`artifacts` 中加入原始帧、截图和回放报告"
                ])),
            new("protocols/02-partial-contact-membrane.md", CreateProtocol(
                "02 半接触/垫膜",
                "验证轻度、稳定、线性接触阻抗升高时，系统不会只依赖互易性而漏判。",
                [
                    "全绿状态下采集 qc_ref 与 v_ref。",
                    "在单电极下加入薄膜、垫片或降低导电膏接触面积，避免直接断路。",
                    "记录 48 点白化残差、黄色/红色状态、加权重构是否继续运行。",
                    "移除垫膜并完成红转绿后的参考重采流程。"
                ],
                [
                    "`partial_contact_membrane = true`",
                    "`soft_fault_detection_delay_frames <= 5`",
                    "`reference_recapture_workflow = true`",
                    "`artifacts` 中加入连续帧记录和重构置信度截图"
                ])),
            new("protocols/03-adjacent-dual.md", CreateProtocol(
                "03 邻双电极异常",
                "验证相邻两个电极同时劣化时，定位层能区分邻双，而不是合并成系统级误报。",
                [
                    "选择一对相邻电极，在全绿基线后同时降低接触质量。",
                    "记录两个电极的 EWMA 分数、排序 gap、是否触发 k 上限或系统级哨兵。",
                    "恢复其中一个电极，观察单电极残留定位是否正确。",
                    "恢复全部电极并重采参考。"
                ],
                [
                    "`adjacent_dual = true`",
                    "`red_recovery_delay_frames <= 3`",
                    "`artifacts` 中加入定位分数导出和 UI 截图"
                ])),
            new("protocols/04-cable-loose.md", CreateProtocol(
                "04 线缆松动",
                "验证单行独抬或链路型异常会被标记为线缆/激励对链路，而不是误把单个电极标红。",
                [
                    "全绿基线后，轻微松动某一激励对相关线缆或连接端。",
                    "确认 fault_type 指向 pairlink/cable，而不是单电极接触。",
                    "记录是否出现 ADC 饱和、Top3 拓扑异常或系统级哨兵。",
                    "恢复线缆后重新采集参考。"
                ],
                [
                    "`cable_loose = true`",
                    "`artifacts` 中加入 fault_type 截图、原始帧和现场照片"
                ])),
            new("protocols/05-switch-channel-abnormal.md", CreateProtocol(
                "05 开关/采集通道异常",
                "验证采集通道、AFE 或 MUX 类异常能和物理电极接触故障分开提示。",
                [
                    "在可控条件下制造或模拟某测量通道异常。",
                    "记录 x_measchannel 或等价通道证据是否高于电极接触分支。",
                    "确认 UI 提示检查通道/AFE/MUX，而不是只提示调整电极。",
                    "恢复通道后运行证据审计。"
                ],
                [
                    "`switch_channel_abnormal = true`",
                    "`artifacts` 中加入通道诊断截图、日志和回放结果"
                ])),
            new("protocols/06-current-source-near-compliance.md", CreateProtocol(
                "06 电流源 near-compliance",
                "验证电流源接近 compliance 或输出受限时，会进入系统级或电流源类故障，而不是逐电极乱报。",
                [
                    "仅在硬件安全允许时执行；若 P0.4 无遥测能力，记录不可测原因。",
                    "逐步提高负载或降低接触条件，使电流源接近 compliance。",
                    "记录实际注入电流幅相、compliance 标志或替代哨兵证据。",
                    "确认系统级报警优先于稀疏电极定位。"
                ],
                [
                    "可测且通过：`current_source_near_compliance_disposition = passed`",
                    "不可测：`current_source_near_compliance_disposition = unavailable`，并填写 `current_source_near_compliance_reason`",
                    "`artifacts` 中加入遥测、日志或不可测说明"
                ])),
            new("protocols/07-conductive-gel-drying.md", CreateProtocol(
                "07 导电膏渐干长时程",
                "验证全局缓慢漂移和局部接触退化不会互相污染，基线生命周期能阻止漂移积累假红。",
                [
                    "完成全绿基线后，保持装置运行长时程采集。",
                    "记录每 5 至 10 分钟的接触分数、系统级哨兵、qc_ref 更新时间。",
                    "若出现红转绿，确认旧参考作废并重采。",
                    "保存完整数据库、审计 JSON 和趋势截图。"
                ],
                [
                    "`conductive_gel_drying = true`",
                    "`collected_at`",
                    "`artifacts` 中加入长时程原始数据和趋势图"
                ])),
            new("protocols/08-reference-recapture-workflow.md", CreateProtocol(
                "08 调整后重采参考流程",
                "验证人工调整电极后，系统不会继续使用带旧接触状态的 v_ref/qc_ref。",
                [
                    "制造一次红色接触异常，并记录红色状态。",
                    "人工调整到全绿，观察 UI 是否强制提示重采参考。",
                    "未重采前尝试继续重构，记录是否显示低置信度或阻止发布。",
                    "完成重采后确认基线版本号变化。"
                ],
                [
                    "`reference_recapture_workflow = true`",
                    "`red_recovery_delay_frames <= 3`",
                    "`artifacts` 中加入调整前后参考版本、截图和回放报告"
                ])),
            new("protocols/09-traceability-replay.md", CreateProtocol(
                "09 任意帧可追溯回放",
                "验证现场记录的任意关键帧都能离线重放出同样的状态、分数、权重和图像置信度。",
                [
                    "从每类硬件实验中选取至少 1 个关键帧。",
                    "运行 ecd-cwr-traceability-verify 或等价回放流程。",
                    "比对 states、fault_type、scores、weights、Q_image 和基线版本。",
                    "把回放 JSON/Markdown 加入硬件证据 artifacts。"
                ],
                [
                    "`traceability_replay_verified = true`",
                    "`artifacts` 中加入 traceability 回放报告"
                ]))
        };

        return new EcdCwrHardwareEvidencePackPlan(
            evidence,
            EvidenceJsonRelativePath,
            EvidenceMarkdownRelativePath,
            files);
    }

    private static string CreateReadme(EcdCwrHardwareValidationEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR P4.2 硬件证据包");
        builder.AppendLine();
        builder.AppendLine($"- 证据集：`{evidence.EvidenceSetId}`");
        builder.AppendLine($"- 证据 JSON：`{EvidenceJsonRelativePath}`");
        builder.AppendLine($"- 证据清单：`{EvidenceMarkdownRelativePath}`");
        builder.AppendLine("- 实验协议：`protocols/*.md`");
        builder.AppendLine();
        builder.AppendLine("## 使用顺序");
        builder.AppendLine();
        builder.AppendLine("1. 按 `protocols/` 中的 9 份协议完成现场实验。");
        builder.AppendLine("2. 把真实原始帧、截图、日志、数据库、回放报告放入本目录或子目录。");
        builder.AppendLine("3. 修改 `ecd-cwr-hardware-evidence.json`，只把真实完成的实验字段改成 true/false，并把真实附件路径写入 `artifacts`；near-compliance 使用 disposition + reason。");
        builder.AppendLine("4. 运行 `dotnet run --project src/EitHost.Tools -- ecd-cwr-hardware-evidence-audit --hardware-evidence <本目录/ecd-cwr-hardware-evidence.json> --base-dir <本目录>`。");
        builder.AppendLine("5. 审核通过后，再运行 `ecd-cwr-validate-all` 进入总验收。");
        builder.AppendLine();
        builder.AppendLine("## 防误通过规则");
        builder.AppendLine();
        builder.AppendLine("- 本包生成的 README、协议文件和 `expected-artifacts.md` 只用于指导实验，不应写入 `artifacts` 当作真实证据。");
        builder.AppendLine("- `null` 表示未完成；`false` 表示已完成但未通过，会让审计失败。");
        builder.AppendLine("- near-compliance 明确不可测时使用 `unavailable`，且必须填写不可测原因；不要用 false 表示不可测。");
        builder.AppendLine("- 红转绿后的 `v_ref/qc_ref` 重采记录必须作为附件保留。");
        return builder.ToString();
    }

    private static string CreateExpectedArtifacts()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# P4.2 推荐真实附件");
        builder.AppendLine();
        builder.AppendLine("这些附件路径应在实验完成后写入 `ecd-cwr-hardware-evidence.json` 的 `artifacts`。");
        builder.AppendLine();
        builder.AppendLine("- 每类实验的原始 256 点复数帧数据库或 HDF5。");
        builder.AppendLine("- 对应的 UI 电极环截图或录屏。");
        builder.AppendLine("- `ecd-cwr-hardware-evidence-audit.json/md`。");
        builder.AppendLine("- `ecd-cwr-traceability-verify` 生成的回放 JSON/Markdown。");
        builder.AppendLine("- 现场照片、线缆/通道标记、基线版本记录。");
        builder.AppendLine("- 若某项不可测，提供不可测原因和替代哨兵证据。");
        return builder.ToString();
    }

    private static string CreateProtocol(
        string title,
        string purpose,
        IReadOnlyList<string> steps,
        IReadOnlyList<string> evidenceFields)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine("## 目的");
        builder.AppendLine();
        builder.AppendLine(purpose);
        builder.AppendLine();
        builder.AppendLine("## 步骤");
        builder.AppendLine();
        for (var index = 0; index < steps.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {steps[index]}");
        }

        builder.AppendLine();
        builder.AppendLine("## 需要回填");
        builder.AppendLine();
        foreach (var field in evidenceFields)
        {
            builder.AppendLine($"- {field}");
        }

        builder.AppendLine();
        builder.AppendLine("## 判读提醒");
        builder.AppendLine();
        builder.AppendLine("- 空字段保持 `null`，不要为了总验收把未做实验写成 true。");
        builder.AppendLine("- 若实验失败但流程已完成，应写 false，并在 notes 中说明失败现象。");
        builder.AppendLine("- 协议文件本身不算真实实验附件。");
        return builder.ToString();
    }
}

public sealed record EcdCwrHardwareEvidencePackPlan(
    EcdCwrHardwareValidationEvidence Evidence,
    string EvidenceJsonRelativePath,
    string EvidenceMarkdownRelativePath,
    IReadOnlyList<EcdCwrHardwareEvidencePackFile> Files);

public sealed record EcdCwrHardwareEvidencePackFile(
    string RelativePath,
    string Content);
