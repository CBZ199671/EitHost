namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrFaultDictionaryPolicyBenchmarkOptions
{
    public int StartIndex { get; init; }

    public int? Limit { get; init; }

    public bool SkipMissingResults { get; init; }

    public double ActiveCoefficientThreshold { get; init; } = 1.0e-6;

    public IReadOnlySet<string> ScenarioIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record EcdCwrFaultDictionaryPolicyBenchmarkRow(
    EcdCwrFaultDictionaryPolicy Policy,
    string PolicyVersion,
    double L1Penalty,
    double GroupPenalty,
    int PredictionCount,
    int MissingPredictionCount,
    double HealthyFalseRedRate,
    double HealthyBoundaryHighFalseRedRate,
    double SingleElectrodeTop1Accuracy,
    double AdjacentDualSeparationRate,
    double FaultTypeAccuracy,
    double MeanActiveCoefficientCount,
    double MeanResidualRms,
    string DiagnosticPolicyVersion);

public sealed record EcdCwrFaultDictionaryPolicyBenchmarkReport(
    DateTimeOffset GeneratedAt,
    int ManifestWorkItemCount,
    int SelectedScenarioCount,
    int SkippedMissingCount,
    int FailedScenarioCount,
    double ActiveCoefficientThreshold,
    string EvidenceContract,
    EcdCwrFaultDictionaryPolicy WinnerPolicy,
    string WinnerPolicyVersion,
    EcdCwrFaultDictionaryPolicy PersistedPolicy,
    string PersistedPolicyVersion,
    IReadOnlyList<EcdCwrFaultDictionaryPolicyBenchmarkRow> Rows)
{
    public bool CoverageComplete =>
        FailedScenarioCount == 0 &&
        Rows.All(row =>
            row.MissingPredictionCount == 0 &&
            row.PredictionCount == SelectedScenarioCount);

    public bool WinnerPersisted => WinnerPolicy == PersistedPolicy;

    public bool Passed => CoverageComplete && WinnerPersisted;
}

public static class EcdCwrFaultDictionaryPolicyBenchmarkFormatter
{
    public static string ToMarkdown(EcdCwrFaultDictionaryPolicyBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Fault Dictionary Policy Benchmark",
            "",
            $"- Generated at: {report.GeneratedAt:O}",
            $"- Manifest scenarios: {report.ManifestWorkItemCount}",
            $"- Selected scenarios: {report.SelectedScenarioCount}",
            $"- Skipped missing: {report.SkippedMissingCount}",
            $"- Failed scenarios: {report.FailedScenarioCount}",
            $"- Evidence contract: {report.EvidenceContract}",
            $"- Active coefficient threshold: {report.ActiveCoefficientThreshold:G6}",
            $"- Winner: {report.WinnerPolicy} ({report.WinnerPolicyVersion})",
            $"- Persisted: {report.PersistedPolicy} ({report.PersistedPolicyVersion})",
            $"- Winner persisted: {report.WinnerPersisted}",
            $"- Passed: {report.Passed}",
            "",
            "|policy|version|L1|group|predictions|missing|healthy false red|boundary false red|single top-1|adjacent dual|fault type|mean active 64|mean residual RMS|",
            "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
        };
        foreach (var row in report.Rows)
        {
            lines.Add(
                $"|{row.Policy}|{row.PolicyVersion}|{row.L1Penalty:G6}|{row.GroupPenalty:G6}|{row.PredictionCount}|{row.MissingPredictionCount}|{row.HealthyFalseRedRate:P4}|{row.HealthyBoundaryHighFalseRedRate:P4}|{row.SingleElectrodeTop1Accuracy:P4}|{row.AdjacentDualSeparationRate:P4}|{row.FaultTypeAccuracy:P4}|{row.MeanActiveCoefficientCount:F4}|{row.MeanResidualRms:G6}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
