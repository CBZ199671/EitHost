using System.Globalization;
using System.Text;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrValidationReportBuilder
{
    private const double HealthyBoundaryFalseRedTarget = 0.005;
    private const double SingleElectrodeTop1Target = 0.99;
    private const double AdjacentDualSeparationTarget = 0.95;
    private const double FaultTypeAccuracyTarget = 0.90;
    private const double ContactSubspaceAucTarget = 0.90;
    private const double MultiFrequencyFalseRedReductionTarget = 0.30;
    private const double ImageQualitySpearmanTarget = 0.80;
    private const double HardDetectionDelayFrameTarget = 1.0;
    private const double SoftDetectionDelayFrameTarget = 5.0;
    private const double RedRecoveryDelayFrameTarget = 3.0;
    private const double SevereBadFramePassThroughTarget = 0.0;

    public EcdCwrValidationReport Build(
        EcdCwrSimulationScoreReport score,
        EcdCwrSimulationDatasetValidationReport? datasetValidation = null,
        EcdCwrHardwareValidationEvidence? hardwareEvidence = null,
        EcdCwrTraceabilityReplayVerificationReport? traceabilityReplay = null,
        EcdCwrDynamicSequenceAcceptanceReport? dynamicAcceptance = null,
        EcdCwrRong2026BaselineRunReport? rong2026Baseline = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        var metrics = new List<EcdCwrValidationMetric>
        {
            BuildDatasetMetric(datasetValidation),
            BuildBooleanMetric(
                "coverage_complete",
                "仿真预测覆盖完整性",
                "无缺失预测，预测数覆盖全部 work item",
                score.CoverageComplete,
                $"{score.PredictionCount}/{score.WorkItemCount} predictions, missing={score.MissingPredictionCount}",
                "P4.1 仿真矩阵"),
            BuildLessThanMetric(
                "healthy_boundary_high_false_red",
                "健康+边界高对比目标假红率",
                HealthyBoundaryFalseRedTarget,
                score.HealthyBoundaryHighFalseRedRate,
                "P4 指标 1",
                strict: true),
            BuildGreaterOrEqualMetric(
                "single_electrode_top1",
                "单电极定位 top-1",
                SingleElectrodeTop1Target,
                score.SingleElectrodeTop1Accuracy,
                "P4 指标 2"),
            BuildGreaterOrEqualMetric(
                "adjacent_dual_separation",
                "邻双电极分离率",
                AdjacentDualSeparationTarget,
                score.AdjacentDualSeparationRate,
                "P4 指标 3"),
            BuildGreaterOrEqualMetric(
                "fault_type_accuracy",
                "fault_type 分类准确率",
                FaultTypeAccuracyTarget,
                score.FaultTypeAccuracy,
                "P4 指标 4"),
            BuildGreaterOrEqualMetric(
                "contact_subspace_auc",
                "P3.2 接触子空间判别 AUC",
                ContactSubspaceAucTarget,
                score.ContactSubspaceAuc,
                "P3.2/P4.1 接触子空间仿真",
                formatAsPercent: false,
                minimumPairCount: 2,
                actualPairCount: score.ContactSubspaceScoredCount),
            BuildReconstructionMetric(score.ReconstructionComparison),
            BuildGreaterOrEqualMetric(
                "image_quality_spearman",
                "Q_image 与 weighted CC Spearman",
                ImageQualitySpearmanTarget,
                score.ImageQualityWeightedCcSpearman,
                "P4 指标 9",
                formatAsPercent: false,
                minimumPairCount: 3,
                actualPairCount: score.ImageQualityWeightedCcPairCount),
            BuildMultiFrequencyMetric(score.MultiFrequencyFalseRedImprovement)
        };

        metrics.AddRange(BuildHardwareChecklistMetrics(hardwareEvidence));
        metrics.Add(BuildLessOrEqualMetric(
            "hard_fault_detection_delay",
            "硬故障检测延迟",
            HardDetectionDelayFrameTarget,
            hardwareEvidence?.HardFaultDetectionDelayFrames,
            "P4 指标 5",
            "帧"));
        metrics.Add(BuildLessOrEqualMetric(
            "soft_fault_detection_delay",
            "软故障检测延迟",
            SoftDetectionDelayFrameTarget,
            hardwareEvidence?.SoftFaultDetectionDelayFrames,
            "P4 指标 5",
            "帧"));
        metrics.Add(BuildLessOrEqualMetric(
            "red_recovery_delay",
            "红转绿恢复延迟",
            RedRecoveryDelayFrameTarget,
            hardwareEvidence?.RedRecoveryDelayFrames,
            "P4 指标 6",
            "帧"));
        metrics.Add(BuildLessOrEqualMetric(
            "severe_bad_frame_pass_through",
            "严重坏帧误放行率",
            SevereBadFramePassThroughTarget,
            hardwareEvidence?.SevereBadFramePassThroughRate,
            "P4 指标 8",
            "rate"));
        metrics.Add(BuildTraceabilityMetric(score.WorkItemCount, traceabilityReplay));
        metrics.Add(BuildDynamicAcceptanceMetric(dynamicAcceptance));
        metrics.Add(BuildRong2026BaselineMetric(score.WorkItemCount, rong2026Baseline));

        var p41Status = Aggregate(metrics.Where(metric => metric.Id is
            "dataset_integrity" or
            "coverage_complete" or
            "healthy_boundary_high_false_red" or
            "single_electrode_top1" or
            "adjacent_dual_separation" or
            "fault_type_accuracy" or
            "contact_subspace_auc" or
            "rong2026_executable_baseline" or
            "weighted_reconstruction_baselines" or
            "image_quality_spearman" or
            "multifrequency_false_red_reduction"));
        var p42Status = hardwareEvidence is null
            ? EcdCwrValidationStatus.Incomplete
            : Aggregate(metrics.Where(metric =>
                metric.Id.StartsWith("hardware_", StringComparison.Ordinal) ||
                metric.Id is
                    "hard_fault_detection_delay" or
                    "soft_fault_detection_delay" or
                    "red_recovery_delay" or
                    "severe_bad_frame_pass_through"));
        var overallStatus = Aggregate(metrics);
        var missingEvidence = metrics
            .Where(metric => metric.Status == EcdCwrValidationStatus.Incomplete)
            .Select(metric => $"{metric.Name}: {metric.Actual}")
            .ToArray();

        return new EcdCwrValidationReport(
            DateTimeOffset.Now,
            overallStatus,
            p41Status,
            p42Status,
            overallStatus,
            score.WorkItemCount,
            score.PredictionCount,
            score.CoverageComplete,
            datasetValidation is null
                ? null
                : new EcdCwrDatasetValidationSnapshot(
                    datasetValidation.WorkItemCount,
                    datasetValidation.PassedItems,
                    datasetValidation.FailedItems,
                    datasetValidation.MissingHdf5,
                    datasetValidation.MissingLabel,
                    datasetValidation.Passed),
            hardwareEvidence,
            metrics,
            missingEvidence,
            traceabilityReplay is null
                ? null
                : new EcdCwrTraceabilityValidationSnapshot(
                    traceabilityReplay.VerifiedAt,
                    traceabilityReplay.ExpectedPredictionCount,
                    traceabilityReplay.ReplayedPredictionCount,
                    traceabilityReplay.PassedItems,
                    traceabilityReplay.FailedItems,
                    traceabilityReplay.Passed,
                    traceabilityReplay.ExpectedDiagnosticPolicyVersion,
                    traceabilityReplay.ReplayedDiagnosticPolicyVersion),
            rong2026Baseline is null
                ? null
                : new EcdCwrRongBaselineValidationSnapshot(
                    rong2026Baseline.PolicyVersion,
                    rong2026Baseline.ManifestWorkItemCount,
                    rong2026Baseline.AnalyzedItems,
                    rong2026Baseline.FailedItems,
                    rong2026Baseline.FullCoveragePassed,
                    rong2026Baseline.HealthyBoundaryHighFalsePositiveRate,
                    rong2026Baseline.SingleElectrodeTop1Accuracy,
                    rong2026Baseline.AdjacentDualSeparationRate));
    }

    public static string ToMarkdown(EcdCwrValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR P4 总验收报告");
        builder.AppendLine();
        builder.AppendLine($"- 结论：{FormatStatus(report.OverallStatus)}");
        builder.AppendLine($"- 生成时间：{report.GeneratedAt:O}");
        builder.AppendLine($"- P4.1 仿真全矩阵：{FormatStatus(report.P41SimulationMatrixStatus)}");
        builder.AppendLine($"- P4.2 硬件实验全清单：{FormatStatus(report.P42HardwareChecklistStatus)}");
        builder.AppendLine($"- P4.3 总验收：{FormatStatus(report.P43AcceptanceStatus)}");
        builder.AppendLine($"- Score 覆盖：{report.ScorePredictionCount}/{report.ScoreWorkItemCount}");
        builder.AppendLine();
        builder.AppendLine("## 指标总表");
        builder.AppendLine();
        builder.AppendLine("|id|指标|目标|当前|状态|证据|");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var metric in report.Metrics)
        {
            builder.AppendLine(
                $"|{metric.Id}|{metric.Name}|{metric.Target}|{metric.Actual}|{FormatStatus(metric.Status)}|{metric.Evidence}|");
        }

        builder.AppendLine();
        builder.AppendLine("## Rong 2026 对照");
        builder.AppendLine();
        AppendRongComparison(builder, report);
        builder.AppendLine();
        builder.AppendLine("## 缺失证据");
        builder.AppendLine();
        if (report.MissingEvidence.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var missing in report.MissingEvidence)
            {
                builder.AppendLine($"- {missing}");
            }
        }

        if (report.HardwareEvidence?.Artifacts is { Count: > 0 } artifacts)
        {
            builder.AppendLine();
            builder.AppendLine("## 硬件证据文件");
            foreach (var artifact in artifacts)
            {
                var marker = EcdCwrHardwareArtifactClassifier.IsExperimentArtifact(artifact)
                    ? ""
                    : "（辅助文件，不计入真实实验附件）";
                builder.AppendLine($"- {artifact}{marker}");
            }
        }

        return builder.ToString();
    }

    private static void AppendRongComparison(StringBuilder builder, EcdCwrValidationReport report)
    {
        builder.AppendLine("验收口径使用同场景可执行对照：ECD-CWR 加权重构必须优于剔帧 CD、静态替换 SR 与 Rong 2026 论文式模板补偿。DRM 作为额外互易类强 baseline 保留展示；SR/DRM 不再替代 Rong 复现证据。");
        builder.AppendLine();
        builder.AppendLine("|对照维度|Rong 2026 / 论文式路线|ECD-CWR 当前量化结果|结论|");
        builder.AppendLine("|---|---|---|---|");
        AppendComparisonRow(
            builder,
            "观测信息",
            "重构侧 208 点；激励相关 48 点不作为主证据",
            "完整 16×16=256 点复数观测；48 点用于稳定接触阻抗主动诊断",
            "硬件能力增强");
        AppendComparisonRow(
            builder,
            "U 形/目标假红",
            "U 形拓扑一致是假设；本项目仿真中 13 点严格单调约 93.5% 通过，存在目标诱发假阳性",
            MetricText(report, "healthy_boundary_high_false_red"),
            MetricStatusText(report, "healthy_boundary_high_false_red"));
        AppendComparisonRow(
            builder,
            "多频接触消歧",
            "论文路线未利用本设备多频旁路证据",
            MetricText(report, "multifrequency_false_red_reduction"),
            MetricStatusText(report, "multifrequency_false_red_reduction"));
        AppendComparisonRow(
            builder,
            "接触/目标子空间消歧",
            "互易 + U 形 + 208 稀疏定位，互易一致不能排除稳定线性接触阻抗",
            MetricText(report, "contact_subspace_auc"),
            MetricStatusText(report, "contact_subspace_auc"));
        AppendComparisonRow(
            builder,
            "异常数据处理",
            MetricText(report, "rong2026_executable_baseline"),
            MetricText(report, "weighted_reconstruction_baselines"),
            MetricStatusText(report, "weighted_reconstruction_baselines"));
        AppendComparisonRow(
            builder,
            "可追溯性",
            "论文离线流程为主，现场逐帧审计需另建",
            MetricText(report, "traceability_replay_verified"),
            MetricStatusText(report, "traceability_replay_verified"));
    }

    private static void AppendComparisonRow(
        StringBuilder builder,
        string dimension,
        string baseline,
        string current,
        string conclusion)
    {
        builder.AppendLine($"|{dimension}|{baseline}|{current}|{conclusion}|");
    }

    private static string MetricText(EcdCwrValidationReport report, string metricId)
    {
        var metric = report.Metrics.FirstOrDefault(item => string.Equals(item.Id, metricId, StringComparison.Ordinal));
        return metric is null
            ? "未纳入本报告"
            : $"{metric.Actual}（目标：{metric.Target}）";
    }

    private static string MetricStatusText(EcdCwrValidationReport report, string metricId)
    {
        var metric = report.Metrics.FirstOrDefault(item => string.Equals(item.Id, metricId, StringComparison.Ordinal));
        return metric is null
            ? "未验证"
            : FormatStatus(metric.Status);
    }

    private static EcdCwrValidationMetric BuildDatasetMetric(EcdCwrSimulationDatasetValidationReport? report)
    {
        if (report is null)
        {
            return Incomplete(
                "dataset_integrity",
                "仿真 HDF5/label 数据集完整性",
                "全部 HDF5 与 label 通过结构校验",
                "未提供 ecd-cwr-sim-validate 报告",
                "P4.1 仿真矩阵");
        }

        return new EcdCwrValidationMetric(
            "dataset_integrity",
            "仿真 HDF5/label 数据集完整性",
            "全部 HDF5 与 label 通过结构校验",
            $"passed={report.PassedItems}, failed={report.FailedItems}, missing_hdf5={report.MissingHdf5}, missing_label={report.MissingLabel}",
            report.Passed ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            "P4.1 仿真矩阵");
    }

    private static IEnumerable<EcdCwrValidationMetric> BuildHardwareChecklistMetrics(
        EcdCwrHardwareValidationEvidence? evidence)
    {
        if (evidence is null)
        {
            yield return Incomplete(
                "hardware_checklist",
                "硬件实验全清单",
                "P4.2 七类硬件实验与重采流程均有证据",
                "未提供硬件证据 JSON",
                "P4.2 硬件实验");
            yield break;
        }

        yield return BuildNullableBooleanMetric(
            "hardware_single_loosened",
            "松脱单电极实验",
            "通过",
            evidence.SingleElectrodeLoosened,
            "P4.2 硬件实验");
        yield return BuildNullableBooleanMetric(
            "hardware_partial_contact",
            "半接触/垫膜实验",
            "通过",
            evidence.PartialContactMembrane,
            "P4.2 硬件实验");
        yield return BuildNullableBooleanMetric(
            "hardware_adjacent_dual",
            "邻双电极异常实验",
            "通过",
            evidence.AdjacentDual,
            "P4.2 硬件实验");
        yield return BuildNullableBooleanMetric(
            "hardware_cable_loose",
            "线缆松动实验",
            "通过",
            evidence.CableLoose,
            "P4.2 硬件实验");
        yield return BuildNullableBooleanMetric(
            "hardware_switch_channel",
            "开关/采集通道异常实验",
            "通过",
            evidence.SwitchChannelAbnormal,
            "P4.2 硬件实验");
        yield return BuildNearComplianceMetric(evidence);
        yield return BuildNullableBooleanMetric(
            "hardware_conductive_gel_drying",
            "导电膏渐干长时程实验",
            "通过",
            evidence.ConductiveGelDrying,
            "P4.2 硬件实验");
        yield return BuildNullableBooleanMetric(
            "hardware_reference_recapture",
            "调整后强制重采参考流程",
            "通过",
            evidence.ReferenceRecaptureWorkflow,
            "P4.2 硬件实验");
        yield return BuildArtifactListMetric(evidence.Artifacts);
    }

    private static EcdCwrValidationMetric BuildArtifactListMetric(IReadOnlyList<string>? artifacts)
    {
        if (artifacts is not { Count: > 0 })
        {
            return Incomplete(
                "hardware_artifacts",
                "硬件证据附件列表",
                "至少 1 个实验记录/截图/数据库/回放报告附件",
                "未提供",
            "P4.2 硬件实验");
        }

        var experimentArtifacts = EcdCwrHardwareArtifactClassifier.GetExperimentArtifacts(artifacts);
        if (experimentArtifacts.Count == 0)
        {
            return new EcdCwrValidationMetric(
                "hardware_artifacts",
                "硬件证据附件列表",
                "至少 1 个实验记录/截图/数据库/回放报告附件",
                $"{artifacts.Count} auxiliary artifacts, 0 experiment artifacts",
                EcdCwrValidationStatus.Failed,
                "P4.2 硬件实验");
        }

        return new EcdCwrValidationMetric(
            "hardware_artifacts",
            "硬件证据附件列表",
            "至少 1 个实验记录/截图/数据库/回放报告附件",
            $"{experimentArtifacts.Count}/{artifacts.Count} experiment artifacts",
            EcdCwrValidationStatus.Passed,
            "P4.2 硬件实验");
    }

    private static EcdCwrValidationMetric BuildNearComplianceMetric(
        EcdCwrHardwareValidationEvidence evidence)
    {
        const string id = "hardware_current_source_compliance";
        const string name = "电流源近 compliance 实验";
        const string target = "通过或明确不可测";
        const string source = "P4.2 硬件实验";
        if (evidence.CurrentSourceNearComplianceDisposition is { } disposition)
        {
            return disposition switch
            {
                EcdCwrEvidenceDisposition.Passed => new EcdCwrValidationMetric(
                    id, name, target, "passed", EcdCwrValidationStatus.Passed, source),
                EcdCwrEvidenceDisposition.Failed => new EcdCwrValidationMetric(
                    id, name, target, "failed", EcdCwrValidationStatus.Failed, source),
                EcdCwrEvidenceDisposition.Unavailable when !string.IsNullOrWhiteSpace(
                    evidence.CurrentSourceNearComplianceReason) => new EcdCwrValidationMetric(
                        id,
                        name,
                        target,
                        $"unavailable: {evidence.CurrentSourceNearComplianceReason.Trim()}",
                        EcdCwrValidationStatus.Passed,
                        source),
                EcdCwrEvidenceDisposition.Unavailable => new EcdCwrValidationMetric(
                    id,
                    name,
                    target,
                    "unavailable 缺少原因",
                    EcdCwrValidationStatus.Failed,
                    source),
                _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
            };
        }

        return BuildNullableBooleanMetric(
            id,
            name,
            target,
            evidence.CurrentSourceNearCompliance,
            source);
    }

    private static EcdCwrValidationMetric BuildTraceabilityMetric(
        int expectedWorkItemCount,
        EcdCwrTraceabilityReplayVerificationReport? report)
    {
        const string id = "traceability_replay_verified";
        const string name = "任意帧判决可追溯回放";
        const string target = "全量预测分数、状态、fault_type、Q_image 可重放";
        const string source = "P4 指标 10 / P4.3";
        if (report is null)
        {
            return Incomplete(id, name, target, "未提供全量回放报告", source);
        }

        var actual = $"expected={report.ExpectedPredictionCount}/{expectedWorkItemCount}, " +
            $"replayed={report.ReplayedPredictionCount}, failed={report.FailedItems}, " +
            $"policy={report.ExpectedDiagnosticPolicyVersion ?? "missing"}/{report.ReplayedDiagnosticPolicyVersion ?? "missing"}";
        if (report.ExpectedPredictionCount != expectedWorkItemCount ||
            report.ReplayedPredictionCount != expectedWorkItemCount)
        {
            return Incomplete(id, name, target, $"部分回放：{actual}", source);
        }

        if (!string.Equals(
                report.ExpectedDiagnosticPolicyVersion,
                EcdCwrDiagnosticPolicy.CurrentVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.ReplayedDiagnosticPolicyVersion,
                EcdCwrDiagnosticPolicy.CurrentVersion,
                StringComparison.Ordinal))
        {
            return new EcdCwrValidationMetric(
                id,
                name,
                target,
                $"策略版本不匹配：{actual}",
                EcdCwrValidationStatus.Failed,
                source);
        }

        return new EcdCwrValidationMetric(
            id,
            name,
            target,
            actual,
            report.Passed ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            source);
    }

    private static EcdCwrValidationMetric BuildDynamicAcceptanceMetric(
        EcdCwrDynamicSequenceAcceptanceReport? report)
    {
        const string id = "dynamic_sequence_acceptance";
        const string name = "P3.6 固定滞后/动态 Kalman 序列验收";
        const string target = "脉冲抑制>=90%，阶跃偏差<5%，峰时误差<=2 块，总延迟=2，E5/E12 不回归";
        const string source = "P3.6 / V233";
        if (report is null)
        {
            return Incomplete(id, name, target, "未提供动态序列验收报告", source);
        }

        if (report.HostTemporal is null ||
            report.BackendKalman is null ||
            report.ContactNonRegression is null ||
            report.HostTemporal.OutputLatencyBlocks is null ||
            report.BackendKalman.TotalLatencyFrames is null)
        {
            return new EcdCwrValidationMetric(
                id,
                name,
                target,
                "动态序列验收报告结构不完整",
                EcdCwrValidationStatus.Failed,
                source);
        }

        var contact = string.Join(
            ",",
            report.ContactNonRegression.Select(row =>
                $"E{row.ExpectedElectrode}:diff={row.ActionDifferenceCount}:pass={row.Passed}"));
        var actual = string.Format(
            CultureInfo.InvariantCulture,
            "host_supp={0:P4}, backend_supp={1:P4}, host_step={2:P4}, backend_step={3:P4}, host_peak={4}, backend_peak={5}, latency={6}/{7}, contact={8}",
            report.HostTemporal.IsolatedSuppression,
            report.BackendKalman.IsolatedSuppression,
            report.HostTemporal.StepSteadyStateBias,
            report.BackendKalman.StepSteadyStateBias,
            report.HostTemporal.MaximumPeakTimeErrorBlocks,
            report.BackendKalman.MaximumPeakTimeErrorBlocks,
            string.Join(',', report.HostTemporal.OutputLatencyBlocks),
            string.Join(',', report.BackendKalman.TotalLatencyFrames),
            contact);
        return new EcdCwrValidationMetric(
            id,
            name,
            target,
            actual,
            EcdCwrDynamicSequenceAcceptanceBuilder.IsPassingReport(report)
                ? EcdCwrValidationStatus.Passed
                : EcdCwrValidationStatus.Failed,
            source);
    }

    private static EcdCwrValidationMetric BuildRong2026BaselineMetric(
        int expectedWorkItemCount,
        EcdCwrRong2026BaselineRunReport? report)
    {
        const string id = "rong2026_executable_baseline";
        const string name = "Rong 2026 可执行论文复现";
        const string target = "208 互易+U 形+L1+gap+模板补偿，全矩阵覆盖，假设与策略版本可追溯";
        const string source = "Rong et al. 2026 / P4.3 / V234,V240-V242";
        if (report is null)
        {
            return Incomplete(id, name, target, "未提供 Rong 2026 可执行 baseline 报告", source);
        }

        var actual = string.Format(
            CultureInfo.InvariantCulture,
            "coverage={0}/{1}, failed={2}, policy={3}, boundary_false_positive={4:P4}, single_top1={5:P4}, adjacent_dual={6:P4}",
            report.AnalyzedItems,
            report.ManifestWorkItemCount,
            report.FailedItems,
            report.PolicyVersion,
            report.HealthyBoundaryHighFalsePositiveRate,
            report.SingleElectrodeTop1Accuracy,
            report.AdjacentDualSeparationRate);
        var options = new EcdCwrRong2026Options();
        var expectedPolicy = EcdCwrRong2026Baseline.CreatePolicyVersion(options);
        var valid = string.Equals(
                report.SchemaVersion,
                EcdCwrRong2026Baseline.SchemaVersion,
                StringComparison.Ordinal) &&
            string.Equals(report.SourceDoi, EcdCwrRong2026Baseline.SourceDoi, StringComparison.Ordinal) &&
            string.Equals(report.PolicyVersion, expectedPolicy, StringComparison.Ordinal) &&
            string.Equals(
                report.Equation7Interpretation,
                EcdCwrRong2026Baseline.Equation7Interpretation,
                StringComparison.Ordinal) &&
            report.OperationalAssumptions.SequenceEqual(
                EcdCwrRong2026Baseline.DescribeAssumptions(options),
                StringComparer.Ordinal) &&
            report.ManifestWorkItemCount == expectedWorkItemCount &&
            report.SelectedItems == expectedWorkItemCount &&
            report.AnalyzedItems == expectedWorkItemCount &&
            report.Predictions.Select(item => item.ScenarioId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == expectedWorkItemCount &&
            report.Predictions.All(prediction =>
                string.Equals(prediction.DiagnosticPolicyVersion, expectedPolicy, StringComparison.Ordinal)) &&
            report.FailedItems == 0 &&
            report.SkippedMissing == 0 &&
            report.ExecutionPassed &&
            report.FullCoverage &&
            report.FullCoveragePassed;
        return new EcdCwrValidationMetric(
            id,
            name,
            target,
            actual,
            valid ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            source);
    }

    private static EcdCwrValidationMetric BuildReconstructionMetric(EcdCwrReconstructionComparisonSummary summary)
    {
        var methodText = summary.Methods.Count == 0
            ? "无 CC 对照"
            : string.Join(
                ", ",
                summary.Methods.Select(method =>
                    $"{method.Method}={method.MeanCorrelation.ToString("F4", CultureInfo.InvariantCulture)}"));
        var missingBaselines = EcdCwrReconstructionMethods.RequiredBaselines
            .Where(method => summary.Methods.All(item => !string.Equals(item.Method, method, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingBaselines.Length > 0 ||
            summary.Methods.All(item => !string.Equals(item.Method, EcdCwrReconstructionMethods.Weighted, StringComparison.OrdinalIgnoreCase)))
        {
            return Incomplete(
                "weighted_reconstruction_baselines",
                "加权重构 CC 优于剔帧/静态/Rong 模板补偿",
                "weighted > CD/SR/Rong; DRM reported",
                $"{methodText}; missing={string.Join(",", missingBaselines)}",
                "P4 指标 7");
        }

        return new EcdCwrValidationMetric(
            "weighted_reconstruction_baselines",
            "加权重构 CC 优于剔帧/静态/Rong 模板补偿",
            "weighted > CD/SR/Rong; DRM reported",
            methodText,
            summary.Ready ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            "P4 指标 7");
    }

    private static EcdCwrValidationMetric BuildMultiFrequencyMetric(
        EcdCwrMultiFrequencyFalseRedImprovement? improvement)
    {
        if (improvement is null)
        {
            return Incomplete(
                "multifrequency_false_red_reduction",
                "多频消歧假红率相对下降",
                $">={FormatPercent(MultiFrequencyFalseRedReductionTarget)}",
                "未提供 P2 baseline 预测对照",
                "P3.1/P4.1 多频 CEM 仿真");
        }

        var actual = improvement.RelativeReduction is { } reduction && double.IsFinite(reduction)
            ? $"{FormatPercent(reduction)} (n={improvement.ComparedScenarioCount}, baseline={FormatPercent(improvement.BaselineHealthyBoundaryHighFalseRedRate)}, current={FormatPercent(improvement.CurrentHealthyBoundaryHighFalseRedRate)})"
            : $"n={improvement.ComparedScenarioCount}, baseline={FormatPercent(improvement.BaselineHealthyBoundaryHighFalseRedRate)}, current={FormatPercent(improvement.CurrentHealthyBoundaryHighFalseRedRate)}, reduction=n/a";
        if (improvement.RelativeReduction is not { } finite || !double.IsFinite(finite))
        {
            if (improvement.BaselineHealthyBoundaryHighFalseRedRate <= 0.0 &&
                improvement.CurrentHealthyBoundaryHighFalseRedRate <= 0.0 &&
                improvement.ComparedScenarioCount > 0)
            {
                return new EcdCwrValidationMetric(
                    "multifrequency_false_red_reduction",
                    "多频消歧假红率相对下降",
                    $">={FormatPercent(MultiFrequencyFalseRedReductionTarget)} or baseline/current both 0 false-red",
                    actual,
                    EcdCwrValidationStatus.Passed,
                    "P3.1/P4.1 多频 CEM 仿真");
            }

            return Incomplete(
                "multifrequency_false_red_reduction",
                "多频消歧假红率相对下降",
                $">={FormatPercent(MultiFrequencyFalseRedReductionTarget)}",
                actual,
                "P3.1/P4.1 多频 CEM 仿真");
        }

        return new EcdCwrValidationMetric(
            "multifrequency_false_red_reduction",
            "多频消歧假红率相对下降",
            $">={FormatPercent(MultiFrequencyFalseRedReductionTarget)}",
            actual,
            finite >= MultiFrequencyFalseRedReductionTarget
                ? EcdCwrValidationStatus.Passed
                : EcdCwrValidationStatus.Failed,
            "P3.1/P4.1 多频 CEM 仿真");
    }

    private static EcdCwrValidationMetric BuildBooleanMetric(
        string id,
        string name,
        string target,
        bool value,
        string actual,
        string evidence)
    {
        return new EcdCwrValidationMetric(
            id,
            name,
            target,
            actual,
            value ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            evidence);
    }

    private static EcdCwrValidationMetric BuildNullableBooleanMetric(
        string id,
        string name,
        string target,
        bool? value,
        string evidence)
    {
        if (value is null)
        {
            return Incomplete(id, name, target, "未提供", evidence);
        }

        return new EcdCwrValidationMetric(
            id,
            name,
            target,
            value.Value ? "true" : "false",
            value.Value ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            evidence);
    }

    private static EcdCwrValidationMetric BuildLessThanMetric(
        string id,
        string name,
        double target,
        double actual,
        string evidence,
        bool strict = false)
    {
        if (!double.IsFinite(actual))
        {
            return Incomplete(id, name, $"<{FormatPercent(target)}", "n/a", evidence);
        }

        var passed = strict ? actual < target : actual <= target;
        return new EcdCwrValidationMetric(
            id,
            name,
            $"<{FormatPercent(target)}",
            FormatPercent(actual),
            passed ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            evidence);
    }

    private static EcdCwrValidationMetric BuildGreaterOrEqualMetric(
        string id,
        string name,
        double target,
        double? actual,
        string evidence,
        bool formatAsPercent = true,
        int minimumPairCount = 0,
        int? actualPairCount = null)
    {
        var targetText = formatAsPercent ? FormatPercent(target) : FormatScalar(target);
        if (actual is not { } finite || !double.IsFinite(finite))
        {
            return Incomplete(id, name, $">={targetText}", "n/a", evidence);
        }

        var actualText = formatAsPercent ? FormatPercent(finite) : FormatScalar(finite);
        if (actualPairCount is { } pairCount)
        {
            actualText = $"{actualText} (pairs={pairCount})";
        }

        if (minimumPairCount > 0 && actualPairCount is not null && actualPairCount < minimumPairCount)
        {
            return Incomplete(id, name, $">={targetText}", actualText, evidence);
        }

        return new EcdCwrValidationMetric(
            id,
            name,
            $">={targetText}",
            actualText,
            finite >= target ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            evidence);
    }

    private static EcdCwrValidationMetric BuildLessOrEqualMetric(
        string id,
        string name,
        double target,
        double? actual,
        string evidence,
        string unit)
    {
        if (actual is not { } finite || !double.IsFinite(finite))
        {
            return Incomplete(id, name, $"<={FormatScalar(target)} {unit}", "未提供", evidence);
        }

        return new EcdCwrValidationMetric(
            id,
            name,
            $"<={FormatScalar(target)} {unit}",
            $"{FormatScalar(finite)} {unit}",
            finite <= target ? EcdCwrValidationStatus.Passed : EcdCwrValidationStatus.Failed,
            evidence);
    }

    private static EcdCwrValidationMetric Incomplete(
        string id,
        string name,
        string target,
        string actual,
        string evidence)
    {
        return new EcdCwrValidationMetric(
            id,
            name,
            target,
            actual,
            EcdCwrValidationStatus.Incomplete,
            evidence);
    }

    private static EcdCwrValidationStatus Aggregate(IEnumerable<EcdCwrValidationMetric> metrics)
    {
        var list = metrics.ToArray();
        if (list.Any(metric => metric.Status == EcdCwrValidationStatus.Failed))
        {
            return EcdCwrValidationStatus.Failed;
        }

        return list.Any(metric => metric.Status == EcdCwrValidationStatus.Incomplete)
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

    private static string FormatPercent(double value)
    {
        return value.ToString("P2", CultureInfo.InvariantCulture);
    }

    private static string FormatScalar(double value)
    {
        return value.ToString("G4", CultureInfo.InvariantCulture);
    }
}

public enum EcdCwrValidationStatus
{
    Passed = 0,
    Failed = 1,
    Incomplete = 2
}

public enum EcdCwrEvidenceDisposition
{
    Passed = 0,
    Failed = 1,
    Unavailable = 2
}

public sealed record EcdCwrValidationReport(
    DateTimeOffset GeneratedAt,
    EcdCwrValidationStatus OverallStatus,
    EcdCwrValidationStatus P41SimulationMatrixStatus,
    EcdCwrValidationStatus P42HardwareChecklistStatus,
    EcdCwrValidationStatus P43AcceptanceStatus,
    int ScoreWorkItemCount,
    int ScorePredictionCount,
    bool ScoreCoverageComplete,
    EcdCwrDatasetValidationSnapshot? DatasetValidation,
    EcdCwrHardwareValidationEvidence? HardwareEvidence,
    IReadOnlyList<EcdCwrValidationMetric> Metrics,
    IReadOnlyList<string> MissingEvidence,
    EcdCwrTraceabilityValidationSnapshot? TraceabilityReplay = null,
    EcdCwrRongBaselineValidationSnapshot? Rong2026Baseline = null);

public sealed record EcdCwrRongBaselineValidationSnapshot(
    string PolicyVersion,
    int ManifestWorkItemCount,
    int AnalyzedItems,
    int FailedItems,
    bool FullCoveragePassed,
    double HealthyBoundaryHighFalsePositiveRate,
    double SingleElectrodeTop1Accuracy,
    double AdjacentDualSeparationRate);

public sealed record EcdCwrTraceabilityValidationSnapshot(
    DateTimeOffset VerifiedAt,
    int ExpectedPredictionCount,
    int ReplayedPredictionCount,
    int PassedItems,
    int FailedItems,
    bool Passed,
    string? ExpectedDiagnosticPolicyVersion = null,
    string? ReplayedDiagnosticPolicyVersion = null);

public sealed record EcdCwrDatasetValidationSnapshot(
    int WorkItemCount,
    int PassedItems,
    int FailedItems,
    int MissingHdf5,
    int MissingLabel,
    bool Passed);

public sealed record EcdCwrValidationMetric(
    string Id,
    string Name,
    string Target,
    string Actual,
    EcdCwrValidationStatus Status,
    string Evidence);

public sealed record EcdCwrHardwareValidationEvidence(
    string? EvidenceSetId = null,
    DateTimeOffset? CollectedAt = null,
    bool? SingleElectrodeLoosened = null,
    bool? PartialContactMembrane = null,
    bool? AdjacentDual = null,
    bool? CableLoose = null,
    bool? SwitchChannelAbnormal = null,
    bool? CurrentSourceNearCompliance = null,
    bool? ConductiveGelDrying = null,
    bool? ReferenceRecaptureWorkflow = null,
    double? HardFaultDetectionDelayFrames = null,
    double? SoftFaultDetectionDelayFrames = null,
    double? RedRecoveryDelayFrames = null,
    double? SevereBadFramePassThroughRate = null,
    bool? TraceabilityReplayVerified = null,
    IReadOnlyList<string>? Artifacts = null,
    IReadOnlyList<string>? Notes = null,
    EcdCwrEvidenceDisposition? CurrentSourceNearComplianceDisposition = null,
    string? CurrentSourceNearComplianceReason = null);
