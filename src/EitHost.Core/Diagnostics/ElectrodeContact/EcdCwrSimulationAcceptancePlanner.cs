using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationAcceptancePlanner
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public EcdCwrSimulationAcceptanceStatusReport Build(
        string outputDirectory,
        EcdCwrSimulationAcceptancePaths? paths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var root = Path.GetFullPath(outputDirectory);
        paths ??= EcdCwrSimulationAcceptancePaths.CreateDefault(root);
        var steps = CreateSteps(paths).ToArray();
        var next = steps.FirstOrDefault(step => !step.Ready);
        return new EcdCwrSimulationAcceptanceStatusReport(
            DateTimeOffset.Now,
            root,
            paths,
            steps,
            next?.Command,
            next is null);
    }

    public static string ToMarkdown(EcdCwrSimulationAcceptanceStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR P4 仿真验收状态");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{report.GeneratedAt:O}");
        builder.AppendLine($"- 输出目录：`{report.OutputDirectory}`");
        builder.AppendLine($"- 是否完成：{report.Complete}");
        builder.AppendLine($"- 下一条命令：`{report.NextCommand ?? "none"}`");
        builder.AppendLine();
        builder.AppendLine("|步骤|产物|已存在|ready|状态|命令|");
        builder.AppendLine("|---|---|---:|---:|---|---|");
        foreach (var step in report.Steps)
        {
            builder.AppendLine($"|{step.Name}|`{step.ArtifactPath}`|{step.Exists}|{step.Ready}|{step.Detail}|`{step.Command}`|");
        }

        return builder.ToString();
    }

    private static IEnumerable<EcdCwrSimulationAcceptanceStep> CreateSteps(
        EcdCwrSimulationAcceptancePaths paths)
    {
        var manifestScenarioCount = TryReadManifestScenarioCount(paths.ManifestPath);
        yield return Step(
            "plan",
            "P2.6/P4.1 场景清单",
            paths.ManifestPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-plan --output-dir \"{paths.OutputDirectory}\" --emit-contact-jacobian --emit-multi-frequency",
            () => InspectManifest(paths.ManifestPath));
        yield return Step(
            "run",
            "CEM 仿真数据校验",
            paths.DatasetValidationPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-run --manifest \"{paths.ManifestPath}\" --create-missing-requests --refresh-requests --skip-ready --continue-on-error --persistent-worker; dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-validate --manifest \"{paths.ManifestPath}\"",
            () => InspectDatasetValidation(paths.DatasetValidationPath, manifestScenarioCount));
        yield return Step(
            "baseline-replay",
            "P2 baseline 诊断预测",
            paths.BaselinePredictionsPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-replay --manifest \"{paths.ManifestPath}\" --diagnostic-profile p2-baseline --output-predictions \"{paths.BaselinePredictionsPath}\"",
            () => InspectPredictions(
                paths.BaselinePredictionsPath,
                manifestScenarioCount,
                EcdCwrDiagnosticPolicy.P2BaselineVersion));
        yield return Step(
            "replay",
            "ECD-CWR current 诊断预测",
            paths.PredictionsPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-replay --manifest \"{paths.ManifestPath}\" --diagnostic-profile ecd-cwr-current --output-predictions \"{paths.PredictionsPath}\"",
            () => InspectPredictions(
                paths.PredictionsPath,
                manifestScenarioCount,
                EcdCwrDiagnosticPolicy.CurrentVersion,
                requireMultiFrequency: true));
        yield return Step(
            "rong-baseline",
            "Rong 2026 可执行论文复现",
            paths.RongBaselineReportPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-rong-baseline --manifest \"{paths.ManifestPath}\" --output-predictions \"{paths.RongBaselinePredictionsPath}\" --output-json \"{paths.RongBaselineReportPath}\"",
            () => InspectRongBaseline(
                paths.RongBaselineReportPath,
                paths.RongBaselinePredictionsPath,
                manifestScenarioCount));
        yield return Step(
            "reconstruct",
            "重构结果引用",
            paths.ReconstructionReferencesPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-reconstruct --manifest \"{paths.ManifestPath}\" --predictions \"{paths.PredictionsPath}\" --output-references \"{paths.ReconstructionReferencesPath}\" --skip-existing --continue-on-error",
            () => InspectReconstructionReferences(paths.ReconstructionReferencesPath, manifestScenarioCount));
        yield return Step(
            "rong-reconstruct",
            "Rong 2026 有限接触模板重构",
            paths.RongReconstructionReferencesPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-reconstruct --manifest \"{paths.ManifestPath}\" --predictions \"{paths.PredictionsPath}\" --method {EcdCwrReconstructionMethods.Rong2026TemplateReplacement} --finite-contact-only --output-references \"{paths.RongReconstructionReferencesPath}\" --skip-existing --continue-on-error",
            () => InspectRongReconstructionReferences(
                paths.RongReconstructionReferencesPath,
                paths.ManifestPath));
        yield return Step(
            "cc",
            "重构 CC 对照",
            paths.ReconstructionComparisonPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-cc --manifest \"{paths.ManifestPath}\" --reconstruction-results \"{paths.ReconstructionReferencesPath}\" --reconstruction-results \"{paths.RongReconstructionReferencesPath}\" --output-json \"{paths.ReconstructionComparisonPath}\"",
            () => InspectReconstructionComparisons(paths.ReconstructionComparisonPath, paths.ManifestPath, manifestScenarioCount));
        yield return Step(
            "score",
            "P4 仿真指标",
            paths.ScorePath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-sim-score --manifest \"{paths.ManifestPath}\" --predictions \"{paths.PredictionsPath}\" --baseline-predictions \"{paths.BaselinePredictionsPath}\" --reconstruction-comparisons \"{paths.ReconstructionComparisonPath}\" --output-json \"{paths.ScorePath}\"",
            () => InspectScore(paths.ScorePath, manifestScenarioCount));
        yield return Step(
            "validate-all",
            "P4 总验收报告",
            paths.ValidationReportPath,
            $"dotnet run --project src/EitHost.Tools -- ecd-cwr-validate-all --score \"{paths.ScorePath}\" --dataset-validation \"{paths.DatasetValidationPath}\" --rong-baseline \"{paths.RongBaselineReportPath}\" --output-json \"{paths.ValidationReportPath}\"",
            () => InspectValidationReport(paths.ValidationReportPath));
    }

    private static EcdCwrSimulationAcceptanceStep Step(
        string id,
        string name,
        string artifactPath,
        string command,
        Func<EcdCwrArtifactReadiness> inspect)
    {
        var readiness = inspect();
        return new EcdCwrSimulationAcceptanceStep(
            id,
            name,
            artifactPath,
            readiness.Exists,
            readiness.Ready,
            readiness.Detail,
            command);
    }

    private static EcdCwrArtifactReadiness InspectManifest(string path)
    {
        return InspectJson<EcdCwrSimulationBatchManifest>(path, manifest =>
        {
            var ready = manifest.ScenarioCount > 0 &&
                manifest.WorkItems.Count == manifest.ScenarioCount &&
                manifest.EmitContactJacobian &&
                manifest.EmitMultiFrequency;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                ready,
                $"scenarios={manifest.ScenarioCount}, work_items={manifest.WorkItems.Count}, emit_contact_jacobian={manifest.EmitContactJacobian}, emit_multi_frequency={manifest.EmitMultiFrequency}");
        });
    }

    private static EcdCwrArtifactReadiness InspectDatasetValidation(
        string path,
        int? manifestScenarioCount)
    {
        return InspectJson<EcdCwrSimulationDatasetValidationReport>(path, report =>
        {
            var countMatches = manifestScenarioCount is null ||
                report.WorkItemCount == manifestScenarioCount.Value;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                report.Passed && countMatches,
                $"passed={report.PassedItems}/{report.WorkItemCount}, missing_hdf5={report.MissingHdf5}, missing_label={report.MissingLabel}");
        });
    }

    private static EcdCwrArtifactReadiness InspectPredictions(
        string path,
        int? manifestScenarioCount,
        string expectedPolicyVersion,
        bool requireMultiFrequency = false)
    {
        return InspectJsonArray<EcdCwrSimulationPrediction>(path, predictions =>
        {
            var unique = predictions
                .Select(item => item.ScenarioId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var countReady = manifestScenarioCount is not { } count || unique >= count;
            var multiFrequencyReady = !requireMultiFrequency ||
                predictions.Length > 0 &&
                predictions.All(item => item.MultiFrequencyPeerFrameCount > 0);
            var policyReady = predictions.Length > 0 && predictions.All(item => string.Equals(
                item.DiagnosticPolicyVersion,
                expectedPolicyVersion,
                StringComparison.Ordinal));
            var ready = countReady && multiFrequencyReady && policyReady;
            var multiFrequencyDetail = requireMultiFrequency
                ? multiFrequencyReady.ToString()
                : "not_required";
            return new EcdCwrArtifactReadiness(
                Exists: true,
                ready,
                $"predictions={unique}/{manifestScenarioCount?.ToString() ?? "unknown"}, multi_frequency={multiFrequencyDetail}, policy={PolicyDetail(predictions.Select(item => item.DiagnosticPolicyVersion), expectedPolicyVersion)}");
        });
    }

    private static EcdCwrArtifactReadiness InspectRongBaseline(
        string reportPath,
        string predictionsPath,
        int? manifestScenarioCount)
    {
        if (!File.Exists(predictionsPath))
        {
            return new EcdCwrArtifactReadiness(
                File.Exists(reportPath),
                false,
                "Rong baseline predictions missing");
        }

        var options = new EcdCwrRong2026Options();
        var expectedPolicy = EcdCwrRong2026Baseline.CreatePolicyVersion(options);
        var predictionReadiness = InspectPredictions(
            predictionsPath,
            manifestScenarioCount,
            expectedPolicy);
        return InspectJson<EcdCwrRong2026BaselineRunReport>(reportPath, report =>
        {
            var countMatches = manifestScenarioCount is null ||
                report.ManifestWorkItemCount == manifestScenarioCount.Value &&
                report.AnalyzedItems == manifestScenarioCount.Value;
            var policyReady = string.Equals(report.PolicyVersion, expectedPolicy, StringComparison.Ordinal) &&
                string.Equals(
                    report.Equation7Interpretation,
                    EcdCwrRong2026Baseline.Equation7Interpretation,
                    StringComparison.Ordinal) &&
                report.OperationalAssumptions.SequenceEqual(
                    EcdCwrRong2026Baseline.DescribeAssumptions(options),
                    StringComparer.Ordinal);
            var ready = report.FullCoveragePassed &&
                report.ExecutionPassed &&
                report.FailedItems == 0 &&
                countMatches &&
                policyReady &&
                predictionReadiness.Ready;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                ready,
                $"coverage={report.AnalyzedItems}/{report.ManifestWorkItemCount}, failed={report.FailedItems}, policy={report.PolicyVersion}, predictions={predictionReadiness.Detail}");
        });
    }

    private static EcdCwrArtifactReadiness InspectReconstructionReferences(
        string path,
        int? manifestScenarioCount)
    {
        return InspectJsonArray<EcdCwrReconstructionResultReference>(path, references =>
        {
            var unique = references
                .Select(item => item.ScenarioId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var countReady = manifestScenarioCount is not { } count || unique >= count;
            var policyReady = references.Length > 0 && references.All(reference => string.Equals(
                reference.DiagnosticPolicyVersion,
                EcdCwrDiagnosticPolicy.CurrentVersion,
                StringComparison.Ordinal));
            var ready = countReady && policyReady;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                ready,
                $"scenario_refs={unique}/{manifestScenarioCount?.ToString() ?? "unknown"}, rows={references.Length}, policy={PolicyDetail(references.Select(item => item.DiagnosticPolicyVersion), EcdCwrDiagnosticPolicy.CurrentVersion)}");
        });
    }

    private static EcdCwrArtifactReadiness InspectRongReconstructionReferences(
        string path,
        string manifestPath)
    {
        return InspectJsonArray<EcdCwrReconstructionResultReference>(path, references =>
        {
            var expected = TryReadManifestFiniteContactScenarioCount(manifestPath);
            var unique = references
                .Select(reference => reference.ScenarioId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var methodReady = references.Length > 0 && references.All(reference =>
                string.Equals(
                    reference.Method,
                    EcdCwrReconstructionMethods.Rong2026TemplateReplacement,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    reference.DiagnosticPolicyVersion,
                    EcdCwrDiagnosticPolicy.CurrentVersion,
                    StringComparison.Ordinal) &&
                reference.MethodPolicyVersion?.StartsWith(
                    "rong2026-reproduction-v1:",
                    StringComparison.Ordinal) == true);
            var countReady = expected is not { } expectedCount ||
                unique == expectedCount && references.Length == expectedCount;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                countReady && methodReady,
                $"rong_refs={unique}/{expected?.ToString() ?? "unknown"}, rows={references.Length}, method_policy={methodReady}");
        });
    }

    private static EcdCwrArtifactReadiness InspectReconstructionComparisons(
        string path,
        string manifestPath,
        int? manifestScenarioCount)
    {
        return InspectJsonArray<EcdCwrReconstructionComparison>(path, comparisons =>
        {
            var expected = TryReadManifestNonUniformScenarioCount(manifestPath) is { } nonUniformCount &&
                TryReadManifestFiniteContactScenarioCount(manifestPath) is { } finiteContactCount
                ? (nonUniformCount * EcdCwrReconstructionMethods.All.Length) + finiteContactCount
                : manifestScenarioCount is { } count
                    ? count * EcdCwrReconstructionMethods.All.Length
                    : (int?)null;
            var uniquePairs = comparisons
                .Select(item => $"{item.ScenarioId}::{item.Method}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var countReady = expected is not { } expectedPairs || uniquePairs >= expectedPairs;
            var policyReady = comparisons.Length > 0 && comparisons.All(comparison => string.Equals(
                comparison.DiagnosticPolicyVersion,
                EcdCwrDiagnosticPolicy.CurrentVersion,
                StringComparison.Ordinal));
            var rongComparisons = comparisons
                .Where(comparison => string.Equals(
                    comparison.Method,
                    EcdCwrReconstructionMethods.Rong2026TemplateReplacement,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var rongPolicyReady = rongComparisons.Length > 0 && rongComparisons.All(comparison =>
                comparison.MethodPolicyVersion?.StartsWith(
                    "rong2026-reproduction-v1:",
                    StringComparison.Ordinal) == true);
            var ready = countReady && policyReady && rongPolicyReady;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                ready,
                $"method_pairs={uniquePairs}/{expected?.ToString() ?? "unknown"}, policy={PolicyDetail(comparisons.Select(item => item.DiagnosticPolicyVersion), EcdCwrDiagnosticPolicy.CurrentVersion)}, rong_method_policy={rongPolicyReady}");
        });
    }

    private static int? TryReadManifestNonUniformScenarioCount(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<EcdCwrSimulationBatchManifest>(
                File.ReadAllText(path),
                JsonOptions);
            return manifest?.WorkItems.Count(item => item.Scenario.TargetCount > 0);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryReadManifestFiniteContactScenarioCount(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<EcdCwrSimulationBatchManifest>(
                File.ReadAllText(path),
                JsonOptions);
            return manifest?.WorkItems.Count(item =>
                item.Scenario.TargetCount > 0 &&
                (item.Scenario.FaultMode is EcdCwrFaultMode.Single
                    or EcdCwrFaultMode.AdjacentDual
                    or EcdCwrFaultMode.RemoteDual
                    or EcdCwrFaultMode.Triple) &&
                double.IsFinite(item.Scenario.ContactImpedance.Multiplier) &&
                item.Scenario.ContactImpedance.Multiplier > 1.0);
        }
        catch
        {
            return null;
        }
    }

    private static EcdCwrArtifactReadiness InspectScore(
        string path,
        int? manifestScenarioCount)
    {
        return InspectJson<EcdCwrSimulationScoreReport>(path, report =>
        {
            var countMatches = manifestScenarioCount is null ||
                report.WorkItemCount == manifestScenarioCount.Value;
            return new EcdCwrArtifactReadiness(
                Exists: true,
                report.CoverageComplete && countMatches &&
                    string.Equals(report.DiagnosticPolicyVersion, EcdCwrDiagnosticPolicy.CurrentVersion, StringComparison.Ordinal) &&
                    string.Equals(report.BaselineDiagnosticPolicyVersion, EcdCwrDiagnosticPolicy.P2BaselineVersion, StringComparison.Ordinal) &&
                    string.Equals(report.ReconstructionDiagnosticPolicyVersion, EcdCwrDiagnosticPolicy.CurrentVersion, StringComparison.Ordinal),
                $"predictions={report.PredictionCount}/{report.WorkItemCount}, missing={report.MissingPredictionCount}, policy={report.DiagnosticPolicyVersion ?? "missing"}, baseline_policy={report.BaselineDiagnosticPolicyVersion ?? "missing"}, reconstruction_policy={report.ReconstructionDiagnosticPolicyVersion ?? "missing"}");
        });
    }

    private static string PolicyDetail(IEnumerable<string?> versions, string expected)
    {
        var actual = versions.Distinct(StringComparer.Ordinal).ToArray();
        return actual.Length == 1 && string.Equals(actual[0], expected, StringComparison.Ordinal)
            ? expected
            : $"stale[{string.Join(",", actual.Select(value => value ?? "missing"))}] expected={expected}";
    }

    private static EcdCwrArtifactReadiness InspectValidationReport(string path)
    {
        return InspectJson<EcdCwrValidationReport>(path, report =>
            new EcdCwrArtifactReadiness(
                Exists: true,
                report.OverallStatus == EcdCwrValidationStatus.Passed,
                $"overall={report.OverallStatus}, p4.1={report.P41SimulationMatrixStatus}, p4.2={report.P42HardwareChecklistStatus}"));
    }

    private static int? TryReadManifestScenarioCount(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EcdCwrSimulationBatchManifest>(
                File.ReadAllText(path),
                JsonOptions)?.ScenarioCount;
        }
        catch
        {
            return null;
        }
    }

    private static EcdCwrArtifactReadiness InspectJson<T>(
        string path,
        Func<T, EcdCwrArtifactReadiness> inspect)
    {
        if (!File.Exists(path))
        {
            return new EcdCwrArtifactReadiness(false, false, "missing");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            return value is null
                ? new EcdCwrArtifactReadiness(true, false, "empty or invalid JSON")
                : inspect(value);
        }
        catch (Exception ex)
        {
            return new EcdCwrArtifactReadiness(true, false, $"invalid JSON: {ex.Message}");
        }
    }

    private static EcdCwrArtifactReadiness InspectJsonArray<T>(
        string path,
        Func<T[], EcdCwrArtifactReadiness> inspect)
    {
        if (!File.Exists(path))
        {
            return new EcdCwrArtifactReadiness(false, false, "missing");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T[]>(File.ReadAllText(path), JsonOptions);
            return value is null
                ? new EcdCwrArtifactReadiness(true, false, "empty or invalid JSON")
                : inspect(value);
        }
        catch (Exception ex)
        {
            return new EcdCwrArtifactReadiness(true, false, $"invalid JSON: {ex.Message}");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

public sealed record EcdCwrSimulationAcceptancePaths(
    string OutputDirectory,
    string ManifestPath,
    string DatasetValidationPath,
    string BaselinePredictionsPath,
    string PredictionsPath,
    string RongBaselinePredictionsPath,
    string RongBaselineReportPath,
    string ReconstructionReferencesPath,
    string RongReconstructionReferencesPath,
    string ReconstructionComparisonPath,
    string ScorePath,
    string ValidationReportPath)
{
    public static EcdCwrSimulationAcceptancePaths CreateDefault(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        return new EcdCwrSimulationAcceptancePaths(
            root,
            Path.Combine(root, "ecd-cwr-simulation-batch.json"),
            Path.Combine(root, "ecd-cwr-simulation-validation.json"),
            Path.Combine(root, "p2-baseline-predictions.json"),
            Path.Combine(root, "predictions.json"),
            Path.Combine(root, "rong2026-baseline-predictions.json"),
            Path.Combine(root, "rong2026-baseline-report.json"),
            Path.Combine(root, "reconstruction-results.json"),
            Path.Combine(root, "rong2026-reconstruction-results.json"),
            Path.Combine(root, "reconstruction-cc.json"),
            Path.Combine(root, "ecd-cwr-simulation-score.json"),
            Path.Combine(root, "ecd-cwr-validation-report.json"));
    }
}

public sealed record EcdCwrSimulationAcceptanceStatusReport(
    DateTimeOffset GeneratedAt,
    string OutputDirectory,
    EcdCwrSimulationAcceptancePaths Paths,
    IReadOnlyList<EcdCwrSimulationAcceptanceStep> Steps,
    string? NextCommand,
    bool Complete);

public sealed record EcdCwrSimulationAcceptanceStep(
    string Id,
    string Name,
    string ArtifactPath,
    bool Exists,
    bool Ready,
    string Detail,
    string Command);

internal sealed record EcdCwrArtifactReadiness(
    bool Exists,
    bool Ready,
    string Detail);
