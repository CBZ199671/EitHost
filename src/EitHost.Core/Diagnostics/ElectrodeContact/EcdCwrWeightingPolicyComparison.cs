namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrWeightingPolicyComparisonBuilder
{
    public EcdCwrWeightingPolicyComparisonReport Build(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        IReadOnlyList<EcdCwrSimulationPrediction> predictions,
        IReadOnlyList<EcdCwrReconstructionComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(comparisons);
        var predictionByScenario = predictions
            .GroupBy(prediction => prediction.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var scope = workItems
            .Where(IsFiniteContactReconstructionScenario)
            .Where(item => predictionByScenario.ContainsKey(item.ScenarioId))
            .ToArray();
        var scopedIds = scope
            .Select(item => item.ScenarioId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comparisonByKey = comparisons
            .Where(comparison => scopedIds.Contains(comparison.ScenarioId))
            .Where(comparison => EcdCwrReconstructionMethods.WeightingComparison.Contains(
                comparison.Method,
                StringComparer.OrdinalIgnoreCase))
            .GroupBy(
                comparison => $"{comparison.ScenarioId}|{comparison.Method}",
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var completeIds = scope
            .Where(item => EcdCwrReconstructionMethods.WeightingComparison.All(method =>
                comparisonByKey.ContainsKey($"{item.ScenarioId}|{method}")))
            .Select(item => item.ScenarioId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var yellowIds = completeIds
            .Where(id => IsYellowOnly(predictionByScenario[id]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = EcdCwrReconstructionMethods.WeightingComparison
            .Select(method => BuildRow(
                method,
                completeIds,
                yellowIds,
                predictionByScenario,
                comparisonByKey))
            .ToArray();
        var byMethod = rows.ToDictionary(row => row.Method, StringComparer.OrdinalIgnoreCase);
        var continuous = byMethod[EcdCwrReconstructionMethods.ContaminationAwareWeighted];
        var binary = byMethod[EcdCwrReconstructionMethods.BinaryWeighted];
        var allOne = byMethod[EcdCwrReconstructionMethods.AllOne];
        var diagnosticVersions = comparisons
            .Where(comparison => completeIds.Contains(comparison.ScenarioId))
            .Select(comparison => comparison.DiagnosticPolicyVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new EcdCwrWeightingPolicyComparisonReport(
            DateTimeOffset.Now,
            workItems.Count,
            scope.Length,
            completeIds.Count,
            yellowIds.Count,
            rows,
            diagnosticVersions.Length == 1 ? diagnosticVersions[0] : null,
            ContinuousCcAtLeastBinary: continuous.MeanCorrelationCoefficient + 1.0e-12 >=
                binary.MeanCorrelationCoefficient,
            ContinuousCcAtLeastAllOne: continuous.MeanCorrelationCoefficient + 1.0e-12 >=
                allOne.MeanCorrelationCoefficient,
            HealthyMeasurementsPreserved: continuous.YellowUnaffectedMeasurementLoss <= 1.0e-12 &&
                binary.YellowUnaffectedMeasurementLoss <= 1.0e-12);
    }

    private static EcdCwrWeightingPolicyComparisonRow BuildRow(
        string method,
        IReadOnlySet<string> completeIds,
        IReadOnlySet<string> yellowIds,
        IReadOnlyDictionary<string, EcdCwrSimulationPrediction> predictionByScenario,
        IReadOnlyDictionary<string, EcdCwrReconstructionComparison> comparisonByKey)
    {
        var allCc = completeIds
            .Select(id => comparisonByKey[$"{id}|{method}"].CorrelationCoefficient)
            .ToArray();
        var yellowCc = yellowIds
            .Select(id => comparisonByKey[$"{id}|{method}"].CorrelationCoefficient)
            .ToArray();
        var yellowWeightLoss = new List<double>(yellowIds.Count);
        var yellowUnaffectedLoss = new List<double>(yellowIds.Count);
        foreach (var id in yellowIds)
        {
            var prediction = predictionByScenario[id];
            var weights = WeightsForMethod(method, prediction);
            yellowWeightLoss.Add(1.0 - weights.Average());
            yellowUnaffectedLoss.Add(CalculateUnaffectedMeasurementLoss(weights, prediction.States));
        }

        return new EcdCwrWeightingPolicyComparisonRow(
            method,
            PolicyVersion(method),
            allCc.Length,
            allCc.Length == 0 ? double.NaN : allCc.Average(),
            yellowCc.Length,
            yellowCc.Length == 0 ? double.NaN : yellowCc.Average(),
            yellowWeightLoss.Count == 0 ? double.NaN : yellowWeightLoss.Average(),
            yellowUnaffectedLoss.Count == 0 ? double.NaN : yellowUnaffectedLoss.Average());
    }

    private static double[] WeightsForMethod(
        string method,
        EcdCwrSimulationPrediction prediction)
    {
        return method switch
        {
            EcdCwrReconstructionMethods.ContaminationAwareWeighted =>
                new EcdCwrContaminationAwareWeightMapper().Map(
                    NormalizeScores(prediction),
                    prediction.CandidateEvidenceKinds,
                    prediction.FaultTypes),
            EcdCwrReconstructionMethods.BinaryWeighted => new EcdCwrBinaryWeightMapper().Map(
                prediction.States),
            EcdCwrReconstructionMethods.AllOne => Enumerable.Repeat(1.0, 208).ToArray(),
            _ => throw new ArgumentException($"Unsupported weighting method '{method}'.", nameof(method))
        };
    }

    private static string PolicyVersion(string method)
    {
        return method switch
        {
            EcdCwrReconstructionMethods.ContaminationAwareWeighted =>
                EcdCwrContaminationAwareWeightMapper.CreatePolicyVersion(
                new EcdCwrContinuousWeightMapperOptions()),
            EcdCwrReconstructionMethods.BinaryWeighted => EcdCwrBinaryWeightMapper.CreatePolicyVersion(
                new EcdCwrBinaryWeightMapperOptions()),
            EcdCwrReconstructionMethods.AllOne => "ecd-cwr-all-one-v1",
            _ => throw new ArgumentException($"Unsupported weighting method '{method}'.", nameof(method))
        };
    }

    private static double[] NormalizeScores(EcdCwrSimulationPrediction prediction)
    {
        return prediction.Scores is { Count: 16 } scores
            ? scores.Select(score => double.IsFinite(score) ? Math.Max(0.0, score) : 0.0).ToArray()
            : new double[16];
    }

    private static double CalculateUnaffectedMeasurementLoss(
        IReadOnlyList<double> weights,
        IReadOnlyList<ElectrodeContactState>? states)
    {
        if (states is not { Count: 16 } || weights.Count != 208)
        {
            return double.NaN;
        }

        var losses = new List<double>();
        var offset = 0;
        for (var stimulation = 0; stimulation < 16; stimulation++)
        {
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                var measurement = Mod(stimulation + relativeChannel);
                var involved = new[]
                {
                    stimulation,
                    Mod(stimulation + 1),
                    measurement,
                    Mod(measurement + 1)
                };
                if (involved.All(electrode => states[electrode] == ElectrodeContactState.Green))
                {
                    losses.Add(1.0 - weights[offset]);
                }

                offset++;
            }
        }

        return losses.Count == 0 ? 0.0 : losses.Average();
    }

    private static bool IsYellowOnly(EcdCwrSimulationPrediction prediction)
    {
        return prediction.States is { Count: 16 } states &&
            states.Contains(ElectrodeContactState.Yellow) &&
            states.All(state => state is ElectrodeContactState.Green or ElectrodeContactState.Yellow) &&
            !prediction.SystemLevel;
    }

    private static bool IsFiniteContactReconstructionScenario(EcdCwrSimulationWorkItem item)
    {
        return item.Scenario.TargetCount > 0 &&
            (item.Scenario.FaultMode is EcdCwrFaultMode.Single
                or EcdCwrFaultMode.AdjacentDual
                or EcdCwrFaultMode.RemoteDual
                or EcdCwrFaultMode.Triple) &&
            double.IsFinite(item.Scenario.ContactImpedance.Multiplier) &&
            item.Scenario.ContactImpedance.Multiplier > 1.0;
    }

    private static int Mod(int value)
    {
        var result = value % 16;
        return result < 0 ? result + 16 : result;
    }
}

public sealed record EcdCwrWeightingPolicyComparisonRow(
    string Method,
    string WeightPolicyVersion,
    int ScenarioCount,
    double MeanCorrelationCoefficient,
    int YellowScenarioCount,
    double YellowMeanCorrelationCoefficient,
    double YellowMeasurementWeightLoss,
    double YellowUnaffectedMeasurementLoss);

public sealed record EcdCwrWeightingPolicyComparisonReport(
    DateTimeOffset GeneratedAt,
    int ManifestScenarioCount,
    int ExpectedFiniteContactScenarioCount,
    int CompleteScenarioCount,
    int YellowScenarioCount,
    IReadOnlyList<EcdCwrWeightingPolicyComparisonRow> Rows,
    string? DiagnosticPolicyVersion,
    bool ContinuousCcAtLeastBinary,
    bool ContinuousCcAtLeastAllOne,
    bool HealthyMeasurementsPreserved)
{
    public bool CoverageComplete =>
        ExpectedFiniteContactScenarioCount > 0 &&
        CompleteScenarioCount == ExpectedFiniteContactScenarioCount &&
        Rows.All(row => row.ScenarioCount == CompleteScenarioCount);

    public bool Passed =>
        CoverageComplete &&
        YellowScenarioCount > 0 &&
        ContinuousCcAtLeastBinary &&
        ContinuousCcAtLeastAllOne &&
        HealthyMeasurementsPreserved;
}

public static class EcdCwrWeightingPolicyComparisonFormatter
{
    public static string ToMarkdown(EcdCwrWeightingPolicyComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Weighting Policy Comparison",
            "",
            $"- Generated at: {report.GeneratedAt:O}",
            $"- Diagnostic policy: {report.DiagnosticPolicyVersion ?? "mixed-or-missing"}",
            $"- Manifest scenarios: {report.ManifestScenarioCount}",
            $"- Expected finite-contact scenarios: {report.ExpectedFiniteContactScenarioCount}",
            $"- Complete same-set scenarios: {report.CompleteScenarioCount}",
            $"- Yellow-only scenarios: {report.YellowScenarioCount}",
            $"- Continuous CC >= binary: {report.ContinuousCcAtLeastBinary}",
            $"- Continuous CC >= all-one: {report.ContinuousCcAtLeastAllOne}",
            $"- Unaffected healthy measurements preserved: {report.HealthyMeasurementsPreserved}",
            $"- Passed: {report.Passed}",
            "",
            "|method|policy|scenarios|mean CC|yellow scenarios|yellow mean CC|yellow weight loss|unaffected loss|",
            "|---|---|---:|---:|---:|---:|---:|---:|"
        };
        foreach (var row in report.Rows)
        {
            lines.Add(
                $"|{row.Method}|{row.WeightPolicyVersion}|{row.ScenarioCount}|{row.MeanCorrelationCoefficient:F6}|{row.YellowScenarioCount}|{row.YellowMeanCorrelationCoefficient:F6}|{row.YellowMeasurementWeightLoss:P4}|{row.YellowUnaffectedMeasurementLoss:P6}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
