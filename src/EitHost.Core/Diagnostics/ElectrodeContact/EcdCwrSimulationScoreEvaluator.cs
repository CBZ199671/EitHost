namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationScoreEvaluator
{
    public EcdCwrSimulationScoreReport Evaluate(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        IReadOnlyList<EcdCwrSimulationPrediction> predictions,
        IReadOnlyList<EcdCwrReconstructionComparison>? reconstructionComparisons = null,
        IReadOnlyList<EcdCwrSimulationPrediction>? baselinePredictions = null)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(predictions);
        var diagnosticPolicyVersion = SinglePolicyVersion(predictions);
        var baselinePolicyVersion = SinglePolicyVersion(baselinePredictions ?? []);
        var predictionByScenario = predictions
            .GroupBy(item => item.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var items = workItems
            .Select(item => ScoreItem(item, predictionByScenario.GetValueOrDefault(item.ScenarioId)))
            .ToArray();
        var healthy = items.Where(item => item.ExpectedFaultClass == EcdCwrFaultClass.None).ToArray();
        var healthyBoundaryHigh = items
            .Where(item =>
                item.ExpectedFaultClass == EcdCwrFaultClass.None &&
                item.TargetPlacement == EcdCwrTargetPlacement.Boundary &&
                item.ConductivityPattern == EcdCwrConductivityPattern.High)
            .ToArray();
        var singleZc20OrWorse = items
            .Where(item =>
                item.TruthFaultMode == EcdCwrFaultMode.Single &&
                item.ContactImpedanceMultiplier >= 20.0)
            .ToArray();
        var adjacentDual = items
            .Where(item =>
                item.TruthFaultMode == EcdCwrFaultMode.AdjacentDual &&
                item.ExpectedFaultClass == EcdCwrFaultClass.ElectrodeContact)
            .ToArray();
        var typeClassified = items
            .Where(item => item.ExpectedFaultClass != EcdCwrFaultClass.NotApplicable)
            .ToArray();
        var contactSubspaceAuc = ContactSubspaceAuc(items);
        var contactSubspaceScored = ContactSubspaceScoredCount(items);
        var imageQualityScores = items
            .Select(item => item.ImageQualityScore)
            .Where(score => score is not null && double.IsFinite(score.Value))
            .Select(score => score!.Value)
            .ToArray();
        var comparisonList = (reconstructionComparisons ?? [])
            .Where(comparison => string.Equals(
                comparison.DiagnosticPolicyVersion,
                diagnosticPolicyVersion,
                StringComparison.Ordinal))
            .ToArray();
        var reconstructionPolicyVersion = SinglePolicyVersion(comparisonList);
        var imageQualityCorrelation = ImageQualityWeightedCcSpearman(items, comparisonList);
        var reconstruction = SummarizeReconstruction(comparisonList, items);
        var multiFrequencyImprovement = BuildMultiFrequencyImprovement(
            workItems,
            items,
            diagnosticPolicyVersion is null || string.Equals(
                baselinePolicyVersion,
                EcdCwrDiagnosticPolicy.P2BaselineVersion,
                StringComparison.Ordinal)
                ? baselinePredictions
                : null);

        return new EcdCwrSimulationScoreReport(
            DateTimeOffset.Now,
            workItems.Count,
            predictions.Select(item => item.ScenarioId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            items.Count(item => !item.HasPrediction),
            Rate(healthy.Count(item => item.FalseRed), healthy.Length),
            Rate(healthyBoundaryHigh.Count(item => item.FalseRed), healthyBoundaryHigh.Length),
            Rate(singleZc20OrWorse.Count(item => item.Top1Correct), singleZc20OrWorse.Length),
            Rate(adjacentDual.Count(item => item.AdjacentDualSeparated), adjacentDual.Length),
            Rate(typeClassified.Count(item => item.FaultTypeCorrect), typeClassified.Length),
            contactSubspaceAuc,
            contactSubspaceScored,
            imageQualityScores.Length == 0 ? null : imageQualityScores.Average(),
            items.Count(item => item.LowImageQuality),
            imageQualityCorrelation.Spearman,
            imageQualityCorrelation.PairCount,
            reconstruction,
            items,
            multiFrequencyImprovement,
            diagnosticPolicyVersion,
            baselinePolicyVersion,
            reconstructionPolicyVersion);
    }

    private static string? SinglePolicyVersion(
        IEnumerable<EcdCwrSimulationPrediction> predictions)
    {
        var versions = predictions
            .Select(prediction => prediction.DiagnosticPolicyVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return versions.Length == 1 ? versions[0] : null;
    }

    private static string? SinglePolicyVersion(
        IEnumerable<EcdCwrReconstructionComparison> comparisons)
    {
        var versions = comparisons
            .Select(comparison => comparison.DiagnosticPolicyVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return versions.Length == 1 ? versions[0] : null;
    }

    public static string ToMarkdown(EcdCwrSimulationScoreReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR P2 Simulation Score",
            "",
            $"- Scored at: {report.ScoredAt:O}",
            $"- Work items: {report.WorkItemCount}",
            $"- Predictions: {report.PredictionCount}",
            $"- Missing predictions: {report.MissingPredictionCount}",
            $"- Healthy false red rate: {report.HealthyFalseRedRate:P4}",
            $"- Healthy boundary high false red rate: {report.HealthyBoundaryHighFalseRedRate:P4}",
            $"- Single-electrode top-1 accuracy (zc x20+): {report.SingleElectrodeTop1Accuracy:P4}",
            $"- Adjacent-dual separation rate: {report.AdjacentDualSeparationRate:P4}",
            $"- Fault-type accuracy: {report.FaultTypeAccuracy:P4}",
            $"- Contact-subspace AUC: {FormatOptional(report.ContactSubspaceAuc)} ({report.ContactSubspaceScoredCount} scored)",
            $"- Mean image quality: {FormatOptional(report.MeanImageQuality)} ({report.LowImageQualityCount} low-confidence)",
            $"- Image quality weighted-CC Spearman: {FormatOptional(report.ImageQualityWeightedCcSpearman)} ({report.ImageQualityWeightedCcPairCount} pairs)",
            $"- Multi-frequency false-red reduction: {FormatOptional(report.MultiFrequencyFalseRedImprovement?.RelativeReduction)} ({report.MultiFrequencyFalseRedImprovement?.ComparedScenarioCount ?? 0} compared)",
            $"- Reconstruction comparison ready: {report.ReconstructionComparison.Ready}",
            ""
        };
        if (report.ReconstructionComparison.Methods.Count > 0)
        {
            lines.Add("## Reconstruction CC");
            lines.Add("");
            lines.Add("|method|count|mean_cc|");
            lines.Add("|---|---:|---:|");
            foreach (var method in report.ReconstructionComparison.Methods)
            {
                lines.Add($"|{method.Method}|{method.Count}|{method.MeanCorrelation:F6}|");
            }
            lines.Add("");
        }

        lines.Add("## Failed Items");
        lines.Add("");
        lines.Add("|scenario|reason|truth|predicted_top1|predicted_class|");
        lines.Add("|---|---|---|---:|---|");
        foreach (var item in report.Items.Where(item => item.Issues.Count > 0).Take(200))
        {
            lines.Add(
                $"|{item.ScenarioId}|{string.Join("<br>", item.Issues)}|{item.TruthFaultMode}|{item.PredictedTop1Electrode}|{item.PredictedFaultClass}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static EcdCwrSimulationScoreItem ScoreItem(
        EcdCwrSimulationWorkItem workItem,
        EcdCwrSimulationPrediction? prediction)
    {
        var scenario = workItem.Scenario;
        var issues = new List<string>();
        if (prediction is null)
        {
            issues.Add("missing prediction");
        }
        else
        {
            if (NormalizedStates(prediction).Count != 16)
            {
                issues.Add("prediction states length is not 16");
            }

            if (NormalizedFaultTypes(prediction).Count != 16)
            {
                issues.Add("prediction fault_types length is not 16");
            }

            if (NormalizedScores(prediction).Count != 16)
            {
                issues.Add("prediction scores length is not 16");
            }
        }

        var predictedClass = prediction is null
            ? EcdCwrFaultClass.NotApplicable
            : ClassifyPrediction(prediction);
        var effectiveContactFault = HasEffectiveContactFault(scenario);
        var expectedClass = ExpectedClass(scenario);
        var top1 = prediction is null ? -1 : TopElectrodes(prediction, 1).FirstOrDefault(-1);
        var top2 = prediction is null ? Array.Empty<int>() : TopElectrodes(prediction, 2);
        var falseRed = prediction is not null && HasRedLike(prediction);
        var top1Correct = effectiveContactFault &&
            scenario.FaultElectrodes.Count == 1 &&
            top1 == scenario.FaultElectrodes[0];
        var adjacentDualSeparated = effectiveContactFault &&
            scenario.FaultElectrodes.Count == 2 &&
            top2.Length == 2 &&
            top2.Order().SequenceEqual(scenario.FaultElectrodes.Order());
        var faultTypeCorrect = IsFaultTypeCorrect(scenario, expectedClass, predictedClass);

        if (!effectiveContactFault && falseRed)
        {
            issues.Add("false red on healthy scenario");
        }

        if (effectiveContactFault &&
            scenario.FaultMode == EcdCwrFaultMode.Single &&
            scenario.ContactImpedance.Multiplier >= 20.0 &&
            !top1Correct)
        {
            issues.Add("single-electrode top-1 mismatch");
        }

        if (effectiveContactFault &&
            scenario.FaultMode == EcdCwrFaultMode.AdjacentDual &&
            !adjacentDualSeparated)
        {
            issues.Add("adjacent-dual separation mismatch");
        }

        if (expectedClass != EcdCwrFaultClass.NotApplicable && !faultTypeCorrect)
        {
            issues.Add("fault type mismatch");
        }

        return new EcdCwrSimulationScoreItem(
            scenario.ScenarioId,
            prediction is not null,
            scenario.FaultMode,
            scenario.TargetPlacement,
            scenario.ConductivityPattern,
            scenario.ContactImpedance.Multiplier,
            scenario.FaultElectrodes.ToArray(),
            expectedClass,
            predictedClass,
            top1,
            prediction?.ContactSubspaceScore,
            ContactSubspaceDiscriminantScore(prediction),
            prediction?.ImageQualityScore,
            prediction?.ImageQualityScore is { } imageQuality &&
                double.IsFinite(imageQuality) &&
                imageQuality < 0.5,
            falseRed,
            top1Correct,
            adjacentDualSeparated,
            faultTypeCorrect,
            issues);
    }

    private static bool HasEffectiveContactFault(EcdCwrSimulationScenario scenario)
    {
        return scenario.FaultMode != EcdCwrFaultMode.None &&
            (double.IsPositiveInfinity(scenario.ContactImpedance.Multiplier) ||
                scenario.ContactImpedance.Multiplier > 1.0);
    }

    private static bool IsFaultTypeCorrect(
        EcdCwrSimulationScenario scenario,
        EcdCwrFaultClass expectedClass,
        EcdCwrFaultClass predictedClass)
    {
        if (expectedClass == EcdCwrFaultClass.NotApplicable)
        {
            return false;
        }

        if (double.IsPositiveInfinity(scenario.ContactImpedance.Multiplier) &&
            scenario.FaultMode is EcdCwrFaultMode.Single
                or EcdCwrFaultMode.AdjacentDual
                or EcdCwrFaultMode.RemoteDual
                or EcdCwrFaultMode.Triple)
        {
            return predictedClass is EcdCwrFaultClass.ElectrodeContact or EcdCwrFaultClass.SystemLevel;
        }

        return predictedClass == expectedClass;
    }

    private static EcdCwrFaultClass ExpectedClass(EcdCwrSimulationScenario scenario)
    {
        if (!HasEffectiveContactFault(scenario))
        {
            return EcdCwrFaultClass.None;
        }

        return scenario.FaultMode switch
        {
            EcdCwrFaultMode.Global => EcdCwrFaultClass.SystemLevel,
            EcdCwrFaultMode.Single or EcdCwrFaultMode.AdjacentDual or EcdCwrFaultMode.RemoteDual or EcdCwrFaultMode.Triple => EcdCwrFaultClass.ElectrodeContact,
            _ => EcdCwrFaultClass.NotApplicable
        };
    }

    private static EcdCwrFaultClass ClassifyPrediction(EcdCwrSimulationPrediction prediction)
    {
        var states = NormalizedStates(prediction);
        var faultTypes = NormalizedFaultTypes(prediction);
        if (prediction.SystemLevel ||
            states.Any(state => state == ElectrodeContactState.SystemLevel) ||
            faultTypes.Any(type => type == ElectrodeFaultType.SystemLevel))
        {
            return EcdCwrFaultClass.SystemLevel;
        }

        if (HasRedLike(prediction) ||
            faultTypes.Any(type => type == ElectrodeFaultType.ElectrodeContact))
        {
            return EcdCwrFaultClass.ElectrodeContact;
        }

        if (faultTypes.Any(type => type == ElectrodeFaultType.DrivePairLink))
        {
            return EcdCwrFaultClass.DrivePairLink;
        }

        if (faultTypes.Any(type => type == ElectrodeFaultType.AcquisitionChannel))
        {
            return EcdCwrFaultClass.AcquisitionChannel;
        }

        if (faultTypes.Any(type => type == ElectrodeFaultType.UncertainStructured))
        {
            return EcdCwrFaultClass.UncertainStructured;
        }

        return EcdCwrFaultClass.None;
    }

    private static bool HasRedLike(EcdCwrSimulationPrediction prediction)
    {
        var states = NormalizedStates(prediction);
        return prediction.SystemLevel ||
            states.Any(state => state is ElectrodeContactState.Red or ElectrodeContactState.DarkRed or ElectrodeContactState.SystemLevel);
    }

    private static int[] TopElectrodes(EcdCwrSimulationPrediction prediction, int count)
    {
        var states = NormalizedStates(prediction);
        var rawScores = NormalizedScores(prediction);
        var scores = rawScores.Count == 16
            ? rawScores
            : Enumerable.Range(0, 16)
                .Select(index => states.Count > index ? StateScore(states[index]) : 0.0)
                .ToArray();
        var ordered = scores
            .Select((score, electrode) => new { score, electrode })
            .Where(item => double.IsFinite(item.score) && item.score > 0.0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.electrode)
            .ToArray();
        if (count <= 0 || ordered.Length < count)
        {
            return [];
        }

        var cutoff = ordered[count - 1].score;
        if (ordered.Skip(count).Any(item => NearlyEqual(item.score, cutoff)))
        {
            return [];
        }

        return ordered.Take(count).Select(item => item.electrode).ToArray();
    }

    private static bool NearlyEqual(double left, double right)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= 1.0e-9 * scale;
    }

    private static double StateScore(ElectrodeContactState state)
    {
        return state switch
        {
            ElectrodeContactState.DarkRed => 4.0,
            ElectrodeContactState.Red => 3.0,
            ElectrodeContactState.Yellow => 1.0,
            ElectrodeContactState.SystemLevel => 5.0,
            _ => 0.0
        };
    }

    private static IReadOnlyList<ElectrodeContactState> NormalizedStates(EcdCwrSimulationPrediction prediction)
    {
        return prediction.States ?? [];
    }

    private static IReadOnlyList<ElectrodeFaultType> NormalizedFaultTypes(EcdCwrSimulationPrediction prediction)
    {
        return prediction.CandidateFaultTypes is { Count: 16 }
            ? prediction.CandidateFaultTypes
            : prediction.FaultTypes ?? [];
    }

    private static IReadOnlyList<double> NormalizedScores(EcdCwrSimulationPrediction prediction)
    {
        return prediction.CandidateScores is { Count: 16 }
            ? prediction.CandidateScores
            : prediction.Scores ?? [];
    }

    private static double Rate(int numerator, int denominator)
    {
        return denominator <= 0 ? double.NaN : (double)numerator / denominator;
    }

    private static double? ContactSubspaceAuc(IReadOnlyList<EcdCwrSimulationScoreItem> items)
    {
        var positives = items
            .Where(item =>
                item.ExpectedFaultClass == EcdCwrFaultClass.ElectrodeContact &&
                double.IsFinite(item.ContactImpedanceMultiplier))
            .Select(ContactSubspaceDecisionScore)
            .Where(score => score is not null && double.IsFinite(score.Value))
            .Select(score => score!.Value)
            .ToArray();
        var negatives = items
            .Where(item => item.ExpectedFaultClass == EcdCwrFaultClass.None)
            .Select(ContactSubspaceDecisionScore)
            .Where(score => score is not null && double.IsFinite(score.Value))
            .Select(score => score!.Value)
            .ToArray();
        if (positives.Length == 0 || negatives.Length == 0)
        {
            return null;
        }

        var wins = 0.0;
        foreach (var positive in positives)
        {
            foreach (var negative in negatives)
            {
                wins += positive > negative ? 1.0 : positive.Equals(negative) ? 0.5 : 0.0;
            }
        }

        return wins / (positives.Length * negatives.Length);
    }

    private static int ContactSubspaceScoredCount(IReadOnlyList<EcdCwrSimulationScoreItem> items)
    {
        return items.Count(item =>
            IsContactSubspaceAucItem(item) &&
            ContactSubspaceDecisionScore(item) is { } score &&
            double.IsFinite(score));
    }

    private static bool IsContactSubspaceAucItem(EcdCwrSimulationScoreItem item)
    {
        return item.ExpectedFaultClass == EcdCwrFaultClass.None ||
            item.ExpectedFaultClass == EcdCwrFaultClass.ElectrodeContact &&
            double.IsFinite(item.ContactImpedanceMultiplier);
    }

    private static double? ContactSubspaceDecisionScore(EcdCwrSimulationScoreItem item)
    {
        return item.ContactSubspaceDiscriminantScore ?? item.ContactSubspaceScore;
    }

    private static double? ContactSubspaceDiscriminantScore(EcdCwrSimulationPrediction? prediction)
    {
        if (prediction is null)
        {
            return null;
        }

        if (prediction.ContactSubspaceDiscriminantScore is { } explicitScore &&
            double.IsFinite(explicitScore))
        {
            return explicitScore;
        }

        if (prediction.ContactSubspaceScore is not { } projectionRatio ||
            !double.IsFinite(projectionRatio))
        {
            return null;
        }

        var maxScore = NormalizedScores(prediction)
            .Where(score => double.IsFinite(score))
            .Select(score => Math.Max(0.0, score))
            .DefaultIfEmpty(0.0)
            .Max();
        var structuredEvidence = maxScore <= 0.0 ? 0.0 : maxScore / (maxScore + 1.0);
        return Math.Clamp(projectionRatio, 0.0, 1.0) * structuredEvidence;
    }

    private static string FormatOptional(double? value)
    {
        return value is { } finite && double.IsFinite(finite)
            ? finite.ToString("F4")
            : "n/a";
    }

    private static ImageQualityCorrelationSummary ImageQualityWeightedCcSpearman(
        IReadOnlyList<EcdCwrSimulationScoreItem> items,
        IReadOnlyList<EcdCwrReconstructionComparison> comparisons)
    {
        var weightedByScenario = comparisons
            .Where(item =>
                string.Equals(item.Method, EcdCwrReconstructionMethods.Weighted, StringComparison.OrdinalIgnoreCase) &&
                double.IsFinite(item.CorrelationCoefficient))
            .GroupBy(item => item.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
        var pairs = items
            .Where(item => weightedByScenario.ContainsKey(item.ScenarioId))
            .Select(item =>
            {
                var comparison = weightedByScenario[item.ScenarioId];
                var quality = comparison.ImageQualityScore ?? item.ImageQualityScore;
                return (Quality: quality, Cc: comparison.CorrelationCoefficient);
            })
            .Where(item => item.Quality is { } quality && double.IsFinite(quality))
            .Select(item => (Quality: item.Quality!.Value, item.Cc))
            .ToArray();
        if (pairs.Length < 2)
        {
            return new ImageQualityCorrelationSummary(null, pairs.Length);
        }

        var qualityRanks = AverageRanks(pairs.Select(item => item.Quality).ToArray());
        var ccRanks = AverageRanks(pairs.Select(item => item.Cc).ToArray());
        return new ImageQualityCorrelationSummary(Pearson(qualityRanks, ccRanks), pairs.Length);
    }

    private static EcdCwrMultiFrequencyFalseRedImprovement? BuildMultiFrequencyImprovement(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        IReadOnlyList<EcdCwrSimulationScoreItem> currentItems,
        IReadOnlyList<EcdCwrSimulationPrediction>? baselinePredictions)
    {
        if (baselinePredictions is null)
        {
            return null;
        }

        var baselineByScenario = baselinePredictions
            .GroupBy(item => item.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var currentByScenario = currentItems.ToDictionary(item => item.ScenarioId, StringComparer.OrdinalIgnoreCase);
        var comparable = workItems
            .Where(item =>
                item.Scenario.FaultMode == EcdCwrFaultMode.None &&
                item.Scenario.TargetPlacement == EcdCwrTargetPlacement.Boundary &&
                item.Scenario.ConductivityPattern == EcdCwrConductivityPattern.High &&
                baselineByScenario.ContainsKey(item.ScenarioId) &&
                currentByScenario.GetValueOrDefault(item.ScenarioId)?.HasPrediction == true)
            .ToArray();
        if (comparable.Length == 0)
        {
            return null;
        }

        var baselineItems = comparable
            .Select(item => ScoreItem(item, baselineByScenario[item.ScenarioId]))
            .ToArray();
        var currentComparable = comparable
            .Select(item => currentByScenario[item.ScenarioId])
            .ToArray();
        var baselineRate = Rate(baselineItems.Count(item => item.FalseRed), comparable.Length);
        var currentRate = Rate(currentComparable.Count(item => item.FalseRed), comparable.Length);
        var reduction = baselineRate > 0.0 && double.IsFinite(baselineRate)
            ? (baselineRate - currentRate) / baselineRate
            : (double?)null;
        return new EcdCwrMultiFrequencyFalseRedImprovement(
            comparable.Length,
            baselineRate,
            currentRate,
            reduction);
    }

    private static double[] AverageRanks(IReadOnlyList<double> values)
    {
        var ordered = values
            .Select((value, index) => new { value, index })
            .OrderBy(item => item.value)
            .ThenBy(item => item.index)
            .ToArray();
        var ranks = new double[values.Count];
        var start = 0;
        while (start < ordered.Length)
        {
            var end = start + 1;
            while (end < ordered.Length && ordered[end].value.Equals(ordered[start].value))
            {
                end++;
            }

            var rank = (start + 1 + end) / 2.0;
            for (var offset = start; offset < end; offset++)
            {
                ranks[ordered[offset].index] = rank;
            }

            start = end;
        }

        return ranks;
    }

    private static double? Pearson(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return null;
        }

        var leftMean = left.Average();
        var rightMean = right.Average();
        var numerator = 0.0;
        var leftSum = 0.0;
        var rightSum = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            var leftCentered = left[index] - leftMean;
            var rightCentered = right[index] - rightMean;
            numerator += leftCentered * rightCentered;
            leftSum += leftCentered * leftCentered;
            rightSum += rightCentered * rightCentered;
        }

        var denominator = Math.Sqrt(leftSum * rightSum);
        return denominator <= double.Epsilon
            ? null
            : Math.Clamp(numerator / denominator, -1.0, 1.0);
    }

    private static EcdCwrReconstructionComparisonSummary SummarizeReconstruction(
        IReadOnlyList<EcdCwrReconstructionComparison> comparisons,
        IReadOnlyList<EcdCwrSimulationScoreItem> items)
    {
        var compensableScenarioIds = items
            .Where(item =>
                item.ExpectedFaultClass == EcdCwrFaultClass.ElectrodeContact &&
                double.IsFinite(item.ContactImpedanceMultiplier))
            .Select(item => item.ScenarioId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopedComparisons = compensableScenarioIds.Count == 0
            ? comparisons
            : comparisons
                .Where(item => compensableScenarioIds.Contains(item.ScenarioId))
                .ToArray();
        var methods = scopedComparisons
            .GroupBy(item => item.Method, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EcdCwrReconstructionMethodSummary(
                group.Key,
                group.Count(),
                group.Average(item => item.CorrelationCoefficient)))
            .OrderBy(item => item.Method, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var byMethod = methods.ToDictionary(item => item.Method, StringComparer.OrdinalIgnoreCase);
        var ready = byMethod.TryGetValue(EcdCwrReconstructionMethods.Weighted, out var weighted) &&
            EcdCwrReconstructionMethods.RequiredBaselines.All(method =>
                byMethod.TryGetValue(method, out var baseline) &&
                weighted.MeanCorrelation > baseline.MeanCorrelation);
        return new EcdCwrReconstructionComparisonSummary(ready, methods);
    }
}

public static class EcdCwrReconstructionMethods
{
    public const string Weighted = "ecd_cwr_weighted";
    public const string ContaminationAwareWeighted = "ecd_cwr_weighted_v2";
    public const string BinaryWeighted = "ecd_cwr_binary";
    public const string AllOne = "all_one";
    public const string FrameDrop = "cd_frame_drop";
    public const string StaticReplacement = "sr_static_replacement";
    public const string DirectReciprocity = "drm_direct_reciprocity";
    public const string Rong2026TemplateReplacement = "rong2026_template_replacement";

    public static readonly string[] Baselines =
    [
        FrameDrop,
        StaticReplacement,
        DirectReciprocity,
        Rong2026TemplateReplacement
    ];

    public static readonly string[] RequiredBaselines =
    [
        FrameDrop,
        StaticReplacement,
        Rong2026TemplateReplacement
    ];

    public static readonly string[] All =
    [
        Weighted,
        FrameDrop,
        StaticReplacement,
        DirectReciprocity
    ];

    public static readonly string[] WeightingComparison =
    [
        ContaminationAwareWeighted,
        BinaryWeighted,
        AllOne
    ];

    public static readonly string[] Supported = All
        .Concat(WeightingComparison)
        .Append(Rong2026TemplateReplacement)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string Normalize(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var normalized = method.Trim().ToLowerInvariant();
        return Supported.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : throw new ArgumentException($"Unsupported ECD-CWR reconstruction method '{method}'.", nameof(method));
    }
}

public enum EcdCwrFaultClass
{
    NotApplicable = 0,
    None = 1,
    ElectrodeContact = 2,
    DrivePairLink = 3,
    AcquisitionChannel = 4,
    SystemLevel = 5,
    UncertainStructured = 6
}

public sealed record EcdCwrSimulationPrediction(
    string ScenarioId,
    IReadOnlyList<ElectrodeContactState>? States,
    IReadOnlyList<ElectrodeFaultType>? FaultTypes,
    IReadOnlyList<double>? Scores,
    bool SystemLevel = false,
    double? ContactSubspaceScore = null,
    double? ContactSubspaceDiscriminantScore = null,
    double? ContactSubspaceProjectedNorm = null,
    double? ContactSubspaceResidualNorm = null,
    IReadOnlyList<double>? ContactSubspaceCoefficients = null,
    double? ImageQualityScore = null,
    double? MultiFrequencyPrimaryHz = null,
    int MultiFrequencyPeerFrameCount = 0,
    string? DiagnosticPolicyVersion = null,
    IReadOnlyList<double>? CandidateScores = null,
    IReadOnlyList<ElectrodeFaultType>? CandidateFaultTypes = null,
    IReadOnlyList<ElectrodeEvidenceKind>? CandidateEvidenceKinds = null,
    IReadOnlyList<string>? CandidateReasons = null,
    bool PhysicalFieldGuardApplied = false);

public sealed record EcdCwrReconstructionComparison(
    string ScenarioId,
    string Method,
    double CorrelationCoefficient,
    double? VoltageFitResidualNorm = null,
    double? VoltageFitRelativeResidual = null,
    double? VoltageFitCosineSimilarity = null,
    double? ReconstructionConditionNumber = null,
    double? ImageQualityScore = null,
    double? VoltageFitResidualL1Norm = null,
    double? VoltageFitRelativeL1Residual = null,
    double? VoltageFitResidualLinfNorm = null,
    double? VoltageFitMeasuredNorm = null,
    double? VoltageFitSimulatedNorm = null,
    double? VoltageFitR2 = null,
    double? ReconstructionConductivityRange = null,
    string? DiagnosticPolicyVersion = null,
    string? MethodPolicyVersion = null);

public sealed record EcdCwrReconstructionMethodSummary(
    string Method,
    int Count,
    double MeanCorrelation);

public sealed record EcdCwrReconstructionComparisonSummary(
    bool Ready,
    IReadOnlyList<EcdCwrReconstructionMethodSummary> Methods);

public sealed record EcdCwrSimulationScoreReport(
    DateTimeOffset ScoredAt,
    int WorkItemCount,
    int PredictionCount,
    int MissingPredictionCount,
    double HealthyFalseRedRate,
    double HealthyBoundaryHighFalseRedRate,
    double SingleElectrodeTop1Accuracy,
    double AdjacentDualSeparationRate,
    double FaultTypeAccuracy,
    double? ContactSubspaceAuc,
    int ContactSubspaceScoredCount,
    double? MeanImageQuality,
    int LowImageQualityCount,
    double? ImageQualityWeightedCcSpearman,
    int ImageQualityWeightedCcPairCount,
    EcdCwrReconstructionComparisonSummary ReconstructionComparison,
    IReadOnlyList<EcdCwrSimulationScoreItem> Items,
    EcdCwrMultiFrequencyFalseRedImprovement? MultiFrequencyFalseRedImprovement = null,
    string? DiagnosticPolicyVersion = null,
    string? BaselineDiagnosticPolicyVersion = null,
    string? ReconstructionDiagnosticPolicyVersion = null)
{
    public bool CoverageComplete => MissingPredictionCount == 0 && PredictionCount >= WorkItemCount;

    public bool P2TargetsPassed =>
        CoverageComplete &&
        HealthyBoundaryHighFalseRedRate < 0.005 &&
        SingleElectrodeTop1Accuracy >= 0.99 &&
        AdjacentDualSeparationRate >= 0.95 &&
        FaultTypeAccuracy >= 0.90 &&
        ReconstructionComparison.Ready;
}

public sealed record EcdCwrMultiFrequencyFalseRedImprovement(
    int ComparedScenarioCount,
    double BaselineHealthyBoundaryHighFalseRedRate,
    double CurrentHealthyBoundaryHighFalseRedRate,
    double? RelativeReduction);

public sealed record EcdCwrSimulationScoreItem(
    string ScenarioId,
    bool HasPrediction,
    EcdCwrFaultMode TruthFaultMode,
    EcdCwrTargetPlacement TargetPlacement,
    EcdCwrConductivityPattern ConductivityPattern,
    double ContactImpedanceMultiplier,
    IReadOnlyList<int> TruthFaultElectrodes,
    EcdCwrFaultClass ExpectedFaultClass,
    EcdCwrFaultClass PredictedFaultClass,
    int PredictedTop1Electrode,
    double? ContactSubspaceScore,
    double? ContactSubspaceDiscriminantScore,
    double? ImageQualityScore,
    bool LowImageQuality,
    bool FalseRed,
    bool Top1Correct,
    bool AdjacentDualSeparated,
    bool FaultTypeCorrect,
    IReadOnlyList<string> Issues);

internal sealed record ImageQualityCorrelationSummary(
    double? Spearman,
    int PairCount);
